using MySqlConnector;
using DbConnect.Models;
using DbConnect.Services;

namespace DbConnect.Tests.ProviderConformance;

public class CsvMySqlVisibilityTests
{
    [Fact]
    [Trait("Category", "Integration")]
    public async Task ImporterWithoutTriggerVisibilityMustAcknowledgeUnverifiedEffects()
    {
        var adminConnection = Environment.GetEnvironmentVariable("DBCONNECT_CONFORMANCE_MYSQL_ADMIN_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(adminConnection)) { Assert.False(CsvImportConformanceTests.IsSelected("MySQL"), "Selected CSV MySQL visibility case requires admin fixture configuration."); return; }
        var provider = new MySqlProvider();
        var admin = new DatabaseSession(new ConnectionInfo { ConnectionString = adminConnection }, provider);
        var suffix = Guid.NewGuid().ToString("N")[..16];
        var user = "rain_csv_u_" + suffix;
        var table = "rain_csv_vis_" + suffix;
        var audit = "rain_csv_audit_" + suffix;
        var path = Path.GetTempFileName();
        var schema = new MySqlConnectionStringBuilder(adminConnection).Database;
        try
        {
            await admin.ExecuteNonQueryAsync($"CREATE USER '{user}'@'%' IDENTIFIED BY 'csv_fixture_only'; CREATE TABLE `{table}`(id INTEGER UNIQUE) ENGINE=InnoDB; CREATE TABLE `{audit}`(id INTEGER) ENGINE=MyISAM; CREATE TRIGGER `rain_csv_t_{suffix}` AFTER INSERT ON `{table}` FOR EACH ROW INSERT INTO `{audit}` VALUES(NEW.id); GRANT SELECT, INSERT, DELETE ON `{schema}`.`{table}` TO '{user}'@'%'");
            var limitedConnection = new MySqlConnectionStringBuilder(adminConnection) { UserID = user, Password = "csv_fixture_only", Pooling = false }.ConnectionString;
            var limited = new DatabaseSession(new ConnectionInfo { ConnectionString = limitedConnection }, provider);
            // Prove that zero visible triggers is not proof of absence for this actual importer.
            Assert.Empty((await limited.ExecuteQueryAsync($"SELECT TRIGGER_NAME FROM information_schema.TRIGGERS WHERE EVENT_OBJECT_TABLE='{table}'")).ObjectRows);
            await File.WriteAllTextAsync(path, "id\n1\n1");
            var options = new CsvImportOptions { FilePath = path, TableName = table, Mode = ImportMode.ExistingTable };
            var warning = await CsvImportService.ExecuteImportAsync(limited, options);
            Assert.NotNull(warning.RequiredAcknowledgement);
            Assert.Empty((await admin.ExecuteQueryAsync($"SELECT id FROM `{audit}`")).ObjectRows);
            options.RiskAcknowledgement = warning.RequiredAcknowledgement.Token;
            var result = await CsvImportService.ExecuteImportAsync(limited, options);
            Assert.Equal(TransactionOutcome.Unknown, result.Outcome);
            Assert.Empty((await admin.ExecuteQueryAsync($"SELECT id FROM `{table}`")).ObjectRows);
            Assert.Single((await admin.ExecuteQueryAsync($"SELECT id FROM `{audit}`")).ObjectRows);
        }
        finally
        {
            File.Delete(path);
            await admin.ExecuteNonQueryAsync($"DROP TABLE IF EXISTS `{table}`; DROP TABLE IF EXISTS `{audit}`; DROP USER IF EXISTS '{user}'@'%'");
        }
    }
}
