namespace DbConnect.Models;

internal static class PathGeometries
{
    // Small filled diamond: ◆
    public const string Diamond = "M 8,0 L 16,8 L 8,16 L 0,8 Z";

    // 2×2 grid squares: ▦
    public const string Grid = "M 0,0 H 6 V 6 H 0 Z M 10,0 H 16 V 6 H 10 Z M 0,10 H 6 V 16 H 0 Z M 10,10 H 16 V 16 H 10 Z";

    // Three horizontal lines: ≡
    public const string Lines = "M 0,4 H 16 M 0,8 H 16 M 0,12 H 16";

    // Stacked rectangles (layered): ⊟
    public const string Layers = "M 1,4 H 15 V 8 H 1 Z M 3,9 H 13 V 13 H 3 Z";

    // Filled circle: ●
    public const string Circle = "M 8,1 A 7,7 0 1 1 7.99,1 Z";

    // Folder with a tab
    public const string Folder = "M 0,3 H 6 L 8,5 H 16 V 14 H 0 Z";
}
