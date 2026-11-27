using System.Collections.Generic;
using DbConnect.Models;

namespace DbConnect.Tests;

public class EditableRowTests
{
    [Fact]
    public void Constructor_OriginalValuesKeepTheCallersKeyComparer()
    {
        var row = new EditableRow(new Dictionary<string, object?>(System.StringComparer.OrdinalIgnoreCase) { ["Name"] = "Ada" });

        Assert.Equal("Ada", row.OriginalValues["NAME"]);
    }

    [Fact]
    public void IsValueChanged_ComparesBinaryByContent()
    {
        var row = new EditableRow(new Dictionary<string, object?> { ["Data"] = new byte[] { 1, 2 } });

        row.Values["Data"] = new byte[] { 1, 2 };
        Assert.False(row.IsValueChanged("Data"));

        row.Values["Data"] = new byte[] { 9 };
        Assert.True(row.IsValueChanged("Data"));
    }

    [Fact]
    public void RevertColumn_ReturnsToUnchanged_WhenAnotherBinaryCellHoldsAnEqualCopy()
    {
        var row = new EditableRow(new Dictionary<string, object?> { ["Name"] = "Ada", ["Data"] = new byte[] { 1, 2 } });
        row.Values["Data"] = new byte[] { 1, 2 };
        row.Values["Name"] = "Grace";
        row.State = EditState.Modified;

        row.RevertColumn("Name");

        Assert.Equal(EditState.Unchanged, row.State);
    }
}
