using AvaloniaEdit.Document;
using DbConnect.ViewModels;

namespace DbConnect.Tests;

public class FindReplaceTests
{
    [Fact]
    public void ReplaceAll_ReplacesLiteralMatchesAndUndoesAsOneEdit()
    {
        var document = new TextDocument("SELECT item, ITEM, item;");
        var session = new FindReplaceViewModel(document) { SearchText = "item", ReplacementText = "product" };

        session.ReplaceAllCommand.Execute(null);

        Assert.Equal("SELECT product, product, product;", document.Text);
        document.UndoStack.Undo();
        Assert.Equal("SELECT item, ITEM, item;", document.Text);
    }
    [Theory]
    [InlineData("longer")]
    [InlineData("")]
    public void InSelection_TracksTheOriginalRangeAcrossReplacementsAndUndo(string replacement)
    {
        var document = new TextDocument("item item item item");
        var session = new FindReplaceViewModel(document) { SearchText = "item", ReplacementText = replacement };
        session.Open(5, 9, 5);
        session.InSelection = true;

        session.ReplaceAllCommand.Execute(null);
        Assert.Equal($"item {replacement} {replacement} item", document.Text);
        document.UndoStack.Undo();
        session.ReplacementText = "x";
        session.ReplaceAllCommand.Execute(null);
        Assert.Equal("item x x item", document.Text);
    }
    [Fact]
    public void FindNext_WrapsWithinSelectionAndReplaceAdvancesToNextMatch()
    {
        var document = new TextDocument("cat cat cat cat");
        var session = new FindReplaceViewModel(document) { SearchText = "cat", ReplacementText = "dog" };
        session.Open(4, 7, 4);
        session.InSelection = true;
        var offsets = new System.Collections.Generic.List<int>();
        session.MatchSelected += (start, length) => offsets.Add(start);

        session.FindNextCommand.Execute(null);
        session.ReplaceCommand.Execute(null);
        session.FindNextCommand.Execute(null);

        Assert.Equal("cat dog cat cat", document.Text);
        Assert.Equal(new[] { 4, 8, 8 }, offsets);
    }
    [Theory]
    [InlineData("", "x", false, "cat CAT cat")]
    [InlineData("cat", "$1", true, "$1 CAT $1")]
    [InlineData("missing", "x", false, "cat CAT cat")]
    public void ReplaceAll_HandlesEmptySearchCaseSensitivityAndLiteralReplacement(
        string search, string replacement, bool matchCase, string expected)
    {
        var document = new TextDocument("cat CAT cat");
        var session = new FindReplaceViewModel(document)
        {
            SearchText = search,
            ReplacementText = replacement,
            MatchCase = matchCase
        };

        session.ReplaceAllCommand.Execute(null);

        Assert.Equal(expected, document.Text);
    }

    [Fact]
    public void ReadOnly_AllowsFindButRejectsBothReplacementCommands()
    {
        var document = new TextDocument("cat cat");
        var session = new FindReplaceViewModel(document) { SearchText = "cat", ReplacementText = "dog", IsReadOnly = true };
        var found = -1;
        session.MatchSelected += (start, _) => found = start;

        session.FindNextCommand.Execute(null);
        session.ReplaceCommand.Execute(null);
        session.ReplaceAllCommand.Execute(null);

        Assert.Equal(0, found);
        Assert.False(session.ReplaceCommand.CanExecute(null));
        Assert.False(session.ReplaceAllCommand.CanExecute(null));
        Assert.Equal("cat cat", document.Text);
    }

    [Fact]
    public void InSelection_DoesNotMatchAcrossEitherSelectionBoundary()
    {
        var document = new TextDocument("cat cat cat");
        var session = new FindReplaceViewModel(document) { SearchText = "cat", ReplacementText = "dog" };
        session.Open(1, 9, 1);
        session.InSelection = true;

        session.ReplaceAllCommand.Execute(null);

        Assert.Equal("cat dog cat", document.Text);
    }

    [Fact]
    public void ManualEdit_InvalidatesPreviouslyFoundMatch()
    {
        var document = new TextDocument("cat cat");
        var session = new FindReplaceViewModel(document) { SearchText = "cat", ReplacementText = "dog" };
        session.FindNextCommand.Execute(null);
        document.Replace(0, 3, "fox");

        session.ReplaceCommand.Execute(null);

        Assert.Equal("fox cat", document.Text);
    }
}
