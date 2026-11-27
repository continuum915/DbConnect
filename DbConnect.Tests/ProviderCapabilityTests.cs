using DbConnect.Services;

namespace DbConnect.Tests;

public class ProviderCapabilityTests
{
    [Theory]
    [InlineData("SQLite", "")]
    [InlineData("PostgreSQL", "CONNECT,CREATE,TEMPORARY,USAGE,SELECT,INSERT,UPDATE,DELETE,TRIGGER,EXECUTE,ALL PRIVILEGES")]
    [InlineData("SQL Server", "")]
    [InlineData("MySQL", "SELECT,INSERT,UPDATE,DELETE,CREATE,DROP,ALTER,INDEX,EXECUTE,ALL PRIVILEGES")]
    [InlineData("DuckDB", "")]
    public void ProvidersDeclarePrivilegeOptions(string providerName, string expected)
    {
        Assert.Equal(expected, string.Join(",", new DatabaseService().GetProvider(providerName).Capabilities.PrivilegeOptions));
    }

    [Theory]
    [InlineData("SQLite", true)]
    [InlineData("PostgreSQL", true)]
    [InlineData("SQL Server", false)]
    [InlineData("MySQL", false)]
    [InlineData("DuckDB", false)]
    public void ProvidersDeclareSupportsIndexWhereClause(string providerName, bool expected)
    {
        Assert.Equal(expected, new DatabaseService().GetProvider(providerName).Capabilities.SupportsIndexWhereClause);
    }

    [Theory]
    [InlineData("SQLite", false)]
    [InlineData("PostgreSQL", false)]
    [InlineData("SQL Server", true)]
    [InlineData("MySQL", false)]
    [InlineData("DuckDB", false)]
    public void ProvidersDeclareSupportsIncludedIndexColumns(string providerName, bool expected)
    {
        Assert.Equal(expected, new DatabaseService().GetProvider(providerName).Capabilities.SupportsIncludedIndexColumns);
    }

    [Theory]
    [InlineData("SQLite", true)]
    [InlineData("PostgreSQL", false)]
    [InlineData("SQL Server", false)]
    [InlineData("MySQL", false)]
    [InlineData("DuckDB", true)]
    public void ProvidersDeclareIsEmbedded(string providerName, bool expected)
    {
        Assert.Equal(expected, new DatabaseService().GetProvider(providerName).Capabilities.IsEmbedded);
    }

    [Theory]
    [InlineData("SQLite", true)]
    [InlineData("PostgreSQL", true)]
    [InlineData("SQL Server", true)]
    [InlineData("MySQL", true)]
    [InlineData("DuckDB", false)]
    public void ProvidersDeclareSupportsForwardEngineering(string providerName, bool expected)
    {
        Assert.Equal(expected, new DatabaseService().GetProvider(providerName).Capabilities.SupportsForwardEngineering);
    }

    [Theory]
    [InlineData("SQLite", true)]
    [InlineData("PostgreSQL", false)]
    [InlineData("SQL Server", false)]
    [InlineData("MySQL", false)]
    [InlineData("DuckDB", false)]
    public void ProvidersDeclareRequiresForeignKeyPragma(string providerName, bool expected)
    {
        Assert.Equal(expected, new DatabaseService().GetProvider(providerName).Capabilities.RequiresForeignKeyPragma);
    }

    [Theory]
    [InlineData("SQLite", true)]
    [InlineData("PostgreSQL", false)]
    [InlineData("SQL Server", false)]
    [InlineData("MySQL", false)]
    [InlineData("DuckDB", false)]
    public void ProvidersDeclareRequiresTableRebuildForConstraints(string providerName, bool expected)
    {
        Assert.Equal(expected, new DatabaseService().GetProvider(providerName).Capabilities.RequiresTableRebuildForConstraints);
    }

    [Theory]
    [InlineData("SQLite", false)]
    [InlineData("PostgreSQL", true)]
    [InlineData("SQL Server", false)]
    [InlineData("MySQL", false)]
    [InlineData("DuckDB", false)]
    public void ProvidersDeclareShowsVerifyFullSslHint(string providerName, bool expected)
    {
        Assert.Equal(expected, new DatabaseService().GetProvider(providerName).Capabilities.ShowsVerifyFullSslHint);
    }

