namespace DbConnect.Models;

/// <summary>
/// Something a table script needs to exist already, as the script states it. <see cref="Object"/> identifies it by its
/// raw names where it is a database object another script may create: a referenced table (kind Table) or a trigger
/// function (kind Function). Callers decide whether it is satisfied from <see cref="Object"/>, never from the text.
/// </summary>
public sealed record TablePrerequisite(string Text, DatabaseObjectIdentity? Object = null)
{
    public static implicit operator TablePrerequisite(string text) => new(text);

    public override string ToString() => Text;
}
