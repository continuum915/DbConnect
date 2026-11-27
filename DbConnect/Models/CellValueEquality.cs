using System;
using System.Linq;

namespace DbConnect.Models;

/// <summary>
/// RAIN-P2-11 — Whether two cell values are the same for edit tracking: NULL and DBNull are one,
/// binary is compared by content, and everything else is exact. Deliberately stricter than
/// <c>DataCompareService</c>'s cross-database comparison: a float tolerance or a same-text match
/// here would treat a real edit as no change and silently leave it out of the UPDATE.
/// </summary>
public static class CellValueEquality
{
    public static bool AreEqual(object? left, object? right)
    {
        if (left is DBNull)
            left = null;
        if (right is DBNull)
            right = null;

        if (left is byte[] leftBytes && right is byte[] rightBytes)
            return leftBytes.SequenceEqual(rightBytes);

        return Equals(left, right);
    }
}
