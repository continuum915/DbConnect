using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DbConnect.Models;
using DbConnect.Services;

namespace DbConnect.ViewModels;

internal static class OpenTableMetadataRefresh
{
    public static async Task ApplyAsync(IEnumerable<ViewModelBase> tabs, DatabaseSession session, MetadataScope scope)
    {
        var affected = tabs.OfType<TableDataTabViewModel>().Where(tab =>
            ReferenceEquals(tab.Session, session) && tab.ObjectSelection.Identity is { } identity &&
            MetadataScopeCoverage.Contains(scope, new(identity, SchemaNodeType.Column))).ToArray();
        // The tab's normal refresh guard retains unsaved edits and offers an explicit refresh after they are resolved.
        foreach (var tab in affected)
        {
            if (session.Catalog.GetSnapshot(scope).Error is { } error)
                tab.StatusMessage = error + " Refresh explorer to retry.";
            else await tab.RefreshAsync();
        }
    }
}
