using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace ScanKiosk;

public sealed class TileVm
{
    public required Naps2Profile Profile { get; init; }
    public required string Label { get; init; }
    public required string Description { get; init; }
    public bool IsButtonPreset { get; init; }
    public Brush Border => IsButtonPreset ? (Brush)Application.Current.Resources["Accent"] : Brushes.Transparent;
    public Visibility BadgeVisibility => IsButtonPreset ? Visibility.Visible : Visibility.Hidden;
}

public sealed record ThumbVm(BitmapSource Image, string Caption);

public partial class MainWindow : Window
{
    readonly KioskConfig _cfg;
    readonly KioskState _state = KioskState.Load();
    readonly ScanRunner _runner = new();
    readonly ButtonService? _button;
    readonly DispatcherTimer _countdown = new() { Interval = TimeSpan.FromSeconds(1) };
    readonly DispatcherTimer _exitHold = new();
    List<TileVm> _tiles = new();
    int _secondsLeft;
    bool _scanning;
    TaskCompletionSource<bool>? _message;

    // the document currently shown on the review screen
    string? _docLocal, _docDest;
    Naps2Profile? _docProfile;
    string? _docLabel;

    public MainWindow(KioskConfig cfg, bool windowed, bool buttonEnabled)
    {
        _cfg = cfg;
        InitializeComponent();
        Directory.CreateDirectory(ScanRunner.WorkDir);
        if (!windowed)
        {
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            WindowState = WindowState.Maximized;
            Topmost = true;
        }
        _countdown.Tick += (_, _) => CountdownTick();
        _exitHold.Interval = TimeSpan.FromSeconds(cfg.ExitHoldSeconds);
        _exitHold.Tick += async (_, _) => { _exitHold.Stop(); await ConfirmExit(); };
        PreviewMouseDown += (_, _) => ResetCountdown();
        PreviewTouchDown += (_, _) => ResetCountdown();
        _runner.PageScanned += n => Dispatcher.BeginInvoke(() => PageCountText.Text = $"Page {n}");

        LoadTiles();
        if (buttonEnabled)
        {
            _button = new ButtonService(cfg);
            _button.StatusChanged += (ok, msg) => Dispatcher.BeginInvoke(() => SetStatus(ok, msg));
            _button.Pressed += () => Dispatcher.BeginInvoke(OnPhysicalButton);
            Loaded += (_, _) => _button.Start();
        }
        else SetStatus(false, "Scan button off");
        Closed += (_, _) => _button?.Dispose();
    }

    // ------------------------------------------------------------------ home

    void LoadTiles()
    {
        try
        {
            var profiles = Naps2Profiles.Load(_cfg.ProfilesPath);
            var tiles = Naps2Profiles.Tiles(profiles, _cfg);
            var buttonProfile = tiles.FirstOrDefault(t => t.Profile.Name == _state.LastProfile).Profile
                                ?? tiles.FirstOrDefault(t => t.Profile.IsDefault).Profile
                                ?? tiles.FirstOrDefault().Profile;
            _tiles = tiles.Select(t => new TileVm
            {
                Profile = t.Profile, Label = t.Label, Description = t.Description,
                IsButtonPreset = t.Profile == buttonProfile,
            }).ToList();
            Tiles.ItemsSource = _tiles;
            var btn = _tiles.FirstOrDefault(t => t.IsButtonPreset);
            HintText.Text = btn == null
                ? "No NAPS2 presets found for the scanner."
                : $"Load the pages in the feeder, then tap a preset — or press the scanner's button for “{btn.Label}”.";
        }
        catch (Exception ex)
        {
            Log.Write($"Loading NAPS2 profiles failed: {ex.Message}");
            Tiles.ItemsSource = null;
            HintText.Text = $"Could not read the NAPS2 presets ({ex.Message}).";
        }
    }

    void SetStatus(bool ok, string text)
    {
        StatusText.Text = text;
        StatusDot.Fill = (Brush)FindResource(ok ? "Ok" : "Danger");
    }

    void Show(UIElement view)
    {
        foreach (var v in new UIElement[] { HomeView, ScanView, ReviewView })
            v.Visibility = v == view ? Visibility.Visible : Visibility.Collapsed;
        if (view == ReviewView) StartCountdown(); else _countdown.Stop();
    }

    void ShowHome()
    {
        LoadTiles();
        Show(HomeView);
    }

    async void Tile_Click(object sender, RoutedEventArgs e)
    {
        if (_scanning || ((FrameworkElement)sender).Tag is not TileVm tile) return;
        await Scan(tile.Profile, tile.Label, append: false);
    }

