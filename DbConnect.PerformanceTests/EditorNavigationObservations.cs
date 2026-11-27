namespace DbConnect.PerformanceTests;

public sealed record EditorNavigationSettings(int MiddleOffset, int MiddleLine, int EndOffset, int EndLine,
    int ViewportsPerScroll, int ScrollStepsPerDirection, bool WordWrap, EditorViewportState? InitialViewport = null);

public sealed record EditorViewportState(double X, double Y, double Width, double Height, double ExtentHeight,
    int FirstVisibleLine, int LastVisibleLine, bool VisualLinesValid);

public sealed record EditorViewportObservations(EditorViewportState Before, EditorViewportState After,
    int? TargetLine, double? RequestedY, double? ClampedTargetY);
