using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows.Data;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using InfoDive.Models;
using InfoDive.Services;
using Microsoft.Win32;

namespace InfoDive;

public partial class MainWindow : Window
{
    // ── State ──
    private readonly ObservableCollection<Keyframe> _keyframes = [];

    // Images: the project may hold multiple. _loadedImage* mirrors the currently active one
    // for back-compat with the rest of the code that was written for a single image.
    private readonly ObservableCollection<LoadedImage> _images = [];
    private string _currentImageId = "";
    private BitmapImage? _loadedImage;
    private byte[]? _loadedImageBytes;
    private string? _loadedImageFileName;

    private string? _currentFilePath;
    private bool _isDirty;
    private string? _selectedKeyframeId;
    private bool _syncingImageSelector;

    // Image display mode: true = fit to screen, false = original size (100%)
    private bool _fitToScreen = true;

    // Pan/zoom state
    private double _currentScale = 1.0;
    private double _currentTx;
    private double _currentTy;
    private bool _isPanning;
    private Point _panStart;
    private double _panStartTx;
    private double _panStartTy;

    // Presentation state
    private bool _isPresenting;
    private int _presStep;
    // True after the user advances past the last scene: image has faded to black.
    // Previous/Home navigates back to the last scene; Next is a no-op; Escape exits.
    private bool _presEnded;
    private bool _isPresPanning;
    private Point _presPanStart;
    private double _presPanStartTx;
    private double _presPanStartTy;
    private double _presScale = 1.0;
    private double _presTx;
    private double _presTy;
    private bool _isBlackedOut;
    private bool _isKeyGuideVisible;
    private bool _isDrawMode;
    private bool _isEraserMode;
    private bool _isPresToolbarVisible = true;

    // Window state before presentation (for restore)
    private WindowStyle _savedWindowStyle;
    private WindowState _savedWindowState;
    private ResizeMode _savedResizeMode;
    private bool _savedTopmost;

    // Editor canvas dimensions captured at presentation start (for coordinate conversion)
    private double _editorCanvasWidth;
    private double _editorCanvasHeight;

    // Command-line launch mode
    private bool _autoPresent;

    private const double MinScale = 0.04;
    private const double MaxScale = 40.0;
    private const int MaxRecentFiles = 10;
    private readonly List<string> _recentFiles = [];

    // Per-image stroke collections. Each image keeps its own annotations so switching
    // images does not bleed drawings across them. Not persisted to .przip.
    private readonly Dictionary<string, StrokeCollection> _strokesByImage = new();

    // App-level settings (truly global — theme, recent files, etc.)
    private AppSettings _appSettings = new();

    // Project-level settings (persisted inside the .przip file)
    private ProjectSettings _projectSettings = new();

    // Undo/Redo for destructive actions (scene delete, image delete, clear all).
    private readonly UndoManager _undo = new();

    private ICollectionView? _keyframesView;