    async void OnPhysicalButton()
    {
        if (_scanning || MessageView.Visibility == Visibility.Visible) return;
        var tile = _tiles.FirstOrDefault(t => t.IsButtonPreset);
        if (tile == null) return;
        Log.Write($"Scan button -> {tile.Profile.Name}");
        if (ReviewView.Visibility == Visibility.Visible) FinishDocument();  // the button always starts a new document
        await Scan(tile.Profile, tile.Label, append: false);
    }

    // ------------------------------------------------------------------ scanning

    async Task Scan(Naps2Profile profile, string label, bool append)
    {
        _scanning = true;
        try
        {
            if (!append)
            {
                _state.LastProfile = profile.Name;
                _state.Save();
            }
            ScanPresetText.Text = append ? $"{label} — adding pages" : label;
            PageCountText.Text = "Starting";
            CancelButton.IsEnabled = true;
            Show(ScanView);

            string output = Path.Combine(ScanRunner.WorkDir, $"scan_{DateTime.Now:yyyyMMdd_HHmmss_fff}.pdf");
            var result = await _runner.RunAsync(_cfg, profile, output, append ? _docLocal : null);

            if (!result.Ok)
            {
                TryDelete(output);
                if (result.Error != null) await ShowMessage("Scan not saved", result.Error, "OK");
                if (append && _docLocal != null) await ShowReview(); else ShowHome();
                return;
            }

            string dest = append && _docDest != null
                ? _docDest
                : ScanRunner.ResolveOutputPath(profile.AutoSavePath ?? _cfg.DefaultOutputPattern, DateTime.Now);
            try
            {
                await Task.Run(() => ScanRunner.Publish(output, dest));
                Log.Write($"Saved {dest}");
            }
            catch (Exception ex)
            {
                Log.Write($"Publish to {dest} failed: {ex.Message}; scan kept at {output}");
                await ShowMessage("Could not save the scan",
                    $"The scan could not be copied to\n{Path.GetDirectoryName(dest)}\n\n{ex.Message}\n\nA copy was kept on this computer:\n{output}", "OK");
                ShowHome();
                return;
            }

            if (append && _docLocal != null) TryDelete(_docLocal);
            _docLocal = output;
            _docDest = dest;
            _docProfile = profile;
            _docLabel = label;
            await ShowReview(added: append ? result.PagesScanned : null);
        }
        finally
        {
            _scanning = false;
        }
    }

    void Cancel_Click(object sender, RoutedEventArgs e)
    {
        CancelButton.IsEnabled = false;
        PageCountText.Text = "Cancelling";
        _runner.Cancel();
    }

    // ------------------------------------------------------------------ review

    async Task ShowReview(int? added = null)
    {
        if (_docLocal == null) { ShowHome(); return; }
        Show(ReviewView);
        ReviewTitle.Text = "Saved";
        ReviewFileText.Text = $"{Path.GetFileName(_docDest)}   ·   {_docLabel}   ·   {Path.GetDirectoryName(_docDest)}";
        Thumbs.ItemsSource = null;
        try
        {
            double scale = VisualTreeHelper.GetDpi(this).DpiScaleY;
            var images = await PdfPages.ThumbnailsAsync(_docLocal, (int)(420 * scale));
            Thumbs.ItemsSource = images.Select((img, i) => new ThumbVm(img, $"Page {i + 1}")).ToList();
            ReviewTitle.Text = added is int a
                ? $"Saved {images.Count} pages  (+{a} added)"
                : $"Saved {images.Count} page{(images.Count == 1 ? "" : "s")}";
            ThumbScroll.ScrollToEnd();
        }
        catch (Exception ex)
        {
            Log.Write($"Preview failed: {ex.Message}");
            ReviewTitle.Text = "Saved (preview unavailable)";
        }
    }

    void Done_Click(object sender, RoutedEventArgs e)
    {
        FinishDocument();
        ShowHome();
    }

    async void AddPages_Click(object sender, RoutedEventArgs e)
    {
        if (_scanning || _docProfile == null || _docLabel == null) return;
        await Scan(_docProfile, _docLabel, append: true);
    }

