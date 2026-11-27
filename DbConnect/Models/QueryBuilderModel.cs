using System.Collections.Generic;

namespace DbConnect.Models;

public class QueryBuilderTable
{
    public string Alias { get; set; } = string.Empty;
    public string TableName { get; set; } = string.Empty;
    public string SchemaName { get; set; } = string.Empty;
    public List<QueryBuilderColumn> Columns { get; set; } = new();
    public double CanvasX { get; set; } = 50;
    public double CanvasY { get; set; } = 50;
    public double X
    {
        get => CanvasX;
        set => CanvasX = value;
    }

    public double Y
    {
        get => CanvasY;
        set => CanvasY = value;
    }
}

public class QueryBuilderColumn
{
    public string ColumnName { get; set; } = string.Empty;
    public bool IsSelected { get; set; }
    public string? WhereExpression { get; set; }
    public string? AliasAs { get; set; }
}

public class QueryBuilderJoin
{
    public string LeftTableAlias { get; set; } = string.Empty;
    public string LeftTableName { get; set; } = string.Empty;
    public string LeftColumn { get; set; } = string.Empty;
    public string RightTableAlias { get; set; } = string.Empty;
    public string RightTableName { get; set; } = string.Empty;
    public string RightColumn { get; set; } = string.Empty;
    public string JoinType { get; set; } = "INNER";
}

public class QueryBuilderWhereClause
{
    public string Expression { get; set; } = string.Empty;
}

public class QueryBuilderGroupByClause
{
    public string Column { get; set; } = string.Empty;
}

public class QueryBuilderOrderByClause
{
    public string Column { get; set; } = string.Empty;
    public bool Descending { get; set; }
}

public class QueryBuilderHavingClause
{
    public string Expression { get; set; } = string.Empty;
}
