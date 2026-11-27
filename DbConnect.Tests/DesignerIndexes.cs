using DbConnect.Models;
using DbConnect.ViewModels;

namespace DbConnect.Tests;

/// <summary>Index edits through the designer's Indexes editor, as the Indexes tab makes them.</summary>
internal static class DesignerIndexes
{
    /// <summary>Adds an index with the given name, uniqueness and ordered columns (Index button, Unique cell, column picker).</summary>
    public static EditableIndex AddIndex(this TableDesignerViewModel vm, string name, bool unique, params string[] columns)
    {
        vm.Indexes.AddIndexCommand.Execute(null);
        var index = vm.Indexes.Selected!;
        index.Name = name;
        index.IsUnique = unique;
        foreach (var column in columns)
        {
            vm.Indexes.NewPart = column;
            vm.Indexes.AddPartCommand.Execute(null);
        }
        return index;
    }

    public static EditableIndex Index(this TableDesignerViewModel vm, string name) => vm.Indexes.Items.Single(i => i.Name == name);

    /// <summary>Drops a loaded index, or keeps it again (the row's drop button).</summary>
    public static void DropIndex(this TableDesignerViewModel vm, string name) => vm.Indexes.DropIndexCommand.Execute(vm.Index(name));
}