    [Theory]
    [InlineData("SQLite", false)]
    [InlineData("PostgreSQL", false)]
    [InlineData("SQL Server", true)]
    [InlineData("MySQL", false)]
    [InlineData("DuckDB", false)]
    public void ProvidersDeclareUsesEncryptionOptions(string providerName, bool expected)
    {
        Assert.Equal(expected, new DatabaseService().GetProvider(providerName).Capabilities.UsesEncryptionOptions);
    }

    [Theory]
    [InlineData("SQLite", "", BackupStrategy.SqliteOnline)]
    [InlineData("PostgreSQL", "public", BackupStrategy.PostgreSqlTools)]
    [InlineData("SQL Server", "dbo", BackupStrategy.SqlServerCommands)]
    [InlineData("MySQL", "", BackupStrategy.DbConnectDump)]
    [InlineData("DuckDB", "main", BackupStrategy.None)]
    public void ProvidersDeclareDefaultSchemaAndBackupStrategy(string name, string schema, BackupStrategy backup)
    {
        var capabilities = new DatabaseService().GetProvider(name).Capabilities;
        Assert.Equal(schema, capabilities.DefaultSchema);
        Assert.Equal(backup, capabilities.BackupStrategy);
    }

    [Theory]
    [InlineData("SQLite", "\"Customers\"", "LIMIT", false, false, false, true, true, true, false, true, false, true, false)]
    [InlineData("PostgreSQL", "\"Customers\"", "LIMIT", true, true, true, true, true, true, false, true, true, true, true)]
    [InlineData("SQL Server", "[Customers]", "TOP", false, true, false, true, true, true, false, true, true, true, true)]
    [InlineData("MySQL", "`Customers`", "LIMIT", true, false, false, true, true, true, true, true, true, true, true)]
    [InlineData("DuckDB", "\"Customers\"", "LIMIT", true, true, true, true, true, true, true, false, true, false, false)]
    public void ProvidersExposeExpectedCapabilities(
        string providerName,
        string quotedIdentifier,
        string limitStyle,
        bool supportsAnalyze,
        bool supportsSchemas,
        bool supportsCreateOrReplaceView,
        bool supportsDropColumn,
        bool supportsRenameColumn,
        bool supportsTableDesignerDropColumn,
        bool supportsTableDesignerRenameColumn,
        bool supportsTriggers,
        bool supportsFunctions,
        bool supportsForeignKeys,
        bool supportsProcedures)
    {
        var provider = new DatabaseService().GetProvider(providerName);

        Assert.True(provider.Capabilities.SupportsTransactionalBatches);
        Assert.True(provider.Capabilities.GuaranteesTransactionalBatches);
        Assert.Equal(quotedIdentifier, provider.Capabilities.QuoteIdentifier("Customers"));
        Assert.Equal("@p2", provider.Capabilities.CreateParameterName(2));
        Assert.Equal(limitStyle, provider.Capabilities.LimitStyle);
        Assert.Equal(supportsAnalyze, provider.Capabilities.SupportsAnalyzeExplain);
        Assert.Equal(supportsSchemas, provider.Capabilities.SupportsSchemas);
        Assert.Equal(supportsCreateOrReplaceView, provider.Capabilities.SupportsCreateOrReplaceView);
        Assert.Equal(supportsDropColumn, provider.Capabilities.SupportsDropColumn);
        Assert.Equal(supportsRenameColumn, provider.Capabilities.SupportsRenameColumn);
        Assert.Equal(supportsTableDesignerDropColumn, provider.Capabilities.SupportsTableDesignerDropColumn);
        Assert.Equal(supportsTableDesignerRenameColumn, provider.Capabilities.SupportsTableDesignerRenameColumn);
        Assert.Equal(supportsTriggers, provider.Capabilities.SupportsTriggers);
        Assert.Equal(supportsFunctions, provider.Capabilities.SupportsFunctions);
        Assert.Equal(supportsForeignKeys, provider.Capabilities.SupportsForeignKeys);
        Assert.Equal(supportsProcedures, provider.Capabilities.SupportsProcedures);
    }

