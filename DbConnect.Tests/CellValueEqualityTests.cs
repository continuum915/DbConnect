using System;
using DbConnect.Models;

namespace DbConnect.Tests;

/// <summary>
/// RAIN-P2-11 — Edit tracking compares cell values exactly, binary by content. Deliberately stricter
/// than DataCompareService's cross-database comparison: a looser rule would swallow real edits.
/// </summary>
public class CellValueEqualityTests
{
    [Fact]
    public void NullAndDbNull_AreEqual()
    {
        Assert.True(CellValueEquality.AreEqual(null, null));
        Assert.True(CellValueEquality.AreEqual(null, DBNull.Value));
        Assert.True(CellValueEquality.AreEqual(DBNull.Value, null));
    }

    [Fact]
    public void NullAndAValue_AreNotEqual()
    {
        Assert.False(CellValueEquality.AreEqual(null, ""));
        Assert.False(CellValueEquality.AreEqual(0L, null));
    }

    [Fact]
    public void ByteArrays_CompareByContent()
    {
        Assert.True(CellValueEquality.AreEqual(new byte[] { 1, 2 }, new byte[] { 1, 2 }));
        Assert.False(CellValueEquality.AreEqual(new byte[] { 1, 2 }, new byte[] { 1, 3 }));
        Assert.False(CellValueEquality.AreEqual(new byte[] { 1, 2 }, new byte[] { 1, 2, 3 }));
    }

    [Fact]
    public void TinyFloatingPointEdits_AreChanges()
    {
        Assert.False(CellValueEquality.AreEqual(1e-12, 5e-12));
    }

    [Fact]
    public void DifferentTypes_AreNotEqual_EvenWithTheSameText()
    {
        Assert.False(CellValueEquality.AreEqual("1", 1L));
    }

    [Fact]
    public void DifferentNumericTypes_AreNotEqual()
    {
        Assert.False(CellValueEquality.AreEqual(1, 1L));
        Assert.False(CellValueEquality.AreEqual(1L, 1.0m));
    }

    [Fact]
    public void SameTypedValues_UseTheirOwnEquality()
    {
        Assert.True(CellValueEquality.AreEqual("Ada", "Ada"));
        Assert.False(CellValueEquality.AreEqual("Ada", "ada"));
        Assert.True(CellValueEquality.AreEqual(new DateTime(2026, 9, 29), new DateTime(2026, 9, 29)));
    }
}