    public MainWindow()
    {
        InitializeComponent();

        // Wrap the keyframes collection in a CollectionView so we can apply live filtering
        // (driven by the search box) without mutating the underlying list.
        _keyframesView = CollectionViewSource.GetDefaultView(_keyframes);
        _keyframesView.Filter = KeyframeFilter;
        KeyframeList.ItemsSource = _keyframesView;

        ImageSelector.ItemsSource = _images;
        _keyframes.CollectionChanged += (_, _) => OnKeyframesChanged();

        // Load persisted app settings
        _appSettings = SettingsService.Load();
        ApplyScaleModeUI();

        // Strokes are bound to the active image's collection in SetActiveImage().

        // Set initial ink attributes
        var da = new DrawingAttributes
        {
            Color = Colors.Red,
            Width = 3,
            Height = 3,
            StylusTip = StylusTip.Ellipse,
            IsHighlighter = false,
        };
        PresInkCanvas.DefaultDrawingAttributes = da;

        Loaded += MainWindow_Loaded;
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        // Process command-line arguments.
        // Flags:
        //   -e / --editor   force editor mode
        //   -p / --present  force presentation mode
        //   (no flag)       use the mode saved in the .przip (ProjectSettings.DefaultLaunchMode)
        //
        // Shift held at launch time (e.g. Shift+double-click in Explorer) acts as an
        // implicit --editor, overriding the project's saved presentation mode. Explicit
        // --present still wins so scripted launches are unaffected.
        //
        // Positional file args:
        //   - Exactly one .przip → open project
        //   - One or more image / PDF files → load all (PDF expands to one image per page)
        //   - Mixed (.przip + images) → .przip wins, images are ignored
        // Surrounding double quotes on each arg are stripped defensively.
        var args = Environment.GetCommandLineArgs();
        var fileArgs = new List<string>();
        bool editFlag = false;
        bool presentFlag = false;

        for (int i = 1; i < args.Length; i++)
        {
            var a = args[i];
            if (a is "--editor" or "-e")
                editFlag = true;
            else if (a is "--present" or "-p")
                presentFlag = true;
            else if (!a.StartsWith('-'))
                fileArgs.Add(a.Trim('"'));
        }

        // Shift at launch → force editor mode (unless an explicit --present was passed)
        if ((Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift)
            editFlag = true;

        // Filter to existing files
        fileArgs = fileArgs.Where(File.Exists).ToList();
        if (fileArgs.Count == 0) return;

        // .przip takes precedence (first one wins, others ignored)
        var prZip = fileArgs.FirstOrDefault(p =>
            string.Equals(System.IO.Path.GetExtension(p), ".przip", StringComparison.OrdinalIgnoreCase));
        if (prZip != null)
        {
            OpenProjectFile(prZip);

            bool startPresent = presentFlag
                || (!editFlag && _projectSettings.DefaultLaunchMode == LaunchMode.Presentation);

            _autoPresent = startPresent;
            if (startPresent && _keyframes.Count > 0)
                StartPresentation(0);
            return;
        }

        // Otherwise, load all importable files (images + PDFs)
        var importable = fileArgs.Where(IsImportableFile).ToList();
        foreach (var file in importable)
            AddImagesFromFile(file);
    }

    // Extensions accepted by Add Image dialogs, command-line, and drag-and-drop.
    private static readonly HashSet<string> ImportableImageExts = new(StringComparer.OrdinalIgnoreCase)
        { ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".webp", ".svg" };

    private static bool IsImportableFile(string path)
    {
        var ext = System.IO.Path.GetExtension(path);
        return ImportableImageExts.Contains(ext)
            || PdfRasterizer.IsPdf(path)
            || DrawioRasterizer.IsDrawio(path);
    }

    /// <summary>
    /// Entry point used by every "add image" trigger (CLI args, file dialogs, drag-drop).
    /// PDFs are rasterized one image per page; other formats fall through to the existing
    /// single-image pipeline. All errors are surfaced via MessageBox here so callers don't
    /// need to repeat the try/catch.
    /// </summary>
    private void AddImagesFromFile(string filePath)
    {
        try
        {
            var bytes = File.ReadAllBytes(filePath);
            var fileName = System.IO.Path.GetFileName(filePath);

            if (PdfRasterizer.IsPdf(filePath))
            {
                List<(byte[] PngBytes, string FileName)> pages;
                try
                {
                    pages = PdfRasterizer.Rasterize(bytes, fileName);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"PDFの読み込みに失敗しました: {fileName}\n{ex.Message}", "エラー",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                foreach (var (pngBytes, pageName) in pages)
                    AddImageFromBytes(pngBytes, pageName, showToast: false);
                ShowToast($"PDFを追加: {fileName} ({pages.Count}ページ)");
            }
            else if (DrawioRasterizer.IsDrawio(filePath))
            {
                List<(byte[] PngBytes, string FileName)> pages;
                try
                {
                    pages = DrawioRasterizer.Rasterize(bytes, fileName);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"draw.io ファイルの読み込みに失敗しました: {fileName}\n\n{ex.Message}", "エラー",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                foreach (var (pngBytes, pageName) in pages)
                    AddImageFromBytes(pngBytes, pageName, showToast: false);
                ShowToast($"draw.ioを追加: {fileName} ({pages.Count}ページ)");
            }
            else
            {
                AddImageFromBytes(bytes, fileName);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"ファイルの読み込みに失敗しました: {filePath}\n{ex.Message}", "エラー",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ════════════════════════════════════════
    //  Image Loading
    // ════════════════════════════════════════

    private void LoadImageFromFile(string filePath)
    {
        try
        {
            var bytes = File.ReadAllBytes(filePath);
            LoadImageFromBytes(bytes, System.IO.Path.GetFileName(filePath));
        }
        catch (Exception ex)
        {
            MessageBox.Show($"画像の読み込みに失敗しました:\n{ex.Message}", "エラー",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// Replace all project images with a single one (drop/open-image entry point).
    /// </summary>
    private void LoadImageFromBytes(byte[] bytes, string fileName)
    {
        if (!TryNormalizeSvg(ref bytes, ref fileName)) return;
        var img = TryCreateLoadedImage(bytes, fileName);
        if (img == null) return;

        _images.Clear();
        _strokesByImage.Clear();
        _images.Add(img);
        SetActiveImage(img.Id, applyFitView: true);
        MarkDirty();
    }

    /// <summary>
    /// Append another image to the project without replacing the existing ones.
    /// Auto-renames duplicate filenames (foo.png → foo (2).png) so each entry has a unique
    /// display name and .przip entry name. <paramref name="showToast"/> can be suppressed
    /// when callers add many images in a batch (e.g. PDF pages) and prefer one summary toast.
    /// </summary>
    private void AddImageFromBytes(byte[] bytes, string fileName, bool showToast = true)
    {
        if (!TryNormalizeSvg(ref bytes, ref fileName)) return;
        var uniqueName = MakeUniqueImageName(fileName);
        var img = TryCreateLoadedImage(bytes, uniqueName);
        if (img == null) return;

        _images.Add(img);
        SetActiveImage(img.Id, applyFitView: true);
        MarkDirty();
        if (showToast)
            ShowToast($"画像を追加: {uniqueName}");
    }

    /// <summary>
    /// Returns a filename that doesn't collide with any existing image in _images
    /// (case-insensitive). Appends " (2)", " (3)", ... before the extension as needed.
    /// <paramref name="excludeId"/> lets the caller ignore a specific image (used when
    /// replacing that image in-place).
    /// </summary>
    private string MakeUniqueImageName(string fileName, string? excludeId = null)
    {
        var existing = _images
            .Where(i => excludeId == null || i.Id != excludeId)
            .Select(i => i.FileName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!existing.Contains(fileName)) return fileName;

        var baseName = System.IO.Path.GetFileNameWithoutExtension(fileName);
        var ext = System.IO.Path.GetExtension(fileName);
        for (var n = 2; n < 10000; n++)
        {
            var candidate = $"{baseName} ({n}){ext}";
            if (!existing.Contains(candidate)) return candidate;
        }
        return $"{baseName} ({Guid.NewGuid():N}){ext}";
    }

    /// <summary>
    /// If the input is SVG, rasterize to PNG bytes and rename foo.svg → foo.png so the
    /// rest of the pipeline (BitmapImage, .przip zip entries, PNG export) works unchanged.
    /// Returns false and shows an error dialog if rasterization fails.
    /// </summary>
    private static bool TryNormalizeSvg(ref byte[] bytes, ref string fileName)
    {
        if (!SvgRasterizer.IsSvg(fileName)) return true;
        try
        {
            bytes = SvgRasterizer.Rasterize(bytes);
            fileName = SvgRasterizer.ToPngFileName(fileName);
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show($"SVGの読み込みに失敗しました:\n{ex.Message}", "エラー",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    private static LoadedImage? TryCreateLoadedImage(byte[] bytes, string fileName)
    {
        BitmapImage bmp;
        try
        {
            bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.StreamSource = new MemoryStream(bytes);
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.EndInit();
            bmp.Freeze();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"画像の読み込みに失敗しました:\n{ex.Message}", "エラー",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return null;
        }

        if (bmp.PixelWidth > 8192 || bmp.PixelHeight > 8192)
        {
            var result = MessageBox.Show(
                $"画像サイズが {bmp.PixelWidth}x{bmp.PixelHeight} です。\n推奨は 8192x8192 以下です。続行しますか？",
                "警告", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (result != MessageBoxResult.Yes) return null;
        }

        return new LoadedImage
        {
            Id = "img_" + Guid.NewGuid().ToString("N")[..8],
            FileName = fileName,
            Bytes = bytes,
            Bitmap = bmp,
        };
    }

    /// <summary>
    /// Switch the active image (what's shown on editor + presentation canvases).
    /// </summary>
    private void SetActiveImage(string imageId, bool applyFitView)
    {
        var img = _images.FirstOrDefault(i => i.Id == imageId);
        if (img == null) return;

        _currentImageId = imageId;

        // Sync legacy single-image fields (the rest of the code relies on these)
        _loadedImage = img.Bitmap;
        _loadedImageBytes = img.Bytes;
        _loadedImageFileName = img.FileName;

        CanvasImage.Source = img.Bitmap;
        PresImage.Source = img.Bitmap;

        EditorInkCanvas.Width = img.Bitmap.PixelWidth;
        EditorInkCanvas.Height = img.Bitmap.PixelHeight;
        PresInkCanvas.Width = img.Bitmap.PixelWidth;
        PresInkCanvas.Height = img.Bitmap.PixelHeight;

        EditorInkCanvas.RenderTransform = CanvasImage.RenderTransform;
        PresInkCanvas.RenderTransform = PresImage.RenderTransform;

        // Swap in per-image strokes so drawings don't leak across images
        var strokes = GetStrokesForImage(imageId);
        EditorInkCanvas.Strokes = strokes;
        PresInkCanvas.Strokes = strokes;
        UpdateAnnotationIndicator();

        // Show canvas, hide drop zone
        DropZone.Visibility = Visibility.Collapsed;
        ImageCanvas.Visibility = Visibility.Visible;
        ZoomIndicator.Visibility = Visibility.Visible;
        CanvasHint.Visibility = Visibility.Visible;
        CanvasTools.Visibility = Visibility.Visible;
        AddKeyframeBtn.IsEnabled = true;
        AddImageBtn.IsEnabled = true;
        ReplaceImageBtn.IsEnabled = true;
        ImageSelector.IsEnabled = true;
        DeleteImageBtn.IsEnabled = true;

        // Sync ComboBox selection without re-triggering the handler
        _syncingImageSelector = true;
        try { ImageSelector.SelectedItem = img; }
        finally { _syncingImageSelector = false; }

        // Update keyframe highlight state (scenes belonging to the current image stand out)
        UpdateKeyframeHighlights();
        UpdateStatusImageInfo();

        if (applyFitView)
        {
            Dispatcher.InvokeAsync(() =>
            {
                if (_fitToScreen) FitView();
                else OriginalSizeView();
            }, System.Windows.Threading.DispatcherPriority.Loaded);
        }
    }

    private StrokeCollection GetStrokesForImage(string imageId)
    {
        if (!_strokesByImage.TryGetValue(imageId, out var sc))
        {
            sc = new StrokeCollection();
            sc.StrokesChanged += (_, _) =>
            {
                var img = _images.FirstOrDefault(i => i.Id == imageId);
                if (img != null) img.HasDrawings = sc.Count > 0;
                UpdateAnnotationIndicator();
            };
            _strokesByImage[imageId] = sc;
        }
        return sc;
    }

    // Strokes for the currently active image. When no image is active, returns
    // a throwaway empty collection so .Count / .Clear() callers stay safe.
    private StrokeCollection CurrentStrokes =>
        !string.IsNullOrEmpty(_currentImageId) && _strokesByImage.TryGetValue(_currentImageId, out var sc)
            ? sc
            : new StrokeCollection();

    /// <summary>
    /// Resolve which image a keyframe refers to. Empty/stale ImageId falls back to the first image.
    /// </summary>
    private string ResolveKeyframeImageId(Keyframe kf)
    {
        if (!string.IsNullOrEmpty(kf.ImageId) && _images.Any(i => i.Id == kf.ImageId))
            return kf.ImageId;
        return _images.FirstOrDefault()?.Id ?? "";
    }

    private void ImageSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingImageSelector) return;
        if (ImageSelector.SelectedItem is LoadedImage img && img.Id != _currentImageId)
            SetActiveImage(img.Id, applyFitView: true);
    }

    /// <summary>
    /// Generate ImageRefs with unique filenames (some zip readers require distinct entry names).
    /// Duplicates get a numeric suffix: foo.png → foo (1).png.
    /// </summary>
    private static List<ProjectFileService.ImageRef> BuildUniqueImageRefs(ObservableCollection<LoadedImage> images)
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<ProjectFileService.ImageRef>(images.Count);
        foreach (var img in images)
        {
            var baseName = System.IO.Path.GetFileNameWithoutExtension(img.FileName);
            var ext = System.IO.Path.GetExtension(img.FileName);
            var candidate = img.FileName;
            var n = 1;
            while (!used.Add(candidate))
                candidate = $"{baseName} ({n++}){ext}";
            result.Add(new ProjectFileService.ImageRef(
                Id: img.Id,
                FileName: candidate,
                Width: img.Bitmap.PixelWidth,
                Height: img.Bitmap.PixelHeight,
                Bytes: img.Bytes));
        }
        return result;
    }

    private void AddImage_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Filter = "画像 / PDF / draw.io|*.jpg;*.jpeg;*.png;*.bmp;*.gif;*.webp;*.svg;*.pdf;*.drawio;*.dio|画像ファイル|*.jpg;*.jpeg;*.png;*.bmp;*.gif;*.webp;*.svg|PDF (*.pdf)|*.pdf|draw.io (*.drawio;*.dio)|*.drawio;*.dio|すべてのファイル|*.*",
            Title = "画像を追加",
        };
        if (dlg.ShowDialog() == true)
            AddImagesFromFile(dlg.FileName);
    }

    // ── Image drag & drop reorder (within the ListBox) ──
    // Same pattern as the keyframe D&D: measure drag distance before committing, use a
    // custom data format so drops from elsewhere are ignored.
    private const string ImageDragFormat = "InfoDiveImage";
    private Point _imgDragStart;
    private string? _imgDragId;
    private bool _imgDragInitiated;

    private void Img_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        _imgDragStart = e.GetPosition(null);
        _imgDragId = (sender as FrameworkElement)?.Tag as string;
        _imgDragInitiated = false;
    }

    private void Img_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_imgDragInitiated) return;
        if (e.LeftButton != MouseButtonState.Pressed || _imgDragId == null) return;

        var delta = e.GetPosition(null) - _imgDragStart;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        _imgDragInitiated = true;
        if (sender is FrameworkElement el)
        {
            var data = new DataObject(ImageDragFormat, _imgDragId);
            try { DragDrop.DoDragDrop(el, data, DragDropEffects.Move); }
            catch { /* ignore */ }
        }
    }

    private void Img_DragEnter(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(ImageDragFormat)) { e.Effects = DragDropEffects.None; return; }
        e.Effects = DragDropEffects.Move;
        if (sender is Border b) b.Background = (Brush)FindResource("AccentBrush");
    }

    private void Img_DragLeave(object sender, DragEventArgs e)
    {
        if (sender is Border b) b.ClearValue(Border.BackgroundProperty);
    }

    private void Img_Drop(object sender, DragEventArgs e)
    {
        if (sender is Border b) b.ClearValue(Border.BackgroundProperty);
        if (!e.Data.GetDataPresent(ImageDragFormat)) return;
        if (sender is not FrameworkElement el || el.Tag is not string targetId) return;

        var sourceId = e.Data.GetData(ImageDragFormat) as string;
        if (string.IsNullOrEmpty(sourceId) || sourceId == targetId) return;

        var srcIdx = _images.Select((img, i) => new { img, i }).FirstOrDefault(x => x.img.Id == sourceId)?.i ?? -1;
        var tgtIdx = _images.Select((img, i) => new { img, i }).FirstOrDefault(x => x.img.Id == targetId)?.i ?? -1;
        if (srcIdx < 0 || tgtIdx < 0) return;

        var img = _images[srcIdx];
        _undo.Push(new MoveImageAction(_images, img, srcIdx, tgtIdx));
        _images.Move(srcIdx, tgtIdx);
        UpdateStatusImageInfo();
        UpdateKeyframeHighlights(); // first-image fallback for legacy keyframes may have shifted
        MarkDirty();
    }

    private void DeleteImage_Click(object sender, RoutedEventArgs e)
    {
        var img = _images.FirstOrDefault(i => i.Id == _currentImageId);
        if (img == null) return;

        // Find keyframes using this image (empty ImageId → first image, which is also this one if it's the first)
        var isFirst = _images[0].Id == img.Id;
        var linkedKeyframes = _keyframes
            .Where(k => k.ImageId == img.Id || (isFirst && string.IsNullOrEmpty(k.ImageId)))
            .ToList();

        string message;
        if (linkedKeyframes.Count > 0)
            message = $"画像「{img.FileName}」を削除します。\n" +
                      $"この画像を使うシーン {linkedKeyframes.Count} 件も一緒に削除されます。\n\n" +
                      $"続行しますか？（Ctrl+Z で元に戻せます）";
        else
            message = $"画像「{img.FileName}」を削除します。\n続行しますか？（Ctrl+Z で元に戻せます）";

        var result = MessageBox.Show(message, "画像を削除",
            MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes) return;

        // Capture indices BEFORE mutation for accurate undo restoration
        var imgIndex = _images.IndexOf(img);
        var linkedWithIndices = linkedKeyframes.Select(k => (k, _keyframes.IndexOf(k))).ToList();
        _undo.Push(new DeleteImageAction(
            _images, _keyframes, img, imgIndex, linkedWithIndices,
            id => SetActiveImage(id, applyFitView: true)));

        // Remove linked keyframes
        foreach (var kf in linkedKeyframes)
            _keyframes.Remove(kf);

        // Remove the image
        _images.Remove(img);
        _currentImageId = "";

        // If images remain, show the first one; otherwise reset to empty state
        if (_images.Count > 0)
        {
            SetActiveImage(_images[0].Id, applyFitView: true);
        }
        else
        {
            _loadedImage = null;
            _loadedImageBytes = null;
            _loadedImageFileName = null;
            CanvasImage.Source = null;
            PresImage.Source = null;
            DropZone.Visibility = Visibility.Visible;
            ImageCanvas.Visibility = Visibility.Collapsed;
            ZoomIndicator.Visibility = Visibility.Collapsed;
            CanvasHint.Visibility = Visibility.Collapsed;
            CanvasTools.Visibility = Visibility.Collapsed;
            AddKeyframeBtn.IsEnabled = false;
            ReplaceImageBtn.IsEnabled = false;
            ImageSelector.IsEnabled = false;
            DeleteImageBtn.IsEnabled = false;
        }

        RefreshKeyframeListUI();
        UpdateStatusImageInfo();
        MarkDirty();
        var tail = linkedKeyframes.Count > 0
            ? $"（+ シーン {linkedKeyframes.Count} 件）Ctrl+Z で元に戻せます"
            : "Ctrl+Z で元に戻せます";
        ShowToast($"削除: {img.FileName} {tail}");
    }

    private void FitView()
    {
        if (_loadedImage == null) return;
        var cw = CanvasArea.ActualWidth;
        var ch = CanvasArea.ActualHeight;
        if (cw <= 0 || ch <= 0) return;

        var iw = _loadedImage.PixelWidth;
        var ih = _loadedImage.PixelHeight;
        var sc = Math.Min(cw / iw, ch / ih) * 0.92;

        SetView((cw - iw * sc) / 2, (ch - ih * sc) / 2, sc);
    }

    /// <summary>
    /// Keep the image pixel at the canvas center anchored when the canvas resizes
    /// (window resize, sidebar toggle, etc.). Without this, Tx/Ty are absolute offsets
    /// so the image appears to slide toward a corner when the canvas grows.
    /// </summary>
    private void CanvasArea_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_loadedImage == null) return;
        if (e.PreviousSize.Width <= 0 || e.PreviousSize.Height <= 0) return;
        if (_currentScale <= 0) return;

        var oldCW = e.PreviousSize.Width;
        var oldCH = e.PreviousSize.Height;
        var newCW = e.NewSize.Width;
        var newCH = e.NewSize.Height;

        // Image pixel at the old viewport center
        var imgCenterX = (oldCW / 2 - _currentTx) / _currentScale;
        var imgCenterY = (oldCH / 2 - _currentTy) / _currentScale;

        // Place the same image pixel at the new viewport center
        var newTx = newCW / 2 - imgCenterX * _currentScale;
        var newTy = newCH / 2 - imgCenterY * _currentScale;

        SetView(newTx, newTy, _currentScale);
    }

    private void OriginalSizeView()
    {
        if (_loadedImage == null) return;
        var cw = CanvasArea.ActualWidth;
        var ch = CanvasArea.ActualHeight;
        if (cw <= 0 || ch <= 0) return;

        var iw = _loadedImage.PixelWidth;
        var ih = _loadedImage.PixelHeight;
        // Center at 100%
        SetView((cw - iw) / 2, (ch - ih) / 2, 1.0);
    }

    private void SetView(double tx, double ty, double sc, bool animate = false, int durationMs = 0)
    {
        sc = Math.Clamp(sc, MinScale, MaxScale);
        _currentTx = tx;
        _currentTy = ty;
        _currentScale = sc;

        if (animate && durationMs > 0)
        {
            var duration = TimeSpan.FromMilliseconds(durationMs);
            var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };

            ImageScale.BeginAnimation(ScaleTransform.ScaleXProperty,
                new DoubleAnimation(sc, duration) { EasingFunction = ease });
            ImageScale.BeginAnimation(ScaleTransform.ScaleYProperty,
                new DoubleAnimation(sc, duration) { EasingFunction = ease });
            ImageTranslate.BeginAnimation(TranslateTransform.XProperty,
                new DoubleAnimation(tx, duration) { EasingFunction = ease });
            ImageTranslate.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(ty, duration) { EasingFunction = ease });
        }
        else
        {
            ImageScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            ImageScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            ImageTranslate.BeginAnimation(TranslateTransform.XProperty, null);
            ImageTranslate.BeginAnimation(TranslateTransform.YProperty, null);

            ImageScale.ScaleX = sc;
            ImageScale.ScaleY = sc;
            ImageTranslate.X = tx;
            ImageTranslate.Y = ty;
        }

        UpdateZoomDisplay();
    }