    [Theory]
    [InlineData("SQLite", false, false)]
    [InlineData("PostgreSQL", true, true)]
    [InlineData("SQL Server", true, false)]
    [InlineData("MySQL", true, true)]
    [InlineData("DuckDB", false, false)]
    public void ProvidersExposeExpectedCommentAndUserWriteCapabilities(
        string providerName,
        bool supportsComments,
        bool supportsUserWrite)
    {
        var capabilities = new DatabaseService().GetProvider(providerName).Capabilities;

        Assert.True(capabilities.SupportsViews);
        Assert.True(capabilities.SupportsIndexes);
        Assert.Equal(supportsComments, capabilities.SupportsObjectComments);
        Assert.Equal(supportsUserWrite, capabilities.SupportsUserWrite);
    }

    [Theory]
    [InlineData("SQLite", false)]
    [InlineData("PostgreSQL", false)]
    [InlineData("SQL Server", true)]
    [InlineData("MySQL", false)]
    [InlineData("DuckDB", false)]
    public void ProvidersExposeExpectedExplainStrategy(string providerName, bool usesShowPlan)
    {
        var capabilities = new DatabaseService().GetProvider(providerName).Capabilities;

        Assert.Equal(usesShowPlan, capabilities.UsesShowPlanExplain);
    }

    [Fact]
    public void Capabilities_EscapeIdentifierTerminators()
    {
        Assert.Equal("[Order]]History]", new SqlServerProvider().Capabilities.QuoteIdentifier("Order]History"));
        Assert.Equal("\"Customer\"\"Archive\"", new SqliteProvider().Capabilities.QuoteIdentifier("Customer\"Archive"));
    }

    [Fact]
    public void DdlGenerator_CanQuoteUsingProviderCapabilities()
    {
        var capabilities = new SqlServerProvider().Capabilities;

        Assert.Equal("[Order Details]", DdlGenerator.Quote("Order Details", capabilities));
    }

    [Theory]
    [InlineData("SQLite", "X'AB01'")]
    [InlineData("MySQL", "X'AB01'")]
    [InlineData("SQL Server", "0xAB01")]
    [InlineData("PostgreSQL", @"'\xAB01'::bytea")]
    [InlineData("DuckDB", @"'\xAB\x01'::BLOB")]
    public void Providers_WriteBinaryLiteralsInTheirOwnDialect(string providerName, string expected)
    {
        var capabilities = new DatabaseService().GetProvider(providerName).Capabilities;

        Assert.Equal(expected, capabilities.FormatBinaryLiteral([0xAB, 0x01]));
    }

    [Theory]
    [InlineData("SQLite", "BEGIN TRANSACTION;")]
    [InlineData("PostgreSQL", "BEGIN TRANSACTION;")]
    [InlineData("SQL Server", "BEGIN TRANSACTION;")]
    [InlineData("DuckDB", "BEGIN TRANSACTION;")]
    [InlineData("MySQL", "START TRANSACTION;")]
    public void Providers_DeclareTheirBeginTransactionStatement(string providerName, string expected)
    {
        Assert.Equal(expected, new DatabaseService().GetProvider(providerName).Capabilities.BeginTransactionStatement);
    }

    [Theory]
    [InlineData(TransactionSupportLevel.Guaranteed, "Transactional commit")]
    [InlineData(TransactionSupportLevel.BestEffort, "Best-effort commit")]
    [InlineData(TransactionSupportLevel.None, "Non-transactional commit")]
    public void TransactionSupport_ExposesCommitLabels(TransactionSupportLevel level, string expectedLabel)
    {
        var capabilities = new DatabaseProviderCapabilities("\"", "\"", "LIMIT")
        {
            TransactionSupport = level
        };

        Assert.Equal(expectedLabel, capabilities.TransactionStatusLabel);
    }
}