    async void Discard_Click(object sender, RoutedEventArgs e)
    {
        if (_docDest == null) return;
        _countdown.Stop();
        if (!await ShowMessage("Discard this scan?",
                "The file will be moved to the _discarded folder next to the scans.", "Discard", "Keep it"))
        {
            StartCountdown();
            return;
        }
        try
        {
            if (File.Exists(_docDest))
            {
                var dir = Path.Combine(Path.GetDirectoryName(_docDest)!, "_discarded");
                Directory.CreateDirectory(dir);
                var target = Path.Combine(dir, Path.GetFileName(_docDest));
                for (int i = 1; File.Exists(target); i++)
                    target = Path.Combine(dir, $"{Path.GetFileNameWithoutExtension(_docDest)}_{i}.pdf");
                File.Move(_docDest, target);
                Log.Write($"Discarded {_docDest} -> {target}");
            }
            else Log.Write($"Discard: {_docDest} no longer exists (already processed?)");
        }
        catch (Exception ex)
        {
            Log.Write($"Discard failed: {ex.Message}");
            await ShowMessage("Could not discard", ex.Message, "OK");
        }
        FinishDocument();
        ShowHome();
    }

    void FinishDocument()
    {
        if (_docLocal != null) TryDelete(_docLocal);
        _docLocal = _docDest = null;
        _docProfile = null;
        _docLabel = null;
        Thumbs.ItemsSource = null;
    }

    void StartCountdown()
    {
        _secondsLeft = _cfg.AutoReturnSeconds;
        UpdateCountdownText();
        _countdown.Start();
    }

    void ResetCountdown()
    {
        if (_countdown.IsEnabled) { _secondsLeft = _cfg.AutoReturnSeconds; UpdateCountdownText(); }
    }

    void CountdownTick()
    {
        if (MessageView.Visibility == Visibility.Visible) return;
        if (--_secondsLeft <= 0)
        {
            _countdown.Stop();
            FinishDocument();
            ShowHome();
            return;
        }
        UpdateCountdownText();
    }

    void UpdateCountdownText() =>
        CountdownText.Text = $"Returning to the start screen in {_secondsLeft} s";

    // ------------------------------------------------------------------ messages / exit

    public void ShowError(string title, string body) => _ = ShowMessage(title, body, "OK");

    Task<bool> ShowMessage(string title, string body, string primary, string? secondary = null)
    {
        _message?.TrySetResult(false);
        _message = new TaskCompletionSource<bool>();
        MessageTitle.Text = title;
        MessageBody.Text = body;
        MessagePrimary.Content = primary;
        MessageSecondary.Content = secondary ?? "";
        MessageSecondary.Visibility = secondary == null ? Visibility.Collapsed : Visibility.Visible;
        MessageView.Visibility = Visibility.Visible;
        return _message.Task;
    }

    void CloseMessage(bool result)
    {
        MessageView.Visibility = Visibility.Collapsed;
        _message?.TrySetResult(result);
    }

    void MessagePrimary_Click(object sender, RoutedEventArgs e) => CloseMessage(true);
    void MessageSecondary_Click(object sender, RoutedEventArgs e) => CloseMessage(false);

    void Exit_Down(object sender, MouseButtonEventArgs e) => _exitHold.Start();
    void Exit_Up(object sender, MouseEventArgs e) => _exitHold.Stop();

    async Task ConfirmExit()
    {
        if (_scanning) return;
        if (await ShowMessage("Exit the scan kiosk?", "The kiosk will close until it is started again.", "Exit", "Stay"))
            Close();
    }

    static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    // ------------------------------------------------------------------ layout snapshots (--snapshot)

    public async Task SnapshotAsync(string dir, string? samplePdf)
    {
        Directory.CreateDirectory(dir);
        Left = -20000; Top = 0; ShowActivated = false;
        Show();
        SetStatus(true, "Scanner ready");
        async Task Save(string name)
        {
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Root.UpdateLayout();
            var bmp = new RenderTargetBitmap((int)Root.ActualWidth, (int)Root.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            bmp.Render(Root);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(bmp));
            await using var fs = File.Create(Path.Combine(dir, name + ".png"));
            enc.Save(fs);
        }
        Show(HomeView);
        await Save("1_home");
        ScanPresetText.Text = _tiles.FirstOrDefault()?.Label ?? "Preset";
        PageCountText.Text = "Page 3";
        Show(ScanView);
        await Save("2_scanning");
        if (samplePdf != null)
        {
            var local = Path.Combine(ScanRunner.WorkDir, "snapshot_sample.pdf");
            File.Copy(samplePdf, local, true);
            _docLocal = local;
            _docDest = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Scans", Path.GetFileName(samplePdf));
            _docLabel = _tiles.FirstOrDefault()?.Label ?? "Preset";
            await ShowReview();
            await Save("3_review");
            _countdown.Stop();
        }
        _ = ShowMessage("Scan not saved", "No pages were scanned.\nCheck that the paper is loaded in the feeder, then try again.", "OK");
        await Save("4_message");
        Close();
    }
}
