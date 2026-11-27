using Avalonia.Controls;

namespace Win7AeroTheme.Gallery;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContext = new GalleryViewModel();
    }
}

public sealed class GalleryViewModel
{
    public IReadOnlyList<FileRow> Files { get; } =
    [
        new("Documents", "May 18, 2026", "File folder", ""),
        new("Pictures", "May 17, 2026", "File folder", ""),
        new("Theme notes.txt", "May 16, 2026", "Text Document", "12 KB"),
        new("Aero mockup.png", "May 15, 2026", "PNG image", "842 KB"),
        new("Package manifest.nuspec", "May 14, 2026", "NUSPEC File", "4 KB")
    ];

    public IReadOnlyList<TransferRow> Transfers { get; } =
    [
        new("Win7AeroTheme.1.0.0.nupkg", "100%", "Complete"),
        new("Gallery screenshot.png", "72%", "Copying"),
        new("README.md", "100%", "Complete"),
        new("Symbols.zip", "18%", "Waiting")
    ];
}

public sealed record FileRow(string Name, string DateModified, string Type, string Size);

public sealed record TransferRow(string Item, string Progress, string Status);