    private void UpdateZoomDisplay()
    {
        var pct = Math.Round(_currentScale * 100);
        ZoomText.Text = $"{pct}%";
        StatusZoom.Text = $"Zoom: {pct}%";
    }

    // ════════════════════════════════════════
    //  Pan / Zoom - Editor Canvas
    // ════════════════════════════════════════

    private void ImageCanvas_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            _isPanning = true;
            _panStart = e.GetPosition(CanvasArea);
            _panStartTx = _currentTx;
            _panStartTy = _currentTy;
            ImageCanvas.Cursor = Cursors.ScrollAll;
            ImageCanvas.CaptureMouse();
            e.Handled = true;
        }
    }

    private void ImageCanvas_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isPanning) return;
        var pos = e.GetPosition(CanvasArea);
        var dx = pos.X - _panStart.X;
        var dy = pos.Y - _panStart.Y;
        SetView(_panStartTx + dx, _panStartTy + dy, _currentScale);
    }

    private void ImageCanvas_MouseUp(object sender, MouseButtonEventArgs e)
    {
        _isPanning = false;
        ImageCanvas.Cursor = Cursors.Hand;
        ImageCanvas.ReleaseMouseCapture();
    }

    private void ImageCanvas_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        var pos = e.GetPosition(CanvasArea);
        var factor = e.Delta > 0 ? ZoomInFactor() : ZoomOutFactor();
        ZoomAtPoint(pos.X, pos.Y, factor);
        e.Handled = true;
    }

    private void ZoomAtPoint(double cx, double cy, double factor)
    {
        var newScale = Math.Clamp(_currentScale * factor, MinScale, MaxScale);
        var r = newScale / _currentScale;
        var newTx = cx - (cx - _currentTx) * r;
        var newTy = cy - (cy - _currentTy) * r;
        SetView(newTx, newTy, newScale);
    }

    private void ZoomCenter(double factor)
    {
        var cx = CanvasArea.ActualWidth / 2;
        var cy = CanvasArea.ActualHeight / 2;
        ZoomAtPoint(cx, cy, factor);
    }

    // ════════════════════════════════════════
    //  Keyframe Management
    // ════════════════════════════════════════

    private void AddKeyframe_Click(object sender, RoutedEventArgs e)
    {
        var kf = new Keyframe
        {
            Id = Guid.NewGuid().ToString("N")[..8],
            Name = $"シーン {_keyframes.Count + 1}",
            ImageId = _currentImageId,
            Tx = _currentTx,
            Ty = _currentTy,
            Scale = _currentScale,
            DurationMs = 1200,
            RefCanvasW = CanvasArea.ActualWidth,
            RefCanvasH = CanvasArea.ActualHeight,
        };
        // Insert after the selected scene; append to the end if none is selected.
        var insertAt = _keyframes.Count;
        if (_selectedKeyframeId != null)
        {
            var selIdx = IndexOfKeyframe(_selectedKeyframeId);
            if (selIdx >= 0) insertAt = selIdx + 1;
        }
        _keyframes.Insert(insertAt, kf);
        _undo.Push(new AddKeyframeAction(_keyframes, kf, insertAt));
        _selectedKeyframeId = kf.Id;
        MarkDirty();
        RefreshKeyframeListUI();
        ScrollKeyframeIntoView(kf);
    }

    // Ensure the given keyframe's card is visible in the scene list scroller.
    private void ScrollKeyframeIntoView(Keyframe kf)
    {
        KeyframeList.UpdateLayout();
        if (KeyframeList.ItemContainerGenerator.ContainerFromItem(kf) is FrameworkElement container)
            container.BringIntoView();
    }

    // ── Keyframe drag & drop reorder ──
    // Strategy: track mouse-down position on the card; if movement exceeds the OS drag threshold,
    // initiate DoDragDrop with the keyframe Id. Otherwise treat as a plain click on MouseUp.
    private const string KeyframeDragFormat = "InfoDiveKeyframe";
    private Point _kfDragStart;
    private string? _kfDragId;
    private bool _kfDragInitiated;

    private void Kf_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        // Only initiate drag from empty space on the card — buttons inside swallow their own events
        // via e.Handled, but PreviewMouseDown still fires first. We record intent here; actual drag
        // only starts if the mouse moves past the threshold.
        _kfDragStart = e.GetPosition(null);
        _kfDragId = (sender as FrameworkElement)?.Tag as string;
        _kfDragInitiated = false;
    }

    private void Kf_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_kfDragInitiated) return;
        if (e.LeftButton != MouseButtonState.Pressed || _kfDragId == null) return;

        var delta = e.GetPosition(null) - _kfDragStart;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        _kfDragInitiated = true;
        if (sender is FrameworkElement el)
        {
            var data = new DataObject(KeyframeDragFormat, _kfDragId);
            try { DragDrop.DoDragDrop(el, data, DragDropEffects.Move); }
            catch { /* DragDrop can throw when source disappears mid-drag; ignore */ }
        }
    }

    private void Kf_MouseUp(object sender, MouseButtonEventArgs e)
    {
        // If a drag was initiated, the drop handler (or cancel) already did the work.
        // Otherwise treat as a plain click → go to this keyframe.
        if (_kfDragInitiated)
        {
            _kfDragInitiated = false;
            _kfDragId = null;
            return;
        }

        if (sender is FrameworkElement el && el.Tag is string id)
        {
            var kf = _keyframes.FirstOrDefault(k => k.Id == id);
            if (kf != null) GoToKeyframe(kf, animate: true);
        }
        _kfDragId = null;
    }

    private void Kf_DragEnter(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(KeyframeDragFormat)) { e.Effects = DragDropEffects.None; return; }
        e.Effects = DragDropEffects.Move;
        if (sender is Border b) b.BorderBrush = (Brush)FindResource("AccentBrush");
    }

    private void Kf_DragLeave(object sender, DragEventArgs e)
    {
        // Restore the style-driven border — clearing the local value lets the style win again
        if (sender is Border b) b.ClearValue(Border.BorderBrushProperty);
    }

    private void Kf_Drop(object sender, DragEventArgs e)
    {
        if (sender is Border b) b.ClearValue(Border.BorderBrushProperty);
        if (!e.Data.GetDataPresent(KeyframeDragFormat)) return;
        if (sender is not FrameworkElement el || el.Tag is not string targetId) return;

        var sourceId = e.Data.GetData(KeyframeDragFormat) as string;
        if (string.IsNullOrEmpty(sourceId) || sourceId == targetId) return;

        var srcIdx = IndexOfKeyframe(sourceId);
        var tgtIdx = IndexOfKeyframe(targetId);
        if (srcIdx < 0 || tgtIdx < 0) return;

        var kf = _keyframes[srcIdx];
        _undo.Push(new MoveKeyframeAction(_keyframes, kf, srcIdx, tgtIdx));
        _keyframes.Move(srcIdx, tgtIdx);
        MarkDirty();
    }

    private void GoToKeyframe(Keyframe kf, bool animate = true)
    {
        _selectedKeyframeId = kf.Id;
        UpdateKeyframeSelection();

        // Switch to this keyframe's image if needed
        var targetImgId = ResolveKeyframeImageId(kf);
        if (!string.IsNullOrEmpty(targetImgId) && targetImgId != _currentImageId)
            SetActiveImage(targetImgId, applyFitView: false);

        if (animate)
            SetView(kf.Tx, kf.Ty, kf.Scale, animate: true, durationMs: kf.DurationMs);
        else
            SetView(kf.Tx, kf.Ty, kf.Scale);
        RefreshKeyframeListUI();
    }

    private void EditKeyframeName_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement el && el.Tag is string id)
        {
            var kf = _keyframes.FirstOrDefault(k => k.Id == id);
            if (kf == null) return;

            var dialog = new InputDialog("シーン名を編集", kf.Name);
            dialog.Owner = this;
            if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.Result))
            {
                var newName = dialog.Result;
                var action = EditKeyframeAction.Capture(kf,
                    $"シーン名の変更 ({kf.Name} → {newName})",
                    () => kf.Name = newName);
                if (action != null) _undo.Push(action);
                MarkDirty();
                RefreshKeyframeListUI();
            }
        }
        e.Handled = true;
    }

    private void UpdateKeyframeView_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement el && el.Tag is string id)
        {
            var kf = _keyframes.FirstOrDefault(k => k.Id == id);
            if (kf != null)
            {
                var action = EditKeyframeAction.Capture(kf,
                    $"シーン「{kf.Name}」のビュー更新",
                    () =>
                    {
                        kf.Tx = _currentTx;
                        kf.Ty = _currentTy;
                        kf.Scale = _currentScale;
                        kf.RefCanvasW = CanvasArea.ActualWidth;
                        kf.RefCanvasH = CanvasArea.ActualHeight;
                        kf.ImageId = _currentImageId;
                    });
                if (action != null) _undo.Push(action);
                MarkDirty();
            }
        }
        e.Handled = true;
    }

    private void PreviewKeyframe_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement el && el.Tag is string id)
        {
            var kf = _keyframes.FirstOrDefault(k => k.Id == id);
            if (kf != null)
                GoToKeyframe(kf, animate: true);
        }
        e.Handled = true;
    }

    private void MoveKeyframeUp_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement el && el.Tag is string id)
        {
            var idx = IndexOfKeyframe(id);
            if (idx > 0)
            {
                var kf = _keyframes[idx];
                _undo.Push(new MoveKeyframeAction(_keyframes, kf, idx, idx - 1));
                _keyframes.Move(idx, idx - 1);
                MarkDirty();
            }
        }
        e.Handled = true;
    }

    private void MoveKeyframeDown_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement el && el.Tag is string id)
        {
            var idx = IndexOfKeyframe(id);
            if (idx >= 0 && idx < _keyframes.Count - 1)
            {
                var kf = _keyframes[idx];
                _undo.Push(new MoveKeyframeAction(_keyframes, kf, idx, idx + 1));
                _keyframes.Move(idx, idx + 1);
                MarkDirty();
            }
        }
        e.Handled = true;
    }

    private void DuplicateKeyframe_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement el && el.Tag is string id)
        {
            var kf = _keyframes.FirstOrDefault(k => k.Id == id);
            if (kf != null)
            {
                var idx = IndexOfKeyframe(id);
                var clone = kf.Clone();
                _keyframes.Insert(idx + 1, clone);
                _undo.Push(new AddKeyframeAction(_keyframes, clone, idx + 1));
                MarkDirty();
            }
        }
        e.Handled = true;
    }

    // ── Keyframe in-line editors: Undo for duration slider + image-transition combo ──
    // Strategy: remember the "before" value on drag-start / drop-open. On drag-end / drop-close,
    // compare and push an EditKeyframeAction if the value actually changed. This produces one
    // undo entry per edit gesture (not one per intermediate slider tick).

    private readonly Dictionary<Slider, int> _durationDragStart = new();
    private readonly Dictionary<ComboBox, ImageTransition> _transitionOpenValue = new();

    private void DurationSlider_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is Slider s && s.DataContext is Keyframe kf)
            _durationDragStart[s] = kf.DurationMs;
    }

    private void DurationSlider_PreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Slider s || s.DataContext is not Keyframe kf) return;
        if (!_durationDragStart.Remove(s, out var startMs)) return;
        if (startMs == kf.DurationMs) return;

        var after = KeyframeSnapshot.From(kf);
        var before = after with { DurationMs = startMs };
        _undo.Push(new EditKeyframeAction(kf, before, after, $"「{kf.Name}」の遷移時間変更"));
    }

    private void DurationSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        // Continuous change during drag — mark the project dirty. Undo is only pushed on drop.
        if (sender is Slider s && s.IsLoaded && Math.Abs(e.OldValue - e.NewValue) > 0.5)
            MarkDirty();
    }

    private void TransitionCombo_DropDownOpened(object sender, EventArgs e)
    {
        if (sender is ComboBox cb && cb.DataContext is Keyframe kf)
            _transitionOpenValue[cb] = kf.ImageTransition;
    }

    private void TransitionCombo_DropDownClosed(object sender, EventArgs e)
    {
        if (sender is not ComboBox cb || cb.DataContext is not Keyframe kf) return;
        if (!_transitionOpenValue.Remove(cb, out var openValue)) return;
        if (openValue == kf.ImageTransition) return;

        var after = KeyframeSnapshot.From(kf);
        var before = after with { ImageTransition = openValue };
        _undo.Push(new EditKeyframeAction(kf, before, after, $"「{kf.Name}」の画像切替効果変更"));
    }

    private void TransitionCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox cb && cb.IsLoaded)
            MarkDirty();
    }

    private void ToggleKeyframeExpand_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement el && el.Tag is string id)
        {
            var kf = _keyframes.FirstOrDefault(k => k.Id == id);
            if (kf != null) kf.IsExpanded = !kf.IsExpanded;
        }
        e.Handled = true;
    }

    private void DeleteKeyframe_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement el && el.Tag is string id)
        {
            var kf = _keyframes.FirstOrDefault(k => k.Id == id);
            if (kf != null)
            {
                var index = _keyframes.IndexOf(kf);
                _undo.Push(new DeleteKeyframeAction(_keyframes, kf, index));
                _keyframes.Remove(kf);
                if (_selectedKeyframeId == id)
                    _selectedKeyframeId = null;
                MarkDirty();
            }
        }
        e.Handled = true;
    }

    private int IndexOfKeyframe(string id) =>
        Enumerable.Range(0, _keyframes.Count).FirstOrDefault(i => _keyframes[i].Id == id, -1);

    private void OnKeyframesChanged()
    {
        var hasKf = _keyframes.Count > 0;
        EmptyHint.Visibility = hasKf ? Visibility.Collapsed : Visibility.Visible;
        KeyframeList.Visibility = hasKf ? Visibility.Visible : Visibility.Collapsed;
        StartPresBtn.IsEnabled = hasKf;
        SceneCountRun.Text = $"({_keyframes.Count}シーン)";
        StatusSceneInfo.Text = $"シーン: {_keyframes.Count}";
        UpdateImageSceneCounts();
    }

    private void RefreshKeyframeListUI()
    {
        // Re-apply filter so any just-added/renamed items show up correctly
        _keyframesView?.Refresh();
        UpdateKeyframeHighlights();
        UpdateKeyframeSelection();
    }

    private bool KeyframeFilter(object item)
    {
        var query = KeyframeFilterBox?.Text;
        if (string.IsNullOrWhiteSpace(query)) return true;
        if (item is not Keyframe kf) return true;
        return kf.Name.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    private void KeyframeFilterBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _keyframesView?.Refresh();
    }

    /// <summary>
    /// Briefly display a floating notification at the top of the window.
    /// Fades in (150ms), stays (1.5s), fades out (400ms).
    /// </summary>
    private void ShowToast(string message, string icon = "\u2713")
    {
        ToastText.Text = message;
        ToastIcon.Text = icon;

        var fadeIn = new DoubleAnimation(0, 0.95, TimeSpan.FromMilliseconds(150));
        var fadeOut = new DoubleAnimation(0.95, 0, TimeSpan.FromMilliseconds(400))
        {
            BeginTime = TimeSpan.FromMilliseconds(150 + 1500),
        };
        var storyboard = new Storyboard();
        Storyboard.SetTarget(fadeIn, ToastOverlay);
        Storyboard.SetTargetProperty(fadeIn, new PropertyPath(UIElement.OpacityProperty));
        Storyboard.SetTarget(fadeOut, ToastOverlay);
        Storyboard.SetTargetProperty(fadeOut, new PropertyPath(UIElement.OpacityProperty));
        storyboard.Children.Add(fadeIn);
        storyboard.Children.Add(fadeOut);
        storyboard.Begin();
    }

    /// <summary>
    /// Mark each keyframe as "current image" or not, so the UI can visually distinguish
    /// scenes that belong to the currently displayed image.
    /// </summary>
    private void UpdateKeyframeHighlights()
    {
        foreach (var kf in _keyframes)
        {
            var resolved = ResolveKeyframeImageId(kf);
            kf.IsForCurrentImage = !string.IsNullOrEmpty(_currentImageId)
                                   && resolved == _currentImageId;
        }
        UpdateImageSceneCounts();
    }

    /// <summary>
    /// Sync the IsSelected flag on each keyframe to _selectedKeyframeId so the list highlights
    /// the current selection. Call this after any change to _selectedKeyframeId.
    /// </summary>
    private void UpdateKeyframeSelection()
    {
        foreach (var kf in _keyframes)
            kf.IsSelected = kf.Id == _selectedKeyframeId;
    }

    // ── Keyboard shortcut helpers (editor mode) ──

    private void SwitchAdjacentImage(int delta)
    {
        if (_images.Count <= 1) return;
        var idx = _images.Select((img, i) => new { img, i }).FirstOrDefault(x => x.img.Id == _currentImageId)?.i ?? 0;
        var newIdx = (idx + delta + _images.Count) % _images.Count;
        SetActiveImage(_images[newIdx].Id, applyFitView: true);
    }

    // Shift+←/→ during presentation: jump to the first keyframe belonging to the
    // next (or previous) image in _images order. No wrap-around:
    //  - past the last image → fade to end (same as advancing beyond the last scene)
    //  - past the first image → no-op
    // Images without keyframes are skipped.
    private void PresSwitchImage(int delta)
    {
        if (_keyframes.Count == 0) return;

        // Walk keyframes in scenario order and collect unique image IDs at their
        // first appearance — this is the same ordering used by Shift+1..9 jumps.
        var seen = new HashSet<string>();
        var scenarioImages = new List<(string ImageId, int KfIndex)>();
        for (var i = 0; i < _keyframes.Count; i++)
        {
            var imgId = ResolveKeyframeImageId(_keyframes[i]);
            if (string.IsNullOrEmpty(imgId) || !seen.Add(imgId)) continue;
            scenarioImages.Add((imgId, i));
        }
        if (scenarioImages.Count == 0) return;

        // From the end-fade screen, treat Shift+← as "un-fade back to the current
        // image" (its first keyframe), not as a full image step backward.
        if (_presEnded && delta < 0)
        {
            var curKfIdx = _keyframes.Select((k, i) => new { k, i })
                .FirstOrDefault(x => ResolveKeyframeImageId(x.k) == _currentImageId)?.i ?? -1;
            if (curKfIdx >= 0)
            {
                PresGoTo(curKfIdx);
                return;
            }
        }

        var currIdx = scenarioImages.FindIndex(x => x.ImageId == _currentImageId);
        if (currIdx < 0) currIdx = delta > 0 ? -1 : scenarioImages.Count;
        var nextIdx = currIdx + delta;
        if (nextIdx >= 0 && nextIdx < scenarioImages.Count)
        {
            PresGoTo(scenarioImages[nextIdx].KfIndex);
            return;
        }
        // Walked past the end in the requested direction.
        if (delta > 0 && !_presEnded)
            FadeToEnd();
    }

    private void SelectAdjacentKeyframe(int delta)
    {
        if (_keyframes.Count == 0) return;
        var currentIdx = _selectedKeyframeId != null
            ? _keyframes.Select((k, i) => new { k, i }).FirstOrDefault(x => x.k.Id == _selectedKeyframeId)?.i ?? -1
            : -1;
        var newIdx = currentIdx < 0
            ? (delta > 0 ? 0 : _keyframes.Count - 1)
            : Math.Clamp(currentIdx + delta, 0, _keyframes.Count - 1);
        GoToKeyframe(_keyframes[newIdx], animate: true);
    }

    private void RenameSelectedKeyframe()
    {
        if (_selectedKeyframeId == null) return;
        var kf = _keyframes.FirstOrDefault(k => k.Id == _selectedKeyframeId);
        if (kf == null) return;

        var dialog = new InputDialog("シーン名を編集", kf.Name) { Owner = this };
        if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.Result))
        {
            var newName = dialog.Result;
            var action = EditKeyframeAction.Capture(kf,
                $"シーン名の変更 ({kf.Name} → {newName})",
                () => kf.Name = newName);
            if (action != null) _undo.Push(action);
            MarkDirty();
        }
    }

    private void DeleteSelectedKeyframe()
    {
        if (_selectedKeyframeId == null) return;
        var kf = _keyframes.FirstOrDefault(k => k.Id == _selectedKeyframeId);
        if (kf == null) return;

        var idx = _keyframes.IndexOf(kf);
        _undo.Push(new DeleteKeyframeAction(_keyframes, kf, idx));
        _keyframes.Remove(kf);
        _selectedKeyframeId = null;
        MarkDirty();
    }

    private void DuplicateSelectedKeyframe()
    {
        if (_selectedKeyframeId == null) return;
        var kf = _keyframes.FirstOrDefault(k => k.Id == _selectedKeyframeId);
        if (kf == null) return;

        var idx = _keyframes.IndexOf(kf);
        var clone = kf.Clone();
        _keyframes.Insert(idx + 1, clone);
        _undo.Push(new AddKeyframeAction(_keyframes, clone, idx + 1));
        _selectedKeyframeId = clone.Id;
        UpdateKeyframeSelection();
        MarkDirty();
    }

    /// <summary>
    /// Recompute SceneCount for every image so the picker stays in sync with the keyframe list.
    /// </summary>
    private void UpdateImageSceneCounts()
    {
        if (_images.Count == 0) { UpdateStatusImageInfo(); return; }
        var counts = new Dictionary<string, int>(_images.Count);
        foreach (var img in _images) counts[img.Id] = 0;

        var firstId = _images[0].Id;
        foreach (var kf in _keyframes)
        {
            var id = string.IsNullOrEmpty(kf.ImageId) ? firstId : kf.ImageId;
            if (counts.TryGetValue(id, out var val)) counts[id] = val + 1;
            else counts[firstId]++; // orphaned kf → attribute to first (legacy fallback)
        }
        foreach (var img in _images)
            img.SceneCount = counts[img.Id];

        UpdateStatusImageInfo();
    }

    /// <summary>
    /// Refresh the status bar's image indicator. Shows "画像: 2/5 — foo.png" (index of total and name)
    /// for multi-image projects, just the filename for single-image, empty otherwise.
    /// </summary>
    private void UpdateStatusImageInfo()
    {
        if (_images.Count == 0) { StatusImageInfo.Text = ""; return; }
        var current = _images.FirstOrDefault(i => i.Id == _currentImageId);
        if (current == null) { StatusImageInfo.Text = ""; return; }
        var idx = _images.IndexOf(current) + 1;
        StatusImageInfo.Text = _images.Count > 1
            ? $"画像: {idx}/{_images.Count} — {current.FileName}"
            : $"画像: {current.FileName}";
    }

    // ════════════════════════════════════════
    //  Presentation Mode
    // ════════════════════════════════════════

    private void StartPresentation(int startStep = 0)
    {
        if (_keyframes.Count == 0 || _loadedImage == null) return;

        // Capture editor canvas dimensions before hiding (needed for coordinate conversion)
        _editorCanvasWidth = CanvasArea.ActualWidth;
        _editorCanvasHeight = CanvasArea.ActualHeight;

        _isPresenting = true;
        _presStep = startStep;
        _presEnded = false;
        _isBlackedOut = false;
        _isKeyGuideVisible = false;
        _isDrawMode = false;
        _isEraserMode = false;
        _isPresToolbarVisible = true;

        BlackoutOverlay.Visibility = Visibility.Collapsed;
        KeyGuideOverlay.Visibility = Visibility.Collapsed;
        DrawToolbar.Visibility = Visibility.Collapsed;
        PresInkCanvas.EditingMode = InkCanvasEditingMode.None;
        PresInkCanvas.IsHitTestVisible = false;
        PresToolbar.Visibility = Visibility.Visible;
        PresToolbarShowBtn.Visibility = Visibility.Collapsed;

        // Save window state and go fullscreen
        _savedWindowStyle = WindowStyle;
        _savedWindowState = WindowState;
        _savedResizeMode = ResizeMode;
        _savedTopmost = Topmost;

        // NoResize removes WS_THICKFRAME so Maximized covers the full screen including taskbar
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        Topmost = true;
        // Force through Normal so WPF recalculates maximize bounds with the new style
        if (WindowState == WindowState.Maximized)
            WindowState = WindowState.Normal;
        WindowState = WindowState.Maximized;

        EditorGrid.Visibility = Visibility.Collapsed;
        PresentationGrid.Visibility = Visibility.Visible;

        // Force IME off — otherwise Space/D/etc. get swallowed by the IME and never reach us.
        // Moving focus into PresentationGrid (marked IsInputMethodEnabled=False) also keeps
        // IME off while presenting, even if the user later clicks something.
        InputMethod.Current.ImeState = InputMethodState.Off;
        Keyboard.Focus(PresentationGrid);

        // Hide image until positioned to avoid a brief flash at wrong location
        PresImage.Opacity = 0;
        PresInkCanvas.Opacity = 0;

        // Apply view only after PresCanvas reaches its final fullscreen size.
        // SizeChanged on PresCanvas + ContextIdle fallback handles both cases:
        //   (a) window resizes from non-maximized → SizeChanged fires
        //   (b) already at fullscreen size → fallback fires
        bool viewApplied = false;
        SizeChangedEventHandler? handler = null;
        void ApplyView()
        {
            if (viewApplied) return;
            viewApplied = true;
            if (handler != null) PresCanvas.SizeChanged -= handler;
            LogZoomDiag($"--- StartPresentation step={_presStep} ---");
            var kf = _keyframes[_presStep];
            // Switch to this keyframe's image before computing the pres view
            var targetImgId = ResolveKeyframeImageId(kf);
            if (!string.IsNullOrEmpty(targetImgId) && targetImgId != _currentImageId)
                SetActiveImage(targetImgId, applyFitView: false);
            var (tx, ty, sc) = EditorToPresView(kf);
            SetPresView(tx, ty, sc);
            // Clear any lingering fade animation (from a previous FadeToEnd), then force opacity to 1.
            PresImage.BeginAnimation(UIElement.OpacityProperty, null);
            PresInkCanvas.BeginAnimation(UIElement.OpacityProperty, null);
            PresImage.Opacity = 1;
            PresInkCanvas.Opacity = 1;
        }
        handler = (_, _) => ApplyView();
        PresCanvas.SizeChanged += handler;
        Dispatcher.InvokeAsync(ApplyView, System.Windows.Threading.DispatcherPriority.ContextIdle);
        UpdatePresUI();
    }

    private void EndPresentation()
    {
        _isPresenting = false;
        _isDrawMode = false;
        PresentationGrid.Visibility = Visibility.Collapsed;
        EditorGrid.Visibility = Visibility.Visible;

        // Restore window state (style/resize first so maximize goes to correct area)
        Topmost = _savedTopmost;
        ResizeMode = _savedResizeMode;
        WindowStyle = _savedWindowStyle;
        WindowState = _savedWindowState;

        // Show annotation indicator in editor if strokes exist
        UpdateAnnotationIndicator();

        // If launched with --present, close the app on exit
        if (_autoPresent)
            Close();
    }

    private void PresGoTo(int step, bool animate = true)
    {
        if (_keyframes.Count == 0) return;

        // Advancing past the last scene: fade to black and stop.
        // The user must press Previous/Home (or Escape to exit) to come back.
        if (step >= _keyframes.Count)
        {
            if (!_presEnded) FadeToEnd();
            return;
        }
        if (step < 0) return;

        // Returning from the "ended" state: fade the image back in first.
        var leavingEnd = _presEnded;
        if (leavingEnd) FadeFromEnd();

        _presStep = step;
        var kf = _keyframes[_presStep];

        var targetImgId = ResolveKeyframeImageId(kf);
        var imageChanged = !string.IsNullOrEmpty(targetImgId) && targetImgId != _currentImageId;

        if (!imageChanged)
        {
            // Same image — smooth pan/zoom animation as before.
            // When returning from end-fade, skip the slide animation so it's just a clean fade-in.
            var (tx, ty, sc) = EditorToPresView(kf);
            if (animate && !leavingEnd)
                AnimatePresView(tx, ty, sc, kf.DurationMs);
            else
                SetPresView(tx, ty, sc);
            UpdatePresUI();
            ShowSceneNameOverlay(kf.Name);
            return;
        }

        // Image changed — apply the keyframe's ImageTransition (defaults to Fade)
        var transition = animate ? kf.ImageTransition : ImageTransition.None;
        ApplyImageTransition(transition, kf, targetImgId);
        UpdatePresUI();
        ShowSceneNameOverlay(kf.Name);
    }

    /// <summary>
    /// Animate the presentation image + ink layer to fully transparent, leaving the
    /// PresCanvas' dark background (#07070F) visible. Marks _presEnded = true.
    /// </summary>
    private void FadeToEnd()
    {
        _presEnded = true;
        var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };
        var fadeOut = new DoubleAnimation(0, TimeSpan.FromMilliseconds(500)) { EasingFunction = ease };
        PresImage.BeginAnimation(UIElement.OpacityProperty, fadeOut);
        PresInkCanvas.BeginAnimation(UIElement.OpacityProperty, fadeOut);
        ShowSceneNameOverlay("—  終了  —");
        UpdatePresUI();
    }

    /// <summary>
    /// Animate the presentation content back to fully visible after an end-fade.
    /// </summary>
    private void FadeFromEnd()
    {
        _presEnded = false;
        var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };
        var fadeIn = new DoubleAnimation(1, TimeSpan.FromMilliseconds(400)) { EasingFunction = ease };
        PresImage.BeginAnimation(UIElement.OpacityProperty, fadeIn);
        PresInkCanvas.BeginAnimation(UIElement.OpacityProperty, fadeIn);
    }

    /// <summary>
    /// Switch PresImage to a different image with the requested visual transition.
    /// </summary>
    private void ApplyImageTransition(ImageTransition transition, Keyframe targetKf, string targetImgId)
    {
        const int FadeDurationMs = 300;
        var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };

        void SwapAndShow()
        {
            SetActiveImage(targetImgId, applyFitView: false);
            var (tx, ty, sc) = EditorToPresView(targetKf);
            SetPresView(tx, ty, sc);
        }

        if (transition == ImageTransition.None)
        {
            SwapAndShow();
            PresImage.Opacity = 1;
            PresInkCanvas.Opacity = 1;
            return;
        }

        // Fade and CrossFade both animate opacity. With a single PresImage element we can't do
        // a true crossfade between two bitmaps simultaneously, so both currently render as a
        // fade-through (out → swap → in). CrossFade uses a slightly shorter total duration.
        var halfMs = transition == ImageTransition.CrossFade ? FadeDurationMs / 2 : FadeDurationMs;

        var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(halfMs)) { EasingFunction = ease };
        fadeOut.Completed += (_, _) =>
        {
            SwapAndShow();
            var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(halfMs)) { EasingFunction = ease };
            PresImage.BeginAnimation(UIElement.OpacityProperty, fadeIn);
            PresInkCanvas.BeginAnimation(UIElement.OpacityProperty, fadeIn);
        };
        PresImage.BeginAnimation(UIElement.OpacityProperty, fadeOut);
        PresInkCanvas.BeginAnimation(UIElement.OpacityProperty, fadeOut);
    }

    private void SetPresView(double tx, double ty, double sc)
    {
        _presTx = tx;
        _presTy = ty;
        _presScale = sc;

        PresImageScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        PresImageScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        PresImageTranslate.BeginAnimation(TranslateTransform.XProperty, null);
        PresImageTranslate.BeginAnimation(TranslateTransform.YProperty, null);

        PresImageScale.ScaleX = sc;
        PresImageScale.ScaleY = sc;
        PresImageTranslate.X = tx;
        PresImageTranslate.Y = ty;
    }

    /// <summary>
    /// Convert a keyframe's recorded view to presentation-space coordinates.
    ///
    /// The scale factor is chosen so the content bound (the smaller of image-display-size
    /// and reference-canvas-size in the selected axis) fills the presentation in that axis.
    /// This makes fit-view keyframes fill the screen edge-to-edge, while keeping zoomed-in
    /// keyframes viewport-anchored (same visible pixels as the editor).
    ///
    /// Centering always uses the reference canvas dims (where kf.Tx/Ty live) so the image
    /// pixel at the editor's viewport center lands at the presentation's viewport center.
    /// </summary>
    private (double tx, double ty, double sc) EditorToPresView(Keyframe kf)
    {
        var presW = PresCanvas.ActualWidth;
        var presH = PresCanvas.ActualHeight;

        // Reference canvas dims (where kf.Tx/Ty are expressed)
        var canvasW = kf.RefCanvasW > 0 ? kf.RefCanvasW : _editorCanvasWidth;
        var canvasH = kf.RefCanvasH > 0 ? kf.RefCanvasH : _editorCanvasHeight;

        if (presW <= 0 || presH <= 0 || canvasW <= 0 || canvasH <= 0)
        {
            LogZoomDiag($"[SKIP] presW={presW} presH={presH} canvasW={canvasW} canvasH={canvasH}");
            return (kf.Tx, kf.Ty, kf.Scale);
        }

        // Image display size in the reference canvas. For multi-image (future), look up by kf.ImageId.
        // For now, assume the currently loaded image is the one this keyframe belongs to.
        double imgW = _loadedImage?.PixelWidth * kf.Scale ?? 0;
        double imgH = _loadedImage?.PixelHeight * kf.Scale ?? 0;

        // Content bound: image dim if it fits within the canvas, else canvas dim.
        //   - Fit-view keyframe (image fits): use image dim → image fills pres edge-to-edge
        //   - Zoomed-in keyframe (image exceeds canvas): use canvas dim → same visible pixels as editor
        var refW = imgW > 0 && imgW < canvasW ? imgW : canvasW;
        var refH = imgH > 0 && imgH < canvasH ? imgH : canvasH;

        var scaleFactor = _projectSettings.PresentationScaleMode == PresentationScaleMode.FitHeight
            ? presH / refH
            : presW / refW;
        var sc = kf.Scale * scaleFactor;

        // Re-center: image pixel at editor viewport center → pres viewport center.
        // Algebraic identity: sc/kf.Scale = scaleFactor, so this simplifies to the form below.
        var tx = presW / 2 - (canvasW / 2 - kf.Tx) * scaleFactor;
        var ty = presH / 2 - (canvasH / 2 - kf.Ty) * scaleFactor;

        LogZoomDiag(
            $"mode={_projectSettings.PresentationScaleMode} kf={kf.Name} " +
            $"canvas[W={canvasW:F1} H={canvasH:F1}] " +
            $"imgDisp[W={imgW:F1} H={imgH:F1}] " +
            $"ref[W={refW:F1} H={refH:F1}] " +
            $"kf[Tx={kf.Tx:F1} Ty={kf.Ty:F1} Sc={kf.Scale:F4}] " +
            $"pres[W={presW:F1} H={presH:F1}] " +
            $"factor={scaleFactor:F4} " +
            $"=> Tx={tx:F1} Ty={ty:F1} Sc={sc:F4} " +
            $"imgCenter=({(canvasW/2 - kf.Tx)/kf.Scale:F1},{(canvasH/2 - kf.Ty)/kf.Scale:F1}) " +
            $"presCenterCheck=({(presW/2 - tx)/sc:F1},{(presH/2 - ty)/sc:F1})");
        return (tx, ty, sc);
    }

    private static readonly string ZoomDiagLogPath = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "InfoDive", "zoom_debug.log");

    private static void LogZoomDiag(string line)
    {
        try
        {
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(ZoomDiagLogPath)!);
            System.IO.File.AppendAllText(ZoomDiagLogPath, $"{DateTime.Now:HH:mm:ss.fff} {line}{Environment.NewLine}");
        }
        catch { /* ignore */ }
    }

    private void AnimatePresView(double tx, double ty, double sc, int durationMs)
    {
        _presTx = tx;
        _presTy = ty;
        _presScale = sc;

        var duration = TimeSpan.FromMilliseconds(durationMs);
        var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };

        PresImageScale.BeginAnimation(ScaleTransform.ScaleXProperty,
            new DoubleAnimation(sc, duration) { EasingFunction = ease });
        PresImageScale.BeginAnimation(ScaleTransform.ScaleYProperty,
            new DoubleAnimation(sc, duration) { EasingFunction = ease });
        PresImageTranslate.BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(tx, duration) { EasingFunction = ease });
        PresImageTranslate.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(ty, duration) { EasingFunction = ease });
    }

    private void UpdatePresUI()
    {
        // Prev: always enabled except at step 0 AND not in end-state (end-state → prev goes back to last kf)
        PresPrevBtn.IsEnabled = _presEnded || _presStep > 0;
        // Next: enabled while there are scenes ahead OR on the last scene (to trigger end-fade). Disabled once ended.
        PresNextBtn.IsEnabled = !_presEnded && _keyframes.Count > 0;
        PresStepText.Text = _presEnded
            ? $"終了 / {_keyframes.Count}"
            : $"{_presStep + 1} / {_keyframes.Count}";

        PresDots.Items.Clear();
        for (int i = 0; i < _keyframes.Count; i++)
        {
            var dot = new Ellipse
            {
                Width = 8,
                Height = 8,
                Fill = i == _presStep
                    ? (SolidColorBrush)FindResource("AccentBrush")
                    : new SolidColorBrush(Color.FromRgb(0x25, 0x25, 0x35)),
                Margin = new Thickness(2.5),
                Cursor = Cursors.Hand,
                Tag = i,
                RenderTransform = i == _presStep
                    ? new ScaleTransform(1.4, 1.4, 4, 4)
                    : new ScaleTransform(1, 1, 4, 4),
            };
            dot.MouseLeftButtonDown += (s, _) =>
            {
                if (s is Ellipse el && el.Tag is int idx)
                    PresGoTo(idx);
            };
            dot.ToolTip = _keyframes[i].Name;
            PresDots.Items.Add(dot);
        }
    }

    private void ShowSceneNameOverlay(string name)
    {
        SceneNameText.Text = name;
        var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(300));
        var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(500))
        {
            BeginTime = TimeSpan.FromSeconds(1.5)
        };
        SceneNameOverlay.BeginAnimation(OpacityProperty, null);
        SceneNameOverlay.BeginAnimation(OpacityProperty, fadeIn);
        fadeIn.Completed += (_, _) =>
            SceneNameOverlay.BeginAnimation(OpacityProperty, fadeOut);
    }

    // ── Blackout ──
    private void ToggleBlackout()
    {
        _isBlackedOut = !_isBlackedOut;
        BlackoutOverlay.Visibility = _isBlackedOut ? Visibility.Visible : Visibility.Collapsed;
    }

    // ── Key Guide ──
    private void ToggleKeyGuide()
    {
        _isKeyGuideVisible = !_isKeyGuideVisible;
        KeyGuideOverlay.Visibility = _isKeyGuideVisible ? Visibility.Visible : Visibility.Collapsed;
    }

    // ── Drawing ──
    private void ToggleDrawMode()
    {
        _isDrawMode = !_isDrawMode;
        _isEraserMode = false;

        if (_isDrawMode)
        {
            PresInkCanvas.IsHitTestVisible = true;
            PresInkCanvas.EditingMode = InkCanvasEditingMode.Ink;
            PresInkCanvas.Cursor = Cursors.Cross;
            DrawToolbar.Visibility = Visibility.Visible;
            UpdateEraserButtonVisual();
        }
        else
        {
            PresInkCanvas.EditingMode = InkCanvasEditingMode.None;
            PresInkCanvas.IsHitTestVisible = false;
            DrawToolbar.Visibility = Visibility.Collapsed;
        }
    }

    private void SetEraserMode(bool eraser)
    {
        _isEraserMode = eraser;
        if (_isDrawMode)
        {
            PresInkCanvas.EditingMode = eraser ? InkCanvasEditingMode.EraseByStroke : InkCanvasEditingMode.Ink;
            PresInkCanvas.Cursor = eraser ? Cursors.No : Cursors.Cross;
        }
        UpdateEraserButtonVisual();
    }

    private void UpdateEraserButtonVisual()
    {
        EraserBtn.Foreground = _isEraserMode
            ? new SolidColorBrush(Colors.White)
            : new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA));
    }

    private void SetInkColor(Color color)
    {
        var da = PresInkCanvas.DefaultDrawingAttributes.Clone();
        da.Color = color;
        PresInkCanvas.DefaultDrawingAttributes = da;
        // Also switch out of eraser when picking a color
        if (_isEraserMode)
            SetEraserMode(false);

        // Highlight the selected color border
        ColorRed.BorderBrush = color == Colors.Red ? Brushes.White : Brushes.Transparent;
        ColorYellow.BorderBrush = color == Color.FromRgb(0xFF, 0xFF, 0x00) ? Brushes.White : Brushes.Transparent;
        ColorGreen.BorderBrush = color == Color.FromRgb(0x00, 0xCC, 0x66) ? Brushes.White : Brushes.Transparent;
        ColorBlue.BorderBrush = color == Color.FromRgb(0x33, 0x88, 0xFF) ? Brushes.White : Brushes.Transparent;
        ColorWhite.BorderBrush = color == Colors.White ? Brushes.Gray : Brushes.Transparent;
    }

    private void SaveInkDrawing()
    {
        if (_isPresenting)
        {
            SaveInkDrawingPresentation();
        }
        else
        {
            SaveInkDrawingEditor();
        }
    }

    // Presentation save: one PNG of the currently visible screen (what the audience sees).
    private void SaveInkDrawingPresentation()
    {
        if (CurrentStrokes.Count == 0)
        {
            MessageBox.Show("描画がありません。", "保存", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dlg = new SaveFileDialog
        {
            Filter = "PNG 画像 (*.png)|*.png",
            Title = "描画を画像として保存",
            DefaultExt = ".png",
            FileName = $"annotation_{DateTime.Now:yyyyMMdd_HHmmss}.png",
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            var rtb = RenderPresentationView();
            if (rtb == null) return;
            WritePng(rtb, dlg.FileName);
            ShowToast($"描画を保存: {System.IO.Path.GetFileName(dlg.FileName)}");
        }
        catch (Exception ex)
        {
            MessageBox.Show($"保存に失敗しました:\n{ex.Message}", "エラー",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // Editor save: render every image that has drawings at native resolution. One image
    // with drawings → single-file dialog; multiple → folder dialog + one PNG per image.
    private void SaveInkDrawingEditor()
    {
        var targets = _images
            .Where(i => _strokesByImage.TryGetValue(i.Id, out var sc) && sc.Count > 0)
            .ToList();
        if (targets.Count == 0)
        {
            MessageBox.Show("描画がありません。", "保存", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            if (targets.Count == 1)
            {
                var img = targets[0];
                var dlg = new SaveFileDialog
                {
                    Filter = "PNG 画像 (*.png)|*.png",
                    Title = "描画を画像として保存",
                    DefaultExt = ".png",
                    FileName = BuildAnnotationFileName(img),
                };
                if (dlg.ShowDialog() != true) return;

                var rtb = RenderImageWithStrokes(img);
                WritePng(rtb, dlg.FileName);
                ShowToast($"描画を保存: {System.IO.Path.GetFileName(dlg.FileName)}");
            }
            else
            {
                var folder = new OpenFolderDialog { Title = "描画入りの全画像の書き出し先フォルダを選択" };
                if (folder.ShowDialog() != true) return;

                int n = 0;
                foreach (var img in targets)
                {
                    var rtb = RenderImageWithStrokes(img);
                    var path = System.IO.Path.Combine(folder.FolderName, BuildAnnotationFileName(img));
                    WritePng(rtb, path);
                    n++;
                }
                ShowToast($"描画を {n} 画像 書き出しました");
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"保存に失敗しました:\n{ex.Message}", "エラー",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static string BuildAnnotationFileName(LoadedImage img)
    {
        var baseName = System.IO.Path.GetFileNameWithoutExtension(img.FileName);
        foreach (var c in System.IO.Path.GetInvalidFileNameChars())
            baseName = baseName.Replace(c, '_');
        return $"annotation_{baseName}.png";
    }

    private static void WritePng(BitmapSource bmp, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bmp));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    // Render one image + its strokes at native resolution. Strokes are in image-pixel
    // coordinates so they drop straight onto the bitmap — pan/zoom independent.
    private RenderTargetBitmap RenderImageWithStrokes(LoadedImage img)
    {
        var w = img.Bitmap.PixelWidth;
        var h = img.Bitmap.PixelHeight;
        var strokes = GetStrokesForImage(img.Id);

        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawImage(img.Bitmap, new Rect(0, 0, w, h));
            foreach (var s in strokes)
                s.Draw(dc);
        }

        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        return rtb;
    }

    // Presentation save: capture the visible screen (what the audience sees), but hide
    // floating UI chrome so only the image + ink + letterbox land in the PNG.
    private RenderTargetBitmap? RenderPresentationView()
    {
        UIElement[] overlays = { PresToolbar, PresToolbarShowBtn, DrawToolbar, KeyGuideOverlay, SceneNameOverlay };
        var prevVis = overlays.Select(o => o.Visibility).ToArray();

        try
        {
            var width = (int)PresentationGrid.ActualWidth;
            var height = (int)PresentationGrid.ActualHeight;
            if (width <= 0 || height <= 0) return null;

            for (int i = 0; i < overlays.Length; i++) overlays[i].Visibility = Visibility.Hidden;
            PresentationGrid.UpdateLayout();

            var rtb = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(PresentationGrid);
            return rtb;
        }
        finally
        {
            for (int i = 0; i < overlays.Length; i++) overlays[i].Visibility = prevVis[i];
        }
    }

    private void UpdateAnnotationIndicator()
    {
        bool hasCurrent = CurrentStrokes.Count > 0;
        // Menu save entry enables if any image has strokes; editor save now covers all of them.
        bool hasAny = _strokesByImage.Values.Any(sc => sc.Count > 0);

        if (!_isPresenting)
        {
            AnnotationIndicator.Visibility = hasCurrent ? Visibility.Visible : Visibility.Collapsed;
            MenuSaveAnnotation.IsEnabled = hasAny;
        }
    }

    private void SaveAnnotation_Click(object sender, RoutedEventArgs e) => SaveInkDrawing();

    private void ClearAnnotation_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentStrokes.Count == 0) return;
        var result = MessageBox.Show("この画像の描画をすべて消去しますか？", "確認",
            MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (result == MessageBoxResult.Yes)
        {
            CurrentStrokes.Clear();
            UpdateAnnotationIndicator();
        }
    }

    // Drawing toolbar event handlers
    private void InkColorRed_Click(object sender, MouseButtonEventArgs e) => SetInkColor(Colors.Red);
    private void InkColorYellow_Click(object sender, MouseButtonEventArgs e) => SetInkColor(Color.FromRgb(0xFF, 0xFF, 0x00));
    private void InkColorGreen_Click(object sender, MouseButtonEventArgs e) => SetInkColor(Color.FromRgb(0x00, 0xCC, 0x66));
    private void InkColorBlue_Click(object sender, MouseButtonEventArgs e) => SetInkColor(Color.FromRgb(0x33, 0x88, 0xFF));
    private void InkColorWhite_Click(object sender, MouseButtonEventArgs e) => SetInkColor(Colors.White);

    private void InkThickness_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (PresInkCanvas == null) return;
        var da = PresInkCanvas.DefaultDrawingAttributes.Clone();
        da.Width = e.NewValue;
        da.Height = e.NewValue;
        PresInkCanvas.DefaultDrawingAttributes = da;
    }

    private void InkEraser_Click(object sender, RoutedEventArgs e) => SetEraserMode(!_isEraserMode);
    private void InkClear_Click(object sender, RoutedEventArgs e)
    {
        CurrentStrokes.Clear();
        UpdateAnnotationIndicator();
    }
    private void InkSave_Click(object sender, RoutedEventArgs e) => SaveInkDrawing();

    // ── Toolbar visibility ──
    private void TogglePresToolbar()
    {
        _isPresToolbarVisible = !_isPresToolbarVisible;
        PresToolbar.Visibility = _isPresToolbarVisible ? Visibility.Visible : Visibility.Collapsed;
        PresToolbarShowBtn.Visibility = _isPresToolbarVisible ? Visibility.Collapsed : Visibility.Visible;
    }

    // Presentation toolbar button handlers
    private void PresDrawToggle_Click(object sender, RoutedEventArgs e) => ToggleDrawMode();
    private void PresBlackout_Click(object sender, RoutedEventArgs e) => ToggleBlackout();
    private void PresHelp_Click(object sender, RoutedEventArgs e) => ToggleKeyGuide();
    private void PresToggleToolbar_Click(object sender, RoutedEventArgs e) => TogglePresToolbar();

    private void PresShowToolbar_Click(object sender, MouseButtonEventArgs e)
    {
        _isPresToolbarVisible = true;
        PresToolbar.Visibility = Visibility.Visible;
        PresToolbarShowBtn.Visibility = Visibility.Collapsed;
    }

    private void PresShowBtn_MouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is FrameworkElement el) el.Opacity = 0.9;
    }

    private void PresShowBtn_MouseLeave(object sender, MouseEventArgs e)
    {
        if (sender is FrameworkElement el) el.Opacity = 0.5;
    }

    // Presentation panning
    private void PresCanvas_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            _isPresPanning = true;
            _presPanStart = e.GetPosition(PresCanvas);
            _presPanStartTx = _presTx;
            _presPanStartTy = _presTy;
            PresCanvas.Cursor = Cursors.ScrollAll;
            PresCanvas.CaptureMouse();
        }
    }

    private void PresCanvas_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isPresPanning) return;
        var pos = e.GetPosition(PresCanvas);
        var dx = pos.X - _presPanStart.X;
        var dy = pos.Y - _presPanStart.Y;
        SetPresView(_presPanStartTx + dx, _presPanStartTy + dy, _presScale);
    }

    private void PresCanvas_MouseUp(object sender, MouseButtonEventArgs e)
    {
        _isPresPanning = false;
        PresCanvas.Cursor = Cursors.Arrow;
        PresCanvas.ReleaseMouseCapture();
    }

    // ════════════════════════════════════════
    //  File Operations
    // ════════════════════════════════════════

    private void NewProject()
    {
        if (!ConfirmDiscard()) return;

        _undo.Clear();
        _keyframes.Clear();
        _images.Clear();
        _strokesByImage.Clear();
        _currentImageId = "";
        _loadedImage = null;
        _loadedImageBytes = null;
        _loadedImageFileName = null;
        _currentFilePath = null;
        _isDirty = false;
        _selectedKeyframeId = null;
        _projectSettings = new ProjectSettings();
        ApplyScaleModeUI();

        CanvasImage.Source = null;
        PresImage.Source = null;
        DropZone.Visibility = Visibility.Visible;
        ImageCanvas.Visibility = Visibility.Collapsed;
        ZoomIndicator.Visibility = Visibility.Collapsed;
        CanvasHint.Visibility = Visibility.Collapsed;
        CanvasTools.Visibility = Visibility.Collapsed;
        AddKeyframeBtn.IsEnabled = false;
        ReplaceImageBtn.IsEnabled = false;
        ImageSelector.IsEnabled = false;

        UpdateTitle();
    }

    /// <summary>
    /// Replace the currently-selected image with a new file. Keyframes that reference this
    /// image keep their ImageId (so they still point at the replacement), but their Tx/Ty
    /// may need adjustment if the new image has very different dimensions.
    /// </summary>
    private void ReplaceCurrentImage()
    {
        if (_images.Count == 0 || string.IsNullOrEmpty(_currentImageId))
        {
            MessageBox.Show("差し替える対象の画像がありません。先に「画像を追加」で画像を読み込んでください。",
                "画像差し替え", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dlg = new OpenFileDialog
        {
            Filter = "画像ファイル|*.jpg;*.jpeg;*.png;*.bmp;*.gif;*.webp;*.svg|すべてのファイル|*.*",
            Title = "画像を差し替え",
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            var bytes = File.ReadAllBytes(dlg.FileName);
            var fileName = System.IO.Path.GetFileName(dlg.FileName);
            ReplaceCurrentImageWithBytes(bytes, fileName);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"画像の読み込みに失敗しました:\n{ex.Message}", "エラー",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ReplaceCurrentImageWithBytes(byte[] bytes, string fileName)
    {
        var idx = -1;
        for (var i = 0; i < _images.Count; i++)
            if (_images[i].Id == _currentImageId) { idx = i; break; }
        if (idx < 0) return;

        if (!TryNormalizeSvg(ref bytes, ref fileName)) return;
        var existingId = _images[idx].Id;
        var uniqueName = MakeUniqueImageName(fileName, excludeId: existingId);
        var created = TryCreateLoadedImage(bytes, uniqueName);
        if (created == null) return;

        // Preserve the same Id so existing keyframes stay linked
        var replaced = new LoadedImage
        {
            Id = existingId,
            FileName = created.FileName,
            Bytes = created.Bytes,
            Bitmap = created.Bitmap,
        };
        _images[idx] = replaced;
        SetActiveImage(replaced.Id, applyFitView: true);
        MarkDirty();
        ShowToast($"画像を差し替え: {replaced.FileName}");
    }

    private void OpenProject()
    {
        if (!ConfirmDiscard()) return;

        var dlg = new OpenFileDialog
        {
            Filter = "InfoDive プロジェクト (*.przip)|*.przip|すべてのファイル|*.*",
            Title = "プロジェクトを開く",
        };
        if (dlg.ShowDialog() != true) return;

        OpenProjectFile(dlg.FileName);
    }

    private void OpenProjectFile(string path)
    {
        try
        {
            var (scenario, imageRefs) = ProjectFileService.LoadMulti(path);

            _undo.Clear();
            _keyframes.Clear();
            _images.Clear();
            _strokesByImage.Clear();
            _currentImageId = "";

            // Load all images into memory
            foreach (var r in imageRefs)
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.StreamSource = new MemoryStream(r.Bytes);
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.EndInit();
                bmp.Freeze();
                _images.Add(new LoadedImage
                {
                    Id = string.IsNullOrEmpty(r.Id) ? "img_" + Guid.NewGuid().ToString("N")[..8] : r.Id,
                    FileName = r.FileName,
                    Bytes = r.Bytes,
                    Bitmap = bmp,
                });
            }

            // Show the first image
            if (_images.Count > 0)
                SetActiveImage(_images[0].Id, applyFitView: true);

            foreach (var kf in scenario.Keyframes)
                _keyframes.Add(kf);

            // Apply project-level settings loaded from .przip
            _projectSettings = scenario.Settings ?? new ProjectSettings();
            ApplyScaleModeUI();

            _currentFilePath = path;
            _isDirty = false;
            UpdateTitle();
            AddRecentFile(path);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"プロジェクトの読み込みに失敗しました:\n{ex.Message}", "エラー",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void SaveProject()
    {
        if (_currentFilePath != null)
            SaveProjectTo(_currentFilePath);
        else
            SaveProjectAs();
    }

    private void SaveProjectAs()
    {
        if (_loadedImageBytes == null || _loadedImageFileName == null)
        {
            MessageBox.Show("画像が読み込まれていません。", "保存エラー",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var dlg = new SaveFileDialog
        {
            Filter = "InfoDive プロジェクト (*.przip)|*.przip",
            Title = "名前を付けて保存",
            DefaultExt = ".przip",
        };
        if (dlg.ShowDialog() == true)
            SaveProjectTo(dlg.FileName);
    }

    private void SaveProjectTo(string path)
    {
        if (_images.Count == 0) return;

        try
        {
            var first = _images[0];
            var scenario = new ScenarioData
            {
                Title = System.IO.Path.GetFileNameWithoutExtension(path),
                Canvas = new CanvasInfo
                {
                    ImageWidth = first.Bitmap.PixelWidth,
                    ImageHeight = first.Bitmap.PixelHeight,
                },
                Settings = _projectSettings,
                Keyframes = _keyframes.ToList(),
            };

            // Ensure filenames are unique within the zip (distinct .przip entry names)
            var imageRefs = BuildUniqueImageRefs(_images);
            ProjectFileService.SaveMulti(path, scenario, imageRefs);
            _currentFilePath = path;
            _isDirty = false;
            UpdateTitle();
            AddRecentFile(path);
            ShowToast($"保存しました: {System.IO.Path.GetFileName(path)}");
        }
        catch (Exception ex)
        {
            MessageBox.Show($"保存に失敗しました:\n{ex.Message}", "エラー",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ExportPng()
    {
        if (_loadedImage == null || _keyframes.Count == 0)
        {
            MessageBox.Show("画像とシーンが必要です。", "エクスポート",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dlg = new OpenFolderDialog
        {
            Title = "PNG 連番の書き出し先フォルダを選択",
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            var canvasWidth = (int)CanvasArea.ActualWidth;
            var canvasHeight = (int)CanvasArea.ActualHeight;
            if (canvasWidth <= 0 || canvasHeight <= 0)
            {
                canvasWidth = 1920;
                canvasHeight = 1080;
            }

            for (int i = 0; i < _keyframes.Count; i++)
            {
                var kf = _keyframes[i];
                var rtb = new RenderTargetBitmap(canvasWidth, canvasHeight, 96, 96, PixelFormats.Pbgra32);

                var dv = new DrawingVisual();
                using (var dc = dv.RenderOpen())
                {
                    dc.PushTransform(new TranslateTransform(kf.Tx, kf.Ty));
                    dc.PushTransform(new ScaleTransform(kf.Scale, kf.Scale));
                    dc.DrawImage(_loadedImage, new Rect(0, 0, _loadedImage.PixelWidth, _loadedImage.PixelHeight));
                    dc.Pop();
                    dc.Pop();
                }

                rtb.Render(dv);

                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(rtb));

                // Sanitize file name
                var safeName = kf.Name;
                foreach (var c in System.IO.Path.GetInvalidFileNameChars())
                    safeName = safeName.Replace(c, '_');
                var filePath = System.IO.Path.Combine(dlg.FolderName, $"scene_{i + 1:D3}_{safeName}.png");

                using var stream = File.Create(filePath);
                encoder.Save(stream);
            }

            MessageBox.Show($"{_keyframes.Count} 枚の PNG を書き出しました。", "エクスポート完了",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"エクスポートに失敗しました:\n{ex.Message}", "エラー",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ════════════════════════════════════════
    //  Recent Files
    // ════════════════════════════════════════

    private void AddRecentFile(string path)
    {
        _recentFiles.Remove(path);
        _recentFiles.Insert(0, path);
        if (_recentFiles.Count > MaxRecentFiles)
            _recentFiles.RemoveAt(_recentFiles.Count - 1);
        UpdateRecentFilesMenu();
    }

    private void UpdateRecentFilesMenu()
    {
        RecentFilesMenu.Items.Clear();
        if (_recentFiles.Count == 0)
        {
            RecentFilesMenu.Items.Add(new MenuItem { Header = "(なし)", IsEnabled = false });
            return;
        }

        foreach (var path in _recentFiles)
        {
            var item = new MenuItem { Header = path, Tag = path };
            item.Click += (s, _) =>
            {
                if (s is MenuItem mi && mi.Tag is string p)
                {
                    if (!ConfirmDiscard()) return;
                    OpenProjectFile(p);
                }
            };
            RecentFilesMenu.Items.Add(item);
        }
    }

    // ════════════════════════════════════════
    //  Helpers
    // ════════════════════════════════════════

    private void MarkDirty()
    {
        _isDirty = true;
        UpdateTitle();
    }

    private void UpdateTitle()
    {
        var name = _currentFilePath != null
            ? System.IO.Path.GetFileName(_currentFilePath)
            : "新規プロジェクト";
        Title = $"{(_isDirty ? "* " : "")}{name} - InfoDive";
        StatusFileName.Text = name;
    }

    /// <summary>
    /// Called before operations that would lose the current project (new, open, drop-open).
    /// Returns true if the caller may proceed. Offers Yes=save / No=discard / Cancel=abort.
    /// If Yes is chosen but the save dialog is cancelled, the operation is aborted.
    /// </summary>
    private bool ConfirmDiscard()
    {
        if (!_isDirty) return true;
        var result = MessageBox.Show(
            "未保存の変更があります。保存しますか？",
            "確認", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        return result switch
        {
            MessageBoxResult.Yes => SaveAndContinue(),
            MessageBoxResult.No => true,         // discard changes, proceed
            _ => false,                          // Cancel or closed dialog — abort
        };

        bool SaveAndContinue()
        {
            SaveProject();
            // If the Save-As dialog was cancelled, _isDirty will still be true → abort
            return !_isDirty;
        }
    }

    // ════════════════════════════════════════
    //  Event Handlers (Menu / Toolbar / Keyboard)
    // ════════════════════════════════════════

    private void NewProject_Click(object sender, RoutedEventArgs e) => NewProject();
    private void OpenFile_Click(object sender, RoutedEventArgs e) => OpenProject();
    private void ReplaceImage_Click(object sender, RoutedEventArgs e) => ReplaceCurrentImage();
    private void Save_Click(object sender, RoutedEventArgs e) => SaveProject();
    private void SaveAs_Click(object sender, RoutedEventArgs e) => SaveProjectAs();

    // Keyboard shortcut handlers (wired via ApplicationCommands InputBindings)
    private void CommandNew_Executed(object sender, ExecutedRoutedEventArgs e) => NewProject();
    private void CommandOpen_Executed(object sender, ExecutedRoutedEventArgs e) => OpenProject();
    private void CommandSave_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        // During presentation, Ctrl+S means "save the current drawing as PNG" —
        // saving the project file from a full-screen view would be disruptive.
        if (_isPresenting) SaveInkDrawing();
        else SaveProject();
    }
    private void CommandSaveAs_Executed(object sender, ExecutedRoutedEventArgs e) => SaveProjectAs();

    private void CommandHelp_Executed(object sender, ExecutedRoutedEventArgs e)
        => ShowHelp_Click(sender, e);

    private void CommandUndo_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        var action = _undo.Undo();
        if (action == null)
        {
            ShowToast("元に戻せる操作はありません", icon: "\u26A0"); // ⚠
            return;
        }
        RefreshKeyframeListUI();
        UpdateStatusImageInfo();
        MarkDirty();
        ShowToast($"元に戻しました: {action.Description}");
    }

    private void CommandRedo_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        var action = _undo.Redo();
        if (action == null)
        {
            ShowToast("やり直せる操作はありません", icon: "\u26A0");
            return;
        }
        RefreshKeyframeListUI();
        UpdateStatusImageInfo();
        MarkDirty();
        ShowToast($"やり直しました: {action.Description}");
    }
    private void ExportPng_Click(object sender, RoutedEventArgs e) => ExportPng();
    private void Exit_Click(object sender, RoutedEventArgs e) => Close();

    private void ClearAllScenes_Click(object sender, RoutedEventArgs e)
    {
        if (_keyframes.Count == 0) return;
        var result = MessageBox.Show(
            $"{_keyframes.Count} 件のシーンをすべて削除しますか？（Ctrl+Z で元に戻せます）",
            "確認", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result == MessageBoxResult.Yes)
        {
            _undo.Push(new ClearAllKeyframesAction(_keyframes, _keyframes.ToList()));
            _keyframes.Clear();
            _selectedKeyframeId = null;
            MarkDirty();
        }
    }

    private void ToggleSidebar_Click(object sender, RoutedEventArgs e)
    {
        if (ToggleSidebarMenu.IsChecked)
        {
            SidebarColumn.Width = new GridLength(280);
            SidebarBorder.Visibility = Visibility.Visible;
        }
        else
        {
            SidebarColumn.Width = new GridLength(0);
            SidebarBorder.Visibility = Visibility.Collapsed;
        }
    }

    // Image display mode menu
    private void ImageModeToFit_Click(object sender, RoutedEventArgs e)
    {
        _fitToScreen = true;
        MenuFitToScreen.IsChecked = true;
        MenuOriginalSize.IsChecked = false;
        FitView();
    }

    private void ImageModeToOriginal_Click(object sender, RoutedEventArgs e)
    {
        _fitToScreen = false;
        MenuFitToScreen.IsChecked = false;
        MenuOriginalSize.IsChecked = true;
        OriginalSizeView();
    }

    // Shift = coarse (10%), plain = fine (2%). Applies to menu items and canvas +/- buttons.
    private static double ZoomInFactor() => Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 1.10 : 1.02;
    private static double ZoomOutFactor() => 1.0 / ZoomInFactor();
    private void ZoomIn_Click(object sender, RoutedEventArgs e) => ZoomCenter(ZoomInFactor());
    private void ZoomOut_Click(object sender, RoutedEventArgs e) => ZoomCenter(ZoomOutFactor());
    private void FitView_Click(object sender, RoutedEventArgs e) => FitView();

    private void StartPresentation_Click(object sender, RoutedEventArgs e) => StartPresentation();
    private void StartPresentationFromBeginning_Click(object sender, RoutedEventArgs e) => StartPresentation(0);

    private bool _syncingSettingsUI;

    private void ScaleModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingSettingsUI) return;
        var tag = (ScaleModeCombo.SelectedItem as ComboBoxItem)?.Tag as string;
        if (!Enum.TryParse<PresentationScaleMode>(tag, out var mode)) return;
        if (_projectSettings.PresentationScaleMode == mode) return;
        _projectSettings.PresentationScaleMode = mode;
        MarkDirty();
    }

    private void LaunchModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingSettingsUI) return;
        var tag = (LaunchModeCombo.SelectedItem as ComboBoxItem)?.Tag as string;
        if (!Enum.TryParse<LaunchMode>(tag, out var mode)) return;
        if (_projectSettings.DefaultLaunchMode == mode) return;
        _projectSettings.DefaultLaunchMode = mode;
        MarkDirty();
    }

    private void ApplyScaleModeUI()
    {
        _syncingSettingsUI = true;
        try
        {
            ScaleModeCombo.SelectedIndex = _projectSettings.PresentationScaleMode == PresentationScaleMode.FitHeight ? 1 : 0;
            LaunchModeCombo.SelectedIndex = _projectSettings.DefaultLaunchMode == LaunchMode.Presentation ? 1 : 0;
        }
        finally { _syncingSettingsUI = false; }
    }
    private void EndPresentation_Click(object sender, RoutedEventArgs e) => EndPresentation();
    // From the "ended" state, Previous navigates back to the last keyframe (not step-1 from _presStep).
    private void PresPrev_Click(object sender, RoutedEventArgs e)
        => PresGoTo(_presEnded ? _keyframes.Count - 1 : _presStep - 1);
    private void PresNext_Click(object sender, RoutedEventArgs e) => PresGoTo(_presStep + 1);

    private void ShowHelp_Click(object sender, RoutedEventArgs e)
    {
        MessageBox.Show(
            "InfoDive - インフォグラフィック プレゼンテーションツール\n\n" +
            "使い方:\n" +
            "1. 画像を開く（ドラッグ＆ドロップ or「画像を追加」）\n" +
            "   対応形式: JPG / PNG / BMP / GIF / WebP / SVG / PDF (1ページ=1画像)\n" +
            "2. ドラッグ・スクロールでビューを調整\n" +
            "3.「現在のビューをシーンに追加」でシーンを作成\n" +
            "4.「プレゼン開始」で再生\n\n" +
            "ファイル操作:\n" +
            "  Ctrl+N: 新規  Ctrl+O: 開く  Ctrl+S: 保存\n" +
            "  Ctrl+Shift+S: 名前を付けて保存\n" +
            "  Ctrl+Z: 元に戻す  Ctrl+Y: やり直し\n" +
            "  F1 / ?: この使い方ダイアログを表示\n\n" +
            "ビュー操作:\n" +
            "  F: フィット  +/- / ホイール: ズーム 2%  Shift併用: ズーム 10%  F11: 全画面\n\n" +
            "シーン操作 (エディタ):\n" +
            "  ↑/↓: シーン選択移動  F2: 名前変更\n" +
            "  Delete: 削除  Ctrl+D: 複製\n" +
            "  ドラッグ&ドロップで並び替え\n" +
            "  Ctrl+Tab / Ctrl+Shift+Tab: 画像を切替\n\n" +
            "プレゼン:\n" +
            "  Ctrl+Enter: 開始  F5: 最初から再生  Esc: 終了\n" +
            "  →/Space/PageDown: 次のシーン\n" +
            "  ←/PageUp: 前のシーン\n" +
            "  Shift+→ / Shift+Space: 次の画像\n" +
            "  Shift+←: 前の画像\n" +
            "  Home/End: 最初/最後のシーン\n" +
            "  1〜9: シーン番号でジャンプ  Shift+1〜9: 画像番号でジャンプ\n" +
            "  B: 暗転（任意キーで解除）  H/?: ガイド  T: ツールバー表示切替\n" +
            "  D: 描画（画像ごと）  C: クリア  E: 消しゴム  Ctrl+S: 描画をPNG保存\n\n" +
            "コマンドライン:\n" +
            "  InfoDive.exe -e file.przip   エディタで開く\n" +
            "  InfoDive.exe -p file.przip   プレゼンで開く\n" +
            "  InfoDive.exe img1.png doc.pdf  画像/PDFを読込\n" +
            "  Shift+ダブルクリック起動でプレゼン設定を無視してエディタで開く",
            "使い方", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void ShowAbout_Click(object sender, RoutedEventArgs e)
    {
        MessageBox.Show(
            "InfoDive v1.2\n\n" +
            "インフォグラフィック\nプレゼンテーションツール\n\n" +
            "複数のインフォグラフィック画像を\nパン/ズームのシナリオで\nプレゼンテーションとして再生します。",
            "バージョン情報", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    // Drag & drop on canvas
    private void Canvas_DragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
            e.Effects = DragDropEffects.Copy;
        else
            e.Effects = DragDropEffects.None;
        e.Handled = true;
    }

    private void Canvas_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            var files = (string[])e.Data.GetData(DataFormats.FileDrop);
            if (files?.Length > 0)
            {
                var ext = System.IO.Path.GetExtension(files[0]).ToLowerInvariant();
                if (ext == ".przip")
                    OpenProjectFile(files[0]);
                else if (IsImportableFile(files[0]))
                    AddImagesFromFile(files[0]);
            }
        }
    }

    private void DropZone_Click(object sender, MouseButtonEventArgs e)
    {
        // Drop zone appears when no image is loaded → always "add" (which becomes the first image)
        var dlg = new OpenFileDialog
        {
            Filter = "画像 / PDF / draw.io|*.jpg;*.jpeg;*.png;*.bmp;*.gif;*.webp;*.svg;*.pdf;*.drawio;*.dio|画像ファイル|*.jpg;*.jpeg;*.png;*.bmp;*.gif;*.webp;*.svg|PDF (*.pdf)|*.pdf|draw.io (*.drawio;*.dio)|*.drawio;*.dio|すべてのファイル|*.*",
            Title = "画像を追加",
        };
        if (dlg.ShowDialog() == true)
            AddImagesFromFile(dlg.FileName);
    }

    // During presentation, route keys through our handler before any child can swallow
    // them. InkCanvas in Ink mode, for instance, consumes Escape (stroke-cancel), which
    // would otherwise prevent Esc from toggling draw mode or ending the presentation.
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_isPresenting) Window_KeyDown(sender, e);
    }

    // Keyboard handling
    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        // Presentation mode keys
        if (_isPresenting)
        {
            // If blackout is showing, any key except B and Escape exits blackout
            if (_isBlackedOut && e.Key != Key.B && e.Key != Key.Escape)
            {
                ToggleBlackout();
                e.Handled = true;
                return;
            }

            switch (e.Key)
            {
                case Key.Right:
                case Key.PageDown:
                    if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
                        PresSwitchImage(+1);
                    else
                        PresGoTo(_presStep + 1);
                    e.Handled = true;
                    return;
                case Key.Space:
                    if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
                        PresSwitchImage(+1);
                    else
                        PresGoTo(_presStep + 1);
                    e.Handled = true;
                    return;
                case Key.Left:
                case Key.PageUp:
                    if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
                        PresSwitchImage(-1);
                    else
                        PresGoTo(_presEnded ? _keyframes.Count - 1 : _presStep - 1);
                    e.Handled = true;
                    return;
                case Key.Home:
                    PresGoTo(0);
                    e.Handled = true;
                    return;
                case Key.End:
                    PresGoTo(_keyframes.Count - 1);
                    e.Handled = true;
                    return;
                case Key.Escape:
                    if (_isDrawMode)
                    {
                        ToggleDrawMode();
                        e.Handled = true;
                        return;
                    }
                    if (_isKeyGuideVisible)
                    {
                        ToggleKeyGuide();
                        e.Handled = true;
                        return;
                    }
                    EndPresentation();
                    e.Handled = true;
                    return;
                case Key.F5:
                    PresGoTo(0);
                    e.Handled = true;
                    return;
                case Key.B:
                    ToggleBlackout();
                    e.Handled = true;
                    return;
                case Key.H:
                case Key.OemQuestion: // ? key
                    ToggleKeyGuide();
                    e.Handled = true;
                    return;
                case Key.D:
                    ToggleDrawMode();
                    e.Handled = true;
                    return;
                case Key.T:
                    TogglePresToolbar();
                    e.Handled = true;
                    return;
                case Key.C:
                    if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
                    {
                        CurrentStrokes.Clear();
                        UpdateAnnotationIndicator();
                        e.Handled = true;
                    }
                    return;
                case Key.E:
                    if (_isDrawMode)
                    {
                        SetEraserMode(!_isEraserMode);
                        e.Handled = true;
                    }
                    return;
                case Key.S:
                    if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
                    {
                        SaveInkDrawing();
                        e.Handled = true;
                    }
                    return;
            }

            // Number keys 1-9: scene jump, or Shift+1-9 for image jump (in scenario order
            // — the N-th unique image encountered when walking keyframes from the start).
            if (e.Key >= Key.D1 && e.Key <= Key.D9)
            {
                var idx = e.Key - Key.D1;
                if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
                {
                    var seen = new HashSet<string>();
                    var firstKfByImage = new List<(string ImageId, int KfIndex)>();
                    for (var i = 0; i < _keyframes.Count; i++)
                    {
                        var imgId = ResolveKeyframeImageId(_keyframes[i]);
                        if (string.IsNullOrEmpty(imgId) || !seen.Add(imgId)) continue;
                        firstKfByImage.Add((imgId, i));
                    }
                    if (idx < firstKfByImage.Count)
                        PresGoTo(firstKfByImage[idx].KfIndex);
                }
                else
                {
                    if (idx < _keyframes.Count)
                        PresGoTo(idx);
                }
                e.Handled = true;
                return;
            }
            return;
        }

        // Editor mode keys

        // Skip shortcuts that affect keyframe list when the user is typing in a TextBox/ComboBox
        // (e.g. dialogs or the image selector). Otherwise Delete/F2/arrows would hijack text editing.
        if (Keyboard.FocusedElement is TextBox || Keyboard.FocusedElement is ComboBox)
            goto viewKeys;

        // Scene list navigation / actions
        if (e.Key == Key.Down && !Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            SelectAdjacentKeyframe(+1);
            e.Handled = true;
            return;
        }
        else if (e.Key == Key.Up && !Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            SelectAdjacentKeyframe(-1);
            e.Handled = true;
            return;
        }
        else if (e.Key == Key.F2)
        {
            RenameSelectedKeyframe();
            e.Handled = true;
            return;
        }
        else if (e.Key == Key.Delete)
        {
            DeleteSelectedKeyframe();
            e.Handled = true;
            return;
        }
        else if (e.Key == Key.D && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            DuplicateSelectedKeyframe();
            e.Handled = true;
            return;
        }
        else if (e.Key == Key.Tab && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            // Ctrl+Tab / Ctrl+Shift+Tab: cycle through images in the project
            var reverse = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
            SwitchAdjacentImage(reverse ? -1 : +1);
            e.Handled = true;
            return;
        }

    viewKeys:
        if (e.Key == Key.F && !Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            FitView();
            e.Handled = true;
        }
        else if (e.Key == Key.OemPlus || e.Key == Key.Add)
        {
            ZoomCenter(ZoomInFactor());
            e.Handled = true;
        }
        else if (e.Key == Key.OemMinus || e.Key == Key.Subtract)
        {
            ZoomCenter(ZoomOutFactor());
            e.Handled = true;
        }
        else if (e.Key == Key.F11)
        {
            if (WindowStyle == WindowStyle.None)
            {
                WindowStyle = WindowStyle.SingleBorderWindow;
                WindowState = WindowState.Normal;
            }
            else
            {
                WindowStyle = WindowStyle.None;
                WindowState = WindowState.Maximized;
            }
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            StartPresentation();
            e.Handled = true;
        }
        else if (e.Key == Key.F5)
        {
            StartPresentation(0);
            e.Handled = true;
        }
    }

    private void Window_Closing(object sender, CancelEventArgs e)
    {
        // Don't prompt if auto-presenting
        if (_autoPresent) return;

        if (_isDirty)
        {
            var result = MessageBox.Show(
                "未保存の変更があります。保存しますか？",
                "確認", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            switch (result)
            {
                case MessageBoxResult.Yes:
                    SaveProject();
                    break;
                case MessageBoxResult.Cancel:
                    e.Cancel = true;
                    break;
            }
        }
    }
}
