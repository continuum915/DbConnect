using DbConnect.Models;

namespace DbConnect.ViewModels;

internal static class SessionTabPersistence
{
    public static SavedTab? Capture(ViewModelBase tab) => tab switch
    {
        QueryTabViewModel query => new SavedTab
        {
            TabType = "Query",
            Title = query.Title,
            ConnectionId = query.ConnectionIdToSave,
            ConnectionName = query.ConnectionNameToSave,
            QueryText = query.QueryText,
            IsPinned = query.IsPinned,
            FilePath = query.Document?.Path,
            IsDirty = query.IsDirty
        },
        TableDataTabViewModel table => new SavedTab
        {
            TabType = "TableData",
            Title = table.Title,
            ConnectionId = table.Session?.Connection.Id,
            ConnectionName = table.Session?.Connection.Name,
            TableName = table.TableName,
            Object = table.ObjectSelection.Identity,
            DatabaseName = table.Session?.DatabaseLabel,
            IsPinned = table.IsPinned
        },
        _ => null
    };
}
