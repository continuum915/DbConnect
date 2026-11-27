using CommunityToolkit.Mvvm.ComponentModel;

namespace DbConnect.ViewModels;

public partial class JoinDefinition : ObservableObject
{
    public string LeftTable { get; set; } = string.Empty;
    public string LeftColumn { get; set; } = string.Empty;
    public string RightTable { get; set; } = string.Empty;
    public string RightColumn { get; set; } = string.Empty;

    [ObservableProperty]
    private string _joinType = "INNER";
}

public class WhereCondition
{
    public string Expression { get; set; } = string.Empty;
}

public class OrderByColumn
{
    public string Column { get; set; } = string.Empty;
    public bool Descending { get; set; }
    public string DisplayText => $"{Column} {(Descending ? "DESC" : "ASC")}";
}

public class GroupByColumn
{
    public string Column { get; set; } = string.Empty;
}

public class HavingCondition
{
    public string Expression { get; set; } = string.Empty;
}
