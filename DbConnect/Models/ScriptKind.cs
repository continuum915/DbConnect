namespace DbConnect.Models;

/// <summary>Kinds of script the explorer's Script As menu generates.</summary>
public enum ScriptKind
{
    Create,
    Drop,
    Select,
    Insert,
    Update,
    Delete
}

/// <summary>Where a generated script goes.</summary>
public enum ScriptTarget
{
    NewTab,
    Clipboard,
    File
}
