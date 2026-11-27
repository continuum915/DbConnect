using System.Collections.ObjectModel;
using System.ComponentModel;
using FluentIcons.Common;

namespace DbConnect.Models;

public enum SchemaNodeType
{
    Database,
    Table,
    View,
    Column,
    IndexFolder,   // "Indexes" group node under a Table
    Index,         // individual index node
    TriggerFolder,
    Trigger,
    FunctionFolder,
    Function,

    // Schema grouping (PostgreSQL / SQL Server)
    Schema,

    // Schema-level folder types (lazy-loadable) — tables/views within a schema
    TableFolder,
    ViewFolder,

    // Schema-level folder types (lazy-loadable)
    ProcedureFolder,
    MaterializedViewFolder,
    SequenceFolder,
    ExtensionFolder,
    RoleFolder,

    // Object leaf types
    Procedure,
    MaterializedView,
    Sequence,
    Extension,
    Role,

    // Table-level additions
    ForeignKeyFolder,
    ForeignKey,

    // SQLite-specific
    VirtualTable,
    DatabaseInfoFolder,
    DatabaseInfoItem,
    ConnectionGroup,

    /// <summary>Groups a server's system databases (master, template0, mysql, …) under the connection.</summary>
    DatabaseFolder
}

public class SchemaNode : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    private void Notify(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private int? _childCount;

    public string Name { get; set; } = string.Empty;
    public string? RawName { get; set; }
    public DatabaseObjectIdentity? Identity { get; set; }
    public string? CatalogName { get; set; }
    public string? RoutineSignature { get; set; }
    public SchemaNodeType Type { get; set; }
    private ObservableCollection<SchemaNode> _children = new();
    public ObservableCollection<SchemaNode> Children
    {
        get => _children;
        set
        {
            _children.CollectionChanged -= OnChildrenChanged;
            _children = value;
            _children.CollectionChanged += OnChildrenChanged;
            Notify(nameof(Children));
            Notify(nameof(Meta));
        }
    }

    public SchemaNode() => _children.CollectionChanged += OnChildrenChanged;

    private void OnChildrenChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (Type == SchemaNodeType.ConnectionGroup) Notify(nameof(Meta));
    }
    private MetadataLoadState _metadataState;
    public MetadataLoadState MetadataState
    {
        get => _metadataState;
        set { _metadataState = value; Notify(nameof(MetadataState)); Notify(nameof(MetadataStatus)); }
    }
    public string? MetadataError { get; set; }
    /// <summary>Shown beside the node only when it needs attention: loading, failed, stale or blocked.</summary>
    public string MetadataStatus => MetadataState switch
    {
        MetadataLoadState.Loading => "Loading…",
        MetadataLoadState.Failed => "Failed · Refresh to retry",
        MetadataLoadState.Stale => "Stale · Refresh to retry",
        MetadataLoadState.Pending => "Pending transaction · Commit or roll back to refresh metadata",
        _ => string.Empty
    };

    public int? ChildCount
    {
        get => _childCount;
        set
        {
            if (_childCount == value) return;
            _childCount = value;
            Notify(nameof(ChildCount));
            Notify(nameof(DisplayName));
            Notify(nameof(Meta));
        }
    }

    public string DisplayName => ChildCount.HasValue ? $"{Name} ({ChildCount})" : Name ?? string.Empty;

    /// <summary>Provider badge emoji for root Database nodes (set by MainWindowViewModel).</summary>
    public string ProviderIcon { get; set; } = string.Empty;

    private string? _connectionMeta;

    /// <summary>
    /// For a connection's root node: the provider (and server version once known), shown as the row's meta.
    /// </summary>
    public string? ConnectionMeta
    {
        get => _connectionMeta;
        set { _connectionMeta = value; Notify(nameof(ConnectionMeta)); Notify(nameof(Meta)); }
    }

    /// <summary>The connection's colour tag (a colour string), shown as a small square after a connection's name.</summary>
    public string? TagColor { get; set; }

    /// <summary>True for the node that stands for a connection (a root Database node), not a database on a server.</summary>
    public bool IsConnectionRoot => Type == SchemaNodeType.Database && !string.IsNullOrEmpty(ProviderIcon);

    /// <summary>Connection and group rows are drawn semibold.</summary>
    public bool IsEmphasised => IsConnectionRoot || Type == SchemaNodeType.ConnectionGroup;

    /// <summary>
    /// The navigator's right-aligned meta text: provider for connections, child count for groups and loaded folders.
    /// </summary>
    public string Meta => Type switch
    {
        SchemaNodeType.ConnectionGroup => Children.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
        _ when IsConnectionRoot => ConnectionMeta ?? string.Empty,
        _ => ChildCount?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
    };

    /// <summary>True when this Column node is part of the table's primary key.</summary>
    public bool IsPrimaryKey { get; set; }

    /// <summary>Raw SQL data type string for Column nodes (e.g. "INTEGER", "VARCHAR(255)").
    /// Also used by VirtualTable nodes to store the module name (e.g. "fts5").</summary>
    public string? DataType { get; set; }

    /// <summary>Schema prefix for table/view nodes (e.g. "public" for PostgreSQL, "dbo" for SQL Server).</summary>
    public string? SchemaName { get; set; }

    /// <summary>
    /// For Schema nodes: the schema name used in queries.
    /// For table/view/function/procedure nodes under a schema: the owning schema name.
    /// </summary>
    public string? SchemaPath { get; set; }

    /// <summary>True when this folder node supports lazy-loading its children via GetObjectsAsync.</summary>
    public bool IsLazyLoadable { get; set; }

    /// <summary>True once lazy-loaded children have been fetched and populated.</summary>
    public bool IsLoaded { get; set; }

    /// <summary>When non-null, points to the real underlying node (used by filtered display copies).</summary>
    public SchemaNode? RealNode { get; set; }

    private bool _isExpanded;
    public bool IsExpanded
    {
        get => _isExpanded;
        set { _isExpanded = value; Notify(nameof(IsExpanded)); }
    }

    /// <summary>Optional description / comment for this object (table or column comment from the database).</summary>
    public string? Description { get; set; }

    public string Icon => Type switch
    {
        SchemaNodeType.Database => "🗄️",
        SchemaNodeType.Schema => "📁",
        SchemaNodeType.Table => "📊",
        SchemaNodeType.VirtualTable => "🔮",
        SchemaNodeType.View => "👁️",
        SchemaNodeType.MaterializedView => "🗃️",
        SchemaNodeType.Column => IsPrimaryKey ? "🔑" : "📄",
        SchemaNodeType.IndexFolder
            or SchemaNodeType.TriggerFolder
            or SchemaNodeType.FunctionFolder
            or SchemaNodeType.ProcedureFolder
            or SchemaNodeType.MaterializedViewFolder
            or SchemaNodeType.SequenceFolder
            or SchemaNodeType.ExtensionFolder
            or SchemaNodeType.RoleFolder
            or SchemaNodeType.ForeignKeyFolder
            or SchemaNodeType.DatabaseInfoFolder
            or SchemaNodeType.TableFolder
            or SchemaNodeType.ViewFolder
            or SchemaNodeType.DatabaseFolder => "📁",
        SchemaNodeType.Index => "🔍",
        SchemaNodeType.Trigger => "⚡",
        SchemaNodeType.Function => "ƒ",
        SchemaNodeType.Procedure => "⚙️",
        SchemaNodeType.Sequence => "🔢",
        SchemaNodeType.Extension => "🔌",
        SchemaNodeType.Role => "👤",
        SchemaNodeType.ForeignKey => "🔗",
        SchemaNodeType.DatabaseInfoItem => "ℹ️",
        SchemaNodeType.ConnectionGroup => "📁",
        _ => "❓"
    };

    /// <summary>The node's icon (UI redesign issue 01's icon table).</summary>
    public Symbol? FluentIconSymbol => Type switch
    {
        SchemaNodeType.Database => IsConnectionRoot ? Symbol.PlugConnected : Symbol.Database,
        SchemaNodeType.Schema => Symbol.Database,
        SchemaNodeType.Table => Symbol.Table,
        SchemaNodeType.VirtualTable => Symbol.Table,
        SchemaNodeType.View => Symbol.Eye,
        SchemaNodeType.MaterializedView => Symbol.Eye,
        SchemaNodeType.Column => IsPrimaryKey ? Symbol.Key : (Symbol?)null,
        SchemaNodeType.ForeignKey => Symbol.Link,
        SchemaNodeType.IndexFolder or SchemaNodeType.TriggerFolder
            or SchemaNodeType.FunctionFolder or SchemaNodeType.ProcedureFolder
            or SchemaNodeType.MaterializedViewFolder or SchemaNodeType.SequenceFolder
            or SchemaNodeType.ExtensionFolder or SchemaNodeType.RoleFolder
            or SchemaNodeType.ForeignKeyFolder or SchemaNodeType.DatabaseInfoFolder
            or SchemaNodeType.TableFolder or SchemaNodeType.ViewFolder
            or SchemaNodeType.DatabaseFolder => Symbol.Folder,
        SchemaNodeType.Index => Symbol.ArrowSort,
        SchemaNodeType.Trigger => Symbol.Flash,
        SchemaNodeType.Function => Symbol.MathFormula,
        SchemaNodeType.Procedure => Symbol.Code,
        SchemaNodeType.Role => Symbol.Person,
        SchemaNodeType.DatabaseInfoItem => Symbol.Info,
        SchemaNodeType.ConnectionGroup => Symbol.FolderList,
        _ => (Symbol?)null
    };

    /// <summary>The palette role the icon is drawn in ("IconBlue" for tables, ...); the view maps it to a brush.</summary>
    public string IconRole => Type switch
    {
        SchemaNodeType.Database => IsConnectionRoot ? "IconGreen" : "IconGold",
        SchemaNodeType.Schema => "IconGold",
        SchemaNodeType.Table or SchemaNodeType.VirtualTable => "IconBlue",
        SchemaNodeType.View or SchemaNodeType.MaterializedView => "IconPurple",
        SchemaNodeType.Column or SchemaNodeType.ConnectionGroup or SchemaNodeType.Role => IsPrimaryKey ? "IconGold" : "IconSlate",
        SchemaNodeType.ForeignKey => "IconTeal",
        SchemaNodeType.Trigger or SchemaNodeType.Function or SchemaNodeType.Procedure => "IconOrange",
        SchemaNodeType.Index or SchemaNodeType.DatabaseInfoItem => "TextMuted",
        _ => "IconOffline",
    };
}
