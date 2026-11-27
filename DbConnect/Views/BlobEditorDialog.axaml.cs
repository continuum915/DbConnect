using System.IO;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using DbConnect.Models;
using DbConnect.Services;

namespace DbConnect.Views;

public partial class BlobEditorDialog : Window
{
    private byte[]? _currentBytes;
    private TextBlock _infoBlock = null!;
    private TextBlock _hexBlock = null!;
    private TabItem _imageTab = null!;
    private Image _previewImage = null!;
    private TabControl _tabControl = null!;

    public BlobEditorDialog() { InitializeComponent(); } // designer

    public BlobEditorDialog(byte[]? data, string? columnName = null, bool allowEdit = false)
    {
        InitializeComponent();
        _currentBytes = data;

        _infoBlock = this.FindControl<TextBlock>("InfoBlock")!;
        _hexBlock = this.FindControl<TextBlock>("HexBlock")!;
        _imageTab = this.FindControl<TabItem>("ImageTab")!;
        _previewImage = this.FindControl<Image>("PreviewImage")!;
        _tabControl = this.FindControl<TabControl>("TabControl")!;

        if (columnName != null)
            Title = $"Binary Data — {columnName}";

        UpdateDisplay();

        var exportButton = this.FindControl<Button>("ExportButton")!;
        var importButton = this.FindControl<Button>("ImportButton")!;
        var setNullButton = this.FindControl<Button>("SetNullButton")!;
        var clearButton = this.FindControl<Button>("ClearButton")!;
        var okButton = this.FindControl<Button>("OkButton")!;
        var closeButton = this.FindControl<Button>("CloseButton")!;

        exportButton.Click += OnExportClicked;
        importButton.Click += async (_, _) => await OnImportClickedAsync();
        setNullButton.Click += (_, _) => Close(BlobEditResult.Null);
        clearButton.Click += (_, _) => Close(BlobEditResult.Empty);
        okButton.Click += (_, _) =>
        {
            if (_currentBytes == null)
                Close(BlobEditResult.Null);
            else
                Close(BlobEditResult.FromBytes(_currentBytes));
        };
        closeButton.Click += (_, _) => Close(null);

        if (allowEdit)
        {
            importButton.IsVisible = true;
            setNullButton.IsVisible = true;
            clearButton.IsVisible = true;
            okButton.IsVisible = true;
            closeButton.Content = "Cancel";
        }
        else
        {
            // Read-only: OK is hidden, so Close is the default as well as the cancel button.
            closeButton.IsDefault = true;
        }
    }

    private void UpdateDisplay()
    {
        var data = _currentBytes;

        if (data == null)
        {
            _infoBlock.Text = "<NULL>";
            _hexBlock.Text = "(null value)";
            _imageTab.IsVisible = false;
            return;
        }

        if (data.Length == 0)
        {
            _infoBlock.Text = "<empty> — 0 bytes";
            _hexBlock.Text = "(empty byte array)";
            _imageTab.IsVisible = false;
            return;
        }

        var format = ImageFormatDetector.Detect(data);
        _infoBlock.Text = format != null
            ? $"{FormatByteSize(data.Length)}  ·  {format}"
            : FormatByteSize(data.Length);

        _hexBlock.Text = HexDumpBuilder.Build(data);

        if (format != null)
        {
            try
            {
                using var ms = new MemoryStream(data);
                _previewImage.Source = new Bitmap(ms);
                _imageTab.IsVisible = true;
                _tabControl.SelectedItem = _imageTab;
                return;
            }
            catch { /* decoding failed */ }
        }

        _imageTab.IsVisible = false;
    }

    private static string FormatByteSize(int length)
    {
        if (length == 1) return "1 byte";
        if (length < 1024) return $"{length} bytes";
        if (length < 1024 * 1024) return $"{length:N0} bytes ({length / 1024.0:F1} KB)";
        return $"{length:N0} bytes ({length / (1024.0 * 1024):F2} MB)";
    }

    private async void OnExportClicked(object? sender, RoutedEventArgs e)
    {
        if (_currentBytes == null || _currentBytes.Length == 0) return;

        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export Binary Data",
            SuggestedFileName = "blob.bin",
            FileTypeChoices = [new FilePickerFileType("All Files") { Patterns = ["*.*"] }]
        });

        if (file == null) return;
        await using var stream = await file.OpenWriteAsync();
        await stream.WriteAsync(_currentBytes);
    }

    private async Task OnImportClickedAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import Binary Data",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("All Files") { Patterns = ["*.*"] }]
        });

        if (files.Count == 0) return;
        await using var stream = await files[0].OpenReadAsync();
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms);
        _currentBytes = ms.ToArray();
        UpdateDisplay();
    }
}
