using MySqlConnector;
using DbConnect.Models;
using DbConnect.Services;

namespace DbConnect.Tests.ProviderConformance;

/// <summary>Script as CREATE recreates MySQL tables with triggers (§1.6, issue 04).</summary>
public sealed class TableScriptingMySqlTests
{
    private static async Task Execute(string cs, string sql)
    {
        await using var connection = new MySqlConnection(cs);
        await connection.OpenAsync();
        foreach (var statement in SqlBatchParser.SplitStatements(sql, SqlBatchDialect.MySql))
        {
            if (string.IsNullOrWhiteSpace(statement)) continue;
            await using var cmd = new MySqlCommand(statement, connection);
            await cmd.ExecuteNonQueryAsync();
        }
    }

    private static async Task<List<string>> Rows(string cs, string sql)
    {
        await using var connection = new MySqlConnection(cs);
        await connection.OpenAsync();
        await using var cmd = new MySqlCommand(sql, connection);
        var rows = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            rows.Add(string.Join("|", Enumerable.Range(0, reader.FieldCount).Select(i => reader.IsDBNull(i) ? "<null>" : Convert.ToString(reader.GetValue(i)))));
        return rows;
    }

    internal static async Task<List<string>> Definition(string cs, string table) =>
    [
        .. (await Rows(cs, $"SHOW CREATE TABLE `{table}`")).Select(r => System.Text.RegularExpressions.Regex.Replace(r, @" AUTO_INCREMENT=\d+", "")),
        .. await Rows(cs, $"SELECT TRIGGER_NAME, ACTION_TIMING, EVENT_MANIPULATION, ACTION_STATEMENT, ACTION_ORDER FROM INFORMATION_SCHEMA.TRIGGERS WHERE EVENT_OBJECT_SCHEMA = DATABASE() AND EVENT_OBJECT_TABLE = '{table}' ORDER BY TRIGGER_NAME")
    ];

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ScriptedTable_RecreatesDefinitionAndBehavior()
    {
        var cs = ProviderConformanceFixture.GetEnvironmentConnectionString("MySQL");
        if (string.IsNullOrWhiteSpace(cs)) return;
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var parent = $"rain.parent {suffix}";
        var table = $"rain items {suffix}";
        var audit = $"rain_audit_{suffix}";
        try
        {
            await Execute(cs, $$"""
                CREATE TABLE `{{parent}}` (id INT PRIMARY KEY, code VARCHAR(20) UNIQUE);
                CREATE TABLE `{{audit}}` (item_id INT, action VARCHAR(10));
                CREATE TABLE `{{table}}` (
                    id INT UNSIGNED NOT NULL AUTO_INCREMENT,
                    parent_id INT,
                    manager_id INT UNSIGNED,
                    qty DECIMAL(10,2) NOT NULL DEFAULT 1.00 COMMENT 'quantity',
                    total DECIMAL(12,2) GENERATED ALWAYS AS (qty * 2) STORED,
                    `Name` VARCHAR(40) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin,
                    updated TIMESTAMP NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
                    kind ENUM('a','b') DEFAULT 'a',
                    PRIMARY KEY (id),
                    UNIQUE KEY uq_items (parent_id, `Name`),
                    KEY `ix.desc` (qty DESC, `Name`(10)),
                    KEY ix_expr ((lower(`Name`))),
                    CONSTRAINT fk_parent FOREIGN KEY (parent_id) REFERENCES `{{parent}}` (id) ON DELETE CASCADE,
                    CONSTRAINT fk_self FOREIGN KEY (manager_id) REFERENCES `{{table}}` (id),
                    CONSTRAINT ck_qty CHECK (qty > 0)
                ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci ROW_FORMAT=DYNAMIC COMMENT='it''s the items';
                DELIMITER $$
                CREATE TRIGGER `trg_{{suffix}}` AFTER INSERT ON `{{table}}` FOR EACH ROW
                BEGIN
                    INSERT INTO `{{audit}}` (item_id, action) VALUES (NEW.id, 'insert');
                END$$
                CREATE TRIGGER `trg2_{{suffix}}` AFTER INSERT ON `{{table}}` FOR EACH ROW INSERT INTO `{{audit}}` (item_id, action) VALUES (NEW.id, 'second')$$
                DELIMITER ;
                """);
            var session = new DatabaseSession(new ConnectionInfo { Name = "my", Provider = "MySQL", ConnectionString = cs }, new MySqlProvider());
            var result = await TableScripting.ScriptCreateAsync(session, new DatabaseObjectIdentity(null, null, table, SchemaNodeType.Table));
            Assert.Null(result.Error);
            var sql = result.Sql!;
            Assert.Contains($"Referenced table `{parent}`", sql);
            Assert.DoesNotContain("DEFINER=", sql);

            var before = await Definition(cs, table);
            await Execute(cs, $"DROP TABLE `{table}`");
            await Execute(cs, sql);
            Assert.Equal(before, await Definition(cs, table));

            await Execute(cs, $"INSERT INTO `{parent}` VALUES (1, 'p'); INSERT INTO `{table}` (parent_id, qty, `Name`) VALUES (1, 3, 'x');");
            Assert.Equal(["6.00"], await Rows(cs, $"SELECT total FROM `{table}`"));
            Assert.Equal(["insert", "second"], await Rows(cs, $"SELECT action FROM `{audit}` ORDER BY action"));
            await Assert.ThrowsAsync<MySqlException>(() => Execute(cs, $"INSERT INTO `{table}` (qty) VALUES (0)"));
            await Assert.ThrowsAsync<MySqlException>(() => Execute(cs, $"INSERT INTO `{table}` (parent_id, qty) VALUES (99, 1)"));
        }
        finally
        {
            await Execute(cs, $"DROP TABLE IF EXISTS `{table}`; DROP TABLE IF EXISTS `{parent}`; DROP TABLE IF EXISTS `{audit}`;");
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task MissingTable_ReportsErrorInsteadOfScript()
    {
        var cs = ProviderConformanceFixture.GetEnvironmentConnectionString("MySQL");
        if (string.IsNullOrWhiteSpace(cs)) return;
        var session = new DatabaseSession(new ConnectionInfo { Name = "my", Provider = "MySQL", ConnectionString = cs }, new MySqlProvider());
        var result = await TableScripting.ScriptCreateAsync(session, new DatabaseObjectIdentity(null, null, "rain_missing_table", SchemaNodeType.Table));
        Assert.Null(result.Sql);
        Assert.Contains("rain_missing_table", result.Error);
    }
}
