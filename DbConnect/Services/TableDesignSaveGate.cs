using System.Collections.Generic;
using System.Linq;
using DbConnect.Models;

namespace DbConnect.Services;

/// <summary>
/// Decides whether the designer may save a loaded table at all. Saving is blocked while anything
/// is unrepresented, because no plan could promise to keep it. Edits that would lose something
/// the table has are blocked one by one by <see cref="TableChangePlanner"/>.
/// </summary>
public static class TableDesignSaveGate
{
    public static IReadOnlyList<string> Reasons(TableDesign design)
    {
        var reasons = new List<string>();
        if (design.Unrepresented.Count > 0)
            reasons.Add("This table has structure the designer cannot represent, so saving could lose it: " +
                        string.Join(" ", design.Unrepresented.Select(g => g.Description)));
        return reasons;
    }
}
