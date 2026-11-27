namespace DbConnect.Services;

/// <summary>How much of the cell value viewer a value gets, by its size (RAIN-P2-18).</summary>
public enum CellViewerTier
{
    /// <summary>Lazy tree; highlighted, foldable text with Format and word wrap toggles.</summary>
    Full,
    /// <summary>Lazy tree; plain text, JSON always formatted, no highlighting or folding.</summary>
    Reduced,
    /// <summary>No tree; the first <see cref="CellViewerLimits.PreviewChars"/> as plain text.</summary>
    Preview,
}

/// <summary>Every threshold the cell value viewer uses. Sizes are lengths in UTF-16 characters.</summary>
public static class CellViewerLimits
{
    /// <summary>Maximum source characters rendered in a grid cell, excluding the ellipsis.</summary>
    public const int GridDisplayChars = 2048;
    public const int FullMaxChars = 1024 * 1024;
    public const int ReducedMaxChars = 10 * 1024 * 1024;
    public const int PreviewChars = 64 * 1024;
    /// <summary>A reduced-tier text line longer than this forces word wrap on.</summary>
    public const int LongLineChars = 10_000;
    /// <summary>Strings longer than this are cut short, with "…", in the tree.</summary>
    public const int TreeStringMaxChars = 200;
    /// <summary>Arrays and objects with more children than this are shown in groups of this many.</summary>
    public const int TreeGroupSize = 100;
    public const int MaxJsonDepth = 256;

    public static CellViewerTier TierFor(int length) => length switch
    {
        <= FullMaxChars => CellViewerTier.Full,
        <= ReducedMaxChars => CellViewerTier.Reduced,
        _ => CellViewerTier.Preview,
    };
}
