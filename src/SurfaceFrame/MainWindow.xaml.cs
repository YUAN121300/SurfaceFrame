using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace SurfaceFrame;

public partial class MainWindow : Window
{
    readonly LoadedConfig _config;
    readonly HaRuntime _runtime;
    readonly PresenceDirector _presence = new();
    readonly GestureRecognizer _gestures = new();
    readonly SlideDeck _deck = new();
    readonly IdleReturn _idle = new();
    readonly SlideClock _clock;
    readonly List<ControlCardUi> _controlCards = new();
    readonly List<SceneCardUi> _sceneCards = new();
    readonly Dictionary<string, DateTime> _sceneFlashes = new(StringComparer.Ordinal);
    readonly Dictionary<int, string> _touchTags = new();
    Point? _mouseDownPos;
    string? _mouseDownTag;
    DateTime _mouseDownAt;
    UtilityMonthSummary? _lastUtilitySummary;
    readonly Dictionary<string, string> _liveMap = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, PhotoMetadata> _metaMap = new(StringComparer.OrdinalIgnoreCase);
    readonly string _albumFolder;
    readonly string _cacheRoot;
    readonly DispatcherTimer _timer;
    readonly HttpClient? _http;
    readonly CancellationTokenSource _stop = new();

    IReadOnlyList<string> _loadedPhotos = Array.Empty<string>();
    IReadOnlyList<ImmichAlbumInfo> _albums = Array.Empty<ImmichAlbumInfo>();
    bool _immichEnabled;
    string _activeAlbumId;
    string _activeAlbumName = "相册";
    string _cacheFolder;
    string _liveCacheFolder;

    IntPtr _hwnd;
    Page _page = Page.Photos;
    bool _pumping;
    bool _powerWake;
    bool _forcePowerOff;
    bool _offQueued;
    bool _sawMaintenance;
    const int OnlineRandomBatchSize = 60;
    readonly List<Border> _pinDots = new();
    bool _pickerOpen;
    bool _pinUnlocked;
    string _pinBuffer = "";
    IReadOnlyList<string>? _pendingRandomFiles;
    bool _remoteLocked;
    string? _lastRemoteAlbum;
    string? _pendingRemoteAlbum;
    string? _lastRemoteOptionsSignature;
    DateTime _lastAlbumsRefresh = DateTime.MinValue;
    DateTime _lastPowerAssert = DateTime.MinValue;
    DateTime _lastAwake = DateTime.MinValue;
    DateTime _lastImmich = DateTime.MinValue;
    DateTime _lastClockUi = DateTime.MinValue;
    int _immichGate;
    int _albumsFetchGate;
    CancellationTokenSource? _liveCts;
    CancellationTokenSource? _syncCts;

    HeaderUi _haHeader = null!;
    HeaderUi _infoHeader = null!;
    InfoUi _infoUi = null!;

    static readonly FontFamily Mdl2 = new("Segoe MDL2 Assets");

    // Daylight White Palette
    static readonly Brush CardBg = Freeze(Color.FromRgb(255, 255, 255));
    static readonly Brush CardBorder = Freeze(Color.FromRgb(226, 232, 240));
    static readonly Brush SubCardBg = Freeze(Color.FromRgb(248, 250, 252));
    static readonly Brush SubCardBorder = Freeze(Color.FromRgb(241, 245, 249));
    static readonly Brush TextPrimary = Freeze(Color.FromRgb(15, 23, 42));
    static readonly Brush TextSecondary = Freeze(Color.FromRgb(100, 116, 139));
    static readonly Brush TextMuted = Freeze(Color.FromRgb(148, 163, 184));

    static readonly Brush LightOnBg = Freeze(Color.FromRgb(255, 251, 235));
    static readonly Brush LightOnBorder = Freeze(Color.FromRgb(252, 211, 77));
    static readonly Brush AmberSolid = Freeze(Color.FromRgb(245, 158, 11));
    static readonly Brush AmberText = Freeze(Color.FromRgb(180, 83, 9));
    static readonly Brush GridLineStroke = Freeze(Color.FromRgb(226, 232, 240));
    static readonly Brush ElecBarBrush = Freeze(Color.FromRgb(245, 158, 11));
    static readonly Brush GasBarBrush = Freeze(Color.FromRgb(59, 130, 246));

    static readonly Brush SwitchOnBg = Freeze(Color.FromRgb(239, 246, 255));
    static readonly Brush SwitchOnBorder = Freeze(Color.FromRgb(147, 197, 253));
    static readonly Brush BlueSolid = Freeze(Color.FromRgb(37, 99, 235));
    static readonly Brush BlueSoftBg = Freeze(Color.FromRgb(219, 234, 254));
    static readonly Brush BlueText = Freeze(Color.FromRgb(29, 78, 216));

    static readonly Brush GreenSolid = Freeze(Color.FromRgb(16, 185, 129));
    static readonly Brush GreenSoftBg = Freeze(Color.FromRgb(209, 250, 229));
    static readonly Brush GreenText = Freeze(Color.FromRgb(4, 120, 87));

    static readonly Brush RoseSolid = Freeze(Color.FromRgb(225, 29, 72));
    static readonly Brush RoseSoftBg = Freeze(Color.FromRgb(255, 228, 230));
    static readonly Brush RoseBorder = Freeze(Color.FromRgb(253, 164, 175));

    static readonly Brush PillBg = Freeze(Color.FromRgb(241, 245, 249));
    static readonly Brush SegmentBg = Freeze(Color.FromRgb(226, 232, 240));

    public MainWindow(LoadedConfig config)
    {
        _config = config;
        _clock = new SlideClock(TimeSpan.FromSeconds(config.Album.IntervalSeconds));
        _runtime = new HaRuntime(config);
        _albumFolder = config.Resolve(config.Album.Folder);
        _cacheRoot = config.Resolve(config.Immich.CacheFolder);
        _immichEnabled = config.Immich.Enabled && !config.Demo.Enabled;
        _activeAlbumId = config.Immich.AlbumId;
        _cacheFolder = Path.Combine(_cacheRoot, _activeAlbumId);
        _liveCacheFolder = Path.Combine(_cacheFolder, "live");

        if (!config.Demo.Enabled && !string.IsNullOrEmpty(config.ImmichApiKey))
        {
            _http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
            {
                Timeout = TimeSpan.FromSeconds(20),
                MaxResponseContentBufferSize = 32 * 1024 * 1024
            };
        }

        InitializeComponent();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _timer.Tick += (_, _) => Pump();
        PreviewTouchDown += OnTouchStarted;
        PreviewTouchMove += OnTouchMoved;
        PreviewTouchUp += OnTouchEnded;
        LostTouchCapture += (_, e) =>
        {
            _gestures.CancelContact(e.TouchDevice.Id);
            _touchTags.Remove(e.TouchDevice.Id);
        };
        Deactivated += (_, _) => _gestures.Invalidate();
        Loaded += OnLoaded;
        Closed += (_, _) =>
        {
            _timer.Stop();
            CancelLivePhoto();
            CancelSync();
            _stop.Cancel();
            DisplayPower.AllowSleep();
            LivePhotoPlayer.Shutdown();
            _runtime.Dispose();
            _http?.Dispose();
            _stop.Dispose();
            Log.Info("application stopped");
        };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        if (PresentationSource.FromVisual(this) is HwndSource source)
            source.AddHook(WndProc);
        FitScreen();
        DisplayPower.KeepTopmost(_hwnd);
        DisplayPower.KeepSystemAwake(false);
    }

    void OnLoaded(object sender, RoutedEventArgs e)
    {
        FitScreen();
        PhotoImage.Stretch = _config.Album.Crop ? Stretch.UniformToFill : Stretch.Uniform;
        LivePhotoImage.Stretch = PhotoImage.Stretch;
        LeftPhotoImage.Stretch = PhotoImage.Stretch;
        RightPhotoImage.Stretch = PhotoImage.Stretch;
        LeftLivePhotoImage.Stretch = PhotoImage.Stretch;
        RightLivePhotoImage.Stretch = PhotoImage.Stretch;
        SinglePhotoHost.Crop = _config.Album.Crop;
        LeftPhotoHost.Crop = _config.Album.Crop;
        RightPhotoHost.Crop = _config.Album.Crop;

        _haHeader = BuildHeader(HaHeaderHost, Page.Ha);
        _infoHeader = BuildHeader(InfoHeaderHost, Page.Info);
        BuildHaControls();
        _infoUi = BuildInfoBento();
        BuildDemo();
        BuildPinKeypad();

        LoadActiveLiveMap();
        var cached = _immichEnabled && _http != null ? ImmichSync.ListCache(_cacheFolder) : Array.Empty<string>();
        _activeAlbumName = _immichEnabled ? "Immich 相册" : "本地相册";
        UpdateHeaderAlbums();

        ReloadAlbum(cached.Count > 0 ? cached : PhotoDeck.List(_albumFolder), usingCache: cached.Count > 0);
        Log.Info(_config.Demo.Enabled ? "application started (demo)" : "application started");
        if (_config.Error != null)
            Log.Info(_config.Error);

        _clock.Arm(DateTime.UtcNow);
        _runtime.Start();
        if (_http != null && !_config.Demo.Enabled)
            _ = FetchAlbumsListAsync();
        Pump();
        _timer.Start();
        Activate();
        Focus();
    }

    void FitScreen()
    {
        WindowState = WindowState.Normal;
        Left = 0;
        Top = 0;
        Width = SystemParameters.PrimaryScreenWidth;
        Height = SystemParameters.PrimaryScreenHeight;
        Topmost = true;
        if (_hwnd != IntPtr.Zero)
        {
            DisplayPower.MarkFullscreen(_hwnd, true);
            DisplayPower.KeepTopmost(_hwnd);
        }
    }

    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_SETTINGCHANGE = 0x001A;
        const int WM_POWERBROADCAST = 0x0218;
        const int WM_DISPLAYCHANGE = 0x007E;
        const int WM_DPICHANGED = 0x02E0;
        if (msg == WM_POWERBROADCAST)
            _powerWake = true;
        else if (msg is WM_DISPLAYCHANGE or WM_DPICHANGED or WM_SETTINGCHANGE)
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(FitScreen));
        return IntPtr.Zero;
    }

    void Pump()
    {
        if (_pumping)
            return;
        _pumping = true;
        try
        {
            var now = DateTime.UtcNow;
            _runtime.Tick(now);
            var snap = _runtime.Take();
            if (snap.Fault)
                _runtime.DropSocket();

            _presence.Sample(snap.Presence, snap.Phase == ConnPhase.Live, now);
            var command = _presence.ConsumeCommand();
            var maintenance = _presence.MaintenanceActive(now);
            if (_sawMaintenance && !maintenance)
                _forcePowerOff = true;
            _sawMaintenance = maintenance;

            if (command == DisplayCommand.TurnOn && !_remoteLocked)
                WakeToAlbum();
            else if (command == DisplayCommand.TurnOff && !maintenance)
            {
                Log.Info("display off requested");
                BlackShieldThenMonitorOff();
            }

            if (_presence.ConsumeArrivedHome() && !_remoteLocked)
                ShowPage(Page.Photos);

            SyncRemoteControls(snap, now, maintenance);

            if ((_page == Page.Ha || _page == Page.Info) && _idle.Due(now, true))
                ShowPage(Page.Photos);

            var shield = (_presence.Shield || _remoteLocked) && !maintenance;
            var monitorLatched = _presence.MonitorLatched || _remoteLocked;
            var shieldVisibility = shield ? Visibility.Visible : Visibility.Collapsed;
            if (ShieldLayer.Visibility != shieldVisibility)
                _gestures.Invalidate();
            ShieldLayer.Visibility = shieldVisibility;
            if (shield)
            {
                CancelLivePhoto();
                if (_config.Demo.Enabled)
                    ShieldText.Text = ShieldCaption(snap.Presence);
            }

            if (monitorLatched && !maintenance
                && (_powerWake || _forcePowerOff || now - _lastPowerAssert >= TimeSpan.FromSeconds(3)))
            {
                BlackShieldThenMonitorOff();
                _forcePowerOff = false;
                _powerWake = false;
            }

            if (!shield && _page == Page.Photos && _clock.Due(now))
                Advance(1);

            if (now - _lastAwake >= TimeSpan.FromSeconds(30))
            {
                DisplayPower.KeepSystemAwake(!shield);
                if (_hwnd != IntPtr.Zero)
                {
                    DisplayPower.MarkFullscreen(_hwnd, true);
                    DisplayPower.KeepTopmost(_hwnd);
                }
                _lastAwake = now;
            }

            if (now - _lastClockUi >= TimeSpan.FromSeconds(1))
            {
                _lastClockUi = now;
                UpdateClockText();
                if (WindowState != WindowState.Normal
                    || Math.Abs(Left) > 1 || Math.Abs(Top) > 1
                    || Math.Abs(Width - SystemParameters.PrimaryScreenWidth) > 1
                    || Math.Abs(Height - SystemParameters.PrimaryScreenHeight) > 1)
                {
                    FitScreen();
                }
            }

            RefreshHeaderStatus(snap);
            RefreshControls(snap);
            RefreshInfoBento(snap);
            MaybeImmich(now);
        }
        finally
        {
            _pumping = false;
        }
    }

    void BlackShieldThenMonitorOff()
    {
        if (!ShieldOn)
            _gestures.Invalidate();
        CancelLivePhoto();
        CloseAlbumPicker();
        ShieldLayer.Visibility = Visibility.Visible;
        DemoHost.Visibility = Visibility.Collapsed;
        if (_offQueued)
            return;

        _offQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
        {
            _offQueued = false;
            if (_stop.IsCancellationRequested || !(_presence.MonitorLatched || _remoteLocked) || _presence.MaintenanceActive(DateTime.UtcNow))
                return;
            DisplayPower.KeepSystemAwake(false);
            DisplayPower.MonitorOff(_hwnd);
            _lastPowerAssert = DateTime.UtcNow;
        }));
    }

    void WakeToAlbum()
    {
        if (_hwnd != IntPtr.Zero)
            DisplayPower.MonitorOn(_hwnd);
        DisplayPower.KeepSystemAwake(true);
        if (ShieldOn)
            _gestures.Invalidate();
        ShieldLayer.Visibility = Visibility.Collapsed;
        ShowPage(Page.Photos);
        Log.Info("display on requested");
        _lastPowerAssert = DateTime.UtcNow;
    }

    string ShieldCaption(PresenceKind kind)
    {
        if (!string.IsNullOrEmpty(_config.Error))
            return _config.Error!;
        if (_presence.MonitorLatched)
            return "离家息屏";
        if (kind == PresenceKind.Away && !_presence.EverHome)
            return "当前离家";
        return "等待在家确认";
    }

    void ShowPage(Page page)
    {
        if (_page != page)
            _gestures.Invalidate();
        if (page != Page.Photos)
            CancelLivePhoto();
        if (page == Page.Photos)
            CloseAlbumPicker();

        _page = page;
        PhotoLayer.Visibility = page == Page.Photos ? Visibility.Visible : Visibility.Collapsed;
        HaLayer.Visibility = page == Page.Ha ? Visibility.Visible : Visibility.Collapsed;
        InfoLayer.Visibility = page == Page.Info ? Visibility.Visible : Visibility.Collapsed;
        DemoHost.Visibility = _config.Demo.Enabled && page != Page.Photos && !ShieldOn
            ? Visibility.Visible : Visibility.Collapsed;

        var now = DateTime.UtcNow;
        if (page == Page.Photos)
        {
            _clock.Arm(now);
            if (_deck.Current is PhotoSlide cur && !ShieldOn)
                StartLivePhotosForSlide(cur);
        }
        else
        {
            _idle.Touch(now);
        }
    }

    bool ShieldOn => ShieldLayer.Visibility == Visibility.Visible;

    void OnTouchStarted(object sender, TouchEventArgs e)
    {
        e.Handled = true;
        var point = e.GetTouchPoint(this).Position;
        var now = DateTime.UtcNow;
        var id = e.TouchDevice.Id;
        _gestures.TouchDown(id, point.X, point.Y, now);
        if (HitTag(point, e.OriginalSource as DependencyObject) is string tag)
            _touchTags[id] = tag;
        if (!e.TouchDevice.Capture(this))
            _gestures.CancelContact(id);
        if ((_page == Page.Ha || _page == Page.Info) && !ShieldOn)
            _idle.Touch(now);
    }

    void OnTouchMoved(object sender, TouchEventArgs e)
    {
        e.Handled = true;
        var point = e.GetTouchPoint(this).Position;
        _gestures.TouchMove(e.TouchDevice.Id, point.X, point.Y);
        if ((_page == Page.Ha || _page == Page.Info) && !ShieldOn)
            _idle.Touch(DateTime.UtcNow);
    }

    void OnTouchEnded(object sender, TouchEventArgs e)
    {
        e.Handled = true;
        var point = e.GetTouchPoint(this).Position;
        var now = DateTime.UtcNow;
        var id = e.TouchDevice.Id;
        var action = _gestures.TouchUp(id, point.X, point.Y, now);
        _touchTags.Remove(id, out var downTag);
        if (e.TouchDevice.Captured == this)
            e.TouchDevice.Capture(null);
        if ((_page == Page.Ha || _page == Page.Info) && !ShieldOn)
            _idle.Touch(now);
        if (action == UserAction.None)
            return;
        var width = ActualWidth > 1 ? ActualWidth : SystemParameters.PrimaryScreenWidth;
        var tag = action == UserAction.Tap
            ? downTag ?? HitTag(point, e.OriginalSource as DependencyObject)
            : null;
        if (_pickerOpen)
        {
            if (action == UserAction.Tap && tag != null && PointerPolicy.TryParseAlbum(tag, out var modalAct))
                HandleAlbumAction(modalAct);
            return;
        }
        ApplyOutcome(PointerPolicy.Interpret(action, point.X, width, _page, ShieldOn, tag), now);
    }

    protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (ShieldOn && (_presence.MonitorLatched || _remoteLocked) && !_presence.MaintenanceActive(DateTime.UtcNow))
        {
            BlackShieldThenMonitorOff();
            return;
        }
        if (e.StylusDevice != null)
            return;
        var point = e.GetPosition(this);
        _mouseDownPos = point;
        _mouseDownAt = DateTime.UtcNow;
        _mouseDownTag = HitTag(point, e.OriginalSource as DependencyObject);
        if ((_page == Page.Ha || _page == Page.Info) && !ShieldOn)
            _idle.Touch(_mouseDownAt);
    }

    protected override void OnPreviewMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (e.StylusDevice == null)
            DispatchMouseTap(e.GetPosition(this), e.OriginalSource as DependencyObject, DateTime.UtcNow);
    }

    void DispatchMouseTap(Point point, DependencyObject? source, DateTime now)
    {
        var downPos = _mouseDownPos;
        var downTag = _mouseDownTag;
        var downAt = _mouseDownAt;
        _mouseDownPos = null;
        _mouseDownTag = null;
        if (downPos is not Point start)
            return;
        if (Math.Abs(point.X - start.X) > 28 || Math.Abs(point.Y - start.Y) > 28 || now - downAt > TimeSpan.FromMilliseconds(900))
            return;
        if ((_page == Page.Ha || _page == Page.Info) && !ShieldOn)
            _idle.Touch(now);
        var tag = downTag ?? HitTag(point, source);
        if (tag == null)
            return;
        if (_pickerOpen)
        {
            if (PointerPolicy.TryParseAlbum(tag, out var modalAct))
                HandleAlbumAction(modalAct);
            return;
        }
        var width = ActualWidth > 1 ? ActualWidth : SystemParameters.PrimaryScreenWidth;
        ApplyOutcome(PointerPolicy.Interpret(UserAction.Tap, point.X, width, _page, ShieldOn, tag), now);
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        var now = DateTime.UtcNow;
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        var command = MaintKeys.Map(ctrl, shift, e.Key.ToString());
        if (command != MaintCommand.None)
        {
            e.Handled = true;
            if (!e.IsRepeat)
                OnMaint(command, now);
            return;
        }
        if (_pickerOpen && !_pinUnlocked && !ctrl && !shift)
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                CloseAlbumPicker();
                return;
            }
            if (e.Key == Key.Back)
            {
                e.Handled = true;
                HandleAlbumAction("pin:back");
                return;
            }
            var digit = e.Key switch
            {
                >= Key.D0 and <= Key.D9 => (char)('0' + (e.Key - Key.D0)),
                >= Key.NumPad0 and <= Key.NumPad9 => (char)('0' + (e.Key - Key.NumPad0)),
                _ => '\0'
            };
            if (digit != '\0')
            {
                e.Handled = true;
                HandleAlbumAction("pin:" + digit);
                return;
            }
        }
        if (!_config.Demo.Enabled || ctrl || shift)
            return;
        var demo = e.Key switch
        {
            Key.H => "home",
            Key.A => "away",
            Key.D => "drop",
            Key.R => "resume",
            Key.Q => "quick",
            Key.S => "slow",
            _ => null
        };
        if (demo == null)
            return;
        e.Handled = true;
        DoDemo(demo, now);
        Pump();
    }

    void OnMaint(MaintCommand command, DateTime now)
    {
        switch (command)
        {
            case MaintCommand.Exit:
                Close();
                break;
            case MaintCommand.Wake:
                if (_config.Demo.Enabled)
                    BeginMaintenance(now);
                break;
            case MaintCommand.TogglePage:
                if (ShieldOn)
                {
                    if (!_config.Demo.Enabled)
                        return;
                    BeginMaintenance(now);
                }
                var next = _page switch
                {
                    Page.Photos => Page.Ha,
                    Page.Ha => Page.Info,
                    _ => Page.Photos
                };
                ShowPage(next);
                Pump();
                break;
        }
    }

    void BeginMaintenance(DateTime now)
    {
        _presence.BeginMaintenance(now);
        _sawMaintenance = true;
        ShieldLayer.Visibility = Visibility.Collapsed;
        if (_hwnd != IntPtr.Zero)
            DisplayPower.MonitorOn(_hwnd);
        DisplayPower.KeepSystemAwake(true);
    }

    void ApplyOutcome(PointerOutcome outcome, DateTime now)
    {
        if (outcome.Control is string tag)
        {
            if (PointerPolicy.TryParseDemo(tag, out var demo))
                DoDemo(demo, now);
            else if (PointerPolicy.TryParseNav(tag, out var navPage))
                ShowPage(navPage);
            else if (PointerPolicy.TryParseAlbum(tag, out var albumAction))
                HandleAlbumAction(albumAction);
            else if (PointerPolicy.TryParseScene(tag, out var sceneId))
            {
                var ok = ExecuteScene(sceneId, now);
                Log.Info($"ha scene {sceneId} ok={ok}");
            }
            else if (PointerPolicy.TryParseRetry(tag, out var retryEntity))
            {
                var ok = _runtime.Retry(retryEntity, now);
                Log.Info($"ha retry {retryEntity} ok={ok}");
            }
            else if (PointerPolicy.TryParseToggle(tag, out var toggleEntity))
            {
                var snap = _runtime.Take();
                var cur = snap.AllStates.TryGetValue(toggleEntity, out var v) ? v.State : null;
                var turnOn = !string.Equals(cur, "on", StringComparison.OrdinalIgnoreCase);
                var ok = _runtime.Press(toggleEntity, turnOn, now);
                Log.Info($"ha toggle {toggleEntity} on={turnOn} ok={ok}");
            }
            else if (PointerPolicy.TryParseCover(tag, out var coverEntity, out var open))
            {
                var ok = _runtime.Press(coverEntity, open, now);
                Log.Info($"ha cover {coverEntity} open={open} ok={ok}");
            }
            else if (PointerPolicy.TryParseCard(tag, out var entityId, out var turnOn))
            {
                var ok = _runtime.Press(entityId, turnOn, now);
                Log.Info($"ha card {entityId} on={turnOn} ok={ok}");
            }
        }
        if (outcome.GoPage is Page page)
            ShowPage(page);
        if (outcome.PhotoDelta != 0 && _page == Page.Photos && !ShieldOn)
        {
            Advance(outcome.PhotoDelta);
            _clock.Arm(now);
        }
        var guard = outcome.GuardWake;
        Pump();
        if (guard && (_presence.MonitorLatched || _remoteLocked) && !_presence.MaintenanceActive(DateTime.UtcNow))
            BlackShieldThenMonitorOff();
    }

    void DoDemo(string action, DateTime now)
    {
        switch (action)
        {
            case "home":
                _runtime.SetPresence(_config.HomeAssistant.PresenceHome, now);
                break;
            case "away":
                _runtime.SetPresence(_config.HomeAssistant.PresenceAway, now);
                break;
            case "drop":
                _runtime.Drop(now);
                break;
            case "resume":
                _runtime.Resume(now);
                break;
            case "quick":
                _runtime.SetPace(ConfirmPace.Quick);
                break;
            case "slow":
                _runtime.SetPace(ConfirmPace.Slow);
                break;
        }
    }

    string? HitTag(Point point, DependencyObject? source = null)
    {
        if (FindTagUpward(source) is string direct)
            return direct;
        return FindTagUpward(VisualTreeHelper.HitTest(this, point)?.VisualHit as DependencyObject);
    }

    static string? FindTagUpward(DependencyObject? hit)
    {
        while (hit != null)
        {
            if (hit is FrameworkElement element && element.Tag is string tag)
                return element.IsVisible && element.IsEnabled && element.IsHitTestVisible ? tag : null;
            hit = VisualTreeHelper.GetParent(hit);
        }
        return null;
    }

    void ReloadAlbum(IReadOnlyList<string> files, bool usingCache)
    {
        if (_deck.Current != null && _loadedPhotos.SequenceEqual(files, StringComparer.OrdinalIgnoreCase))
            return;
        _loadedPhotos = files;
        var shuffledFiles = SlideDeck.Shuffle(files);
        var slides = SlideDeck.Shuffle(SlideDeck.BuildSlides(shuffledFiles));
        if (slides.Count > 0)
            Log.Info($"slides built {slides.Count} (dual={slides.Count(s => s.Secondary != null)}, single={slides.Count(s => s.Secondary == null)})");
        _deck.Reset(slides);
        if (_deck.Go(1, TryShow) == null)
        {
            if (usingCache)
                ReloadAlbum(PhotoDeck.List(_albumFolder), usingCache: false);
            else
                ShowEmpty();
        }
        else
        {
            ShowImage();
        }
    }

    void Advance(int direction)
    {
        if (direction > 0 && _pendingRandomFiles != null)
        {
            var nextBatch = _pendingRandomFiles;
            _pendingRandomFiles = null;
            LoadActiveLiveMap();
            ReloadAlbum(nextBatch, usingCache: true);
            return;
        }
        if (_deck.Go(direction, TryShow) == null)
            ShowEmpty();
        else
            ShowImage();
        if (direction > 0 && _immichEnabled && _deck.Count > 0 && _deck.CurrentIndex >= _deck.Count - 2
            && DateTime.UtcNow - _lastImmich >= TimeSpan.FromMinutes(2))
        {
            TriggerImmichSync(DateTime.UtcNow, showPill: false, isSwitch: false);
        }
    }

    bool TryShow(PhotoSlide slide)
    {
        CancelLivePhoto();
        if (slide.Secondary == null)
        {
            var bmp = LoadBitmap(slide.Primary);
            if (bmp == null)
                return false;
            PhotoImage.Source = bmp;
            LeftPhotoImage.Source = null;
            RightPhotoImage.Source = null;
            SinglePhotoHost.Visibility = Visibility.Visible;
            DualPhotoHost.Visibility = Visibility.Collapsed;
            LeftCaptionLayer.Visibility = Visibility.Collapsed;
            RightCaptionLayer.Visibility = Visibility.Collapsed;
            ApplyCaption(slide.Primary, SingleCaptionLayer, SingleCaptionCity, SingleCaptionDetail, compact: false);
        }
        else
        {
            var leftBmp = LoadBitmap(slide.Primary);
            var rightBmp = LoadBitmap(slide.Secondary);
            if (leftBmp == null || rightBmp == null)
                return false;
            LeftPhotoImage.Source = leftBmp;
            RightPhotoImage.Source = rightBmp;
            PhotoImage.Source = null;
            SinglePhotoHost.Visibility = Visibility.Collapsed;
            SingleCaptionLayer.Visibility = Visibility.Collapsed;
            DualPhotoHost.Visibility = Visibility.Visible;
            ApplyCaption(slide.Primary, LeftCaptionLayer, LeftCaptionCity, LeftCaptionDetail, compact: true);
            ApplyCaption(slide.Secondary, RightCaptionLayer, RightCaptionCity, RightCaptionDetail, compact: true);
        }

        if (_page == Page.Photos && !ShieldOn)
            StartLivePhotosForSlide(slide);
        return true;
    }

    static BitmapImage? LoadBitmap(string path)
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            image.UriSource = new Uri(Path.GetFullPath(path));
            image.EndInit();
            image.Freeze();
            return image.PixelWidth > 0 ? image : null;
        }
        catch
        {
            return null;
        }
    }

    void ApplyCaption(string path, UIElement host, TextBlock cityTxt, TextBlock detailTxt, bool compact)
    {
        var id = Path.GetFileNameWithoutExtension(path);
        if (string.IsNullOrEmpty(id) || !_metaMap.TryGetValue(id, out var meta) || meta.IsEmpty)
        {
            host.Visibility = Visibility.Collapsed;
            return;
        }

        var hasCity = !string.IsNullOrEmpty(meta.City);
        if (hasCity)
        {
            cityTxt.Text = meta.City!;
            cityTxt.Visibility = Visibility.Visible;
        }
        else
        {
            cityTxt.Text = "";
            cityTxt.Visibility = Visibility.Collapsed;
        }

        string? detail = (!string.IsNullOrEmpty(meta.Date), !string.IsNullOrEmpty(meta.Device)) switch
        {
            (true, true) => $"{meta.Date}  /  {meta.Device}",
            (true, false) => meta.Date,
            (false, true) => meta.Device,
            _ => null
        };
        if (!string.IsNullOrEmpty(detail))
        {
            detailTxt.Text = detail;
            detailTxt.Margin = hasCity ? new Thickness(0, compact ? 2 : 3, 0, 0) : new Thickness(0);
            detailTxt.Visibility = Visibility.Visible;
        }
        else
        {
            detailTxt.Text = "";
            detailTxt.Visibility = Visibility.Collapsed;
        }

        host.Visibility = Visibility.Visible;
    }

    void ShowEmpty()
    {
        CancelLivePhoto();
        PhotoImage.Source = null;
        LeftPhotoImage.Source = null;
        RightPhotoImage.Source = null;
        SinglePhotoHost.Visibility = Visibility.Visible;
        DualPhotoHost.Visibility = Visibility.Collapsed;
        SingleCaptionLayer.Visibility = Visibility.Collapsed;
        LeftCaptionLayer.Visibility = Visibility.Collapsed;
        RightCaptionLayer.Visibility = Visibility.Collapsed;
        EmptyHint.Visibility = Visibility.Visible;
    }

    void ShowImage() => EmptyHint.Visibility = Visibility.Collapsed;

    void CancelLivePhoto()
    {
        try { _liveCts?.Cancel(); } catch { }
        _liveCts?.Dispose();
        _liveCts = null;
        foreach (var img in new[] { LivePhotoImage, LeftLivePhotoImage, RightLivePhotoImage })
        {
            img.Visibility = Visibility.Collapsed;
            img.Source = null;
            img.LayoutTransform = Transform.Identity;
        }
    }

    void StartLivePhotosForSlide(PhotoSlide slide)
    {
        if (!_immichEnabled || _http == null || string.IsNullOrEmpty(_config.ImmichApiKey))
            return;

        var queue = new List<(string AssetId, string VideoId, System.Windows.Controls.Image Target)>();
        if (slide.Secondary == null)
        {
            var id = Path.GetFileNameWithoutExtension(slide.Primary);
            if (_liveMap.TryGetValue(id, out var vid) && !string.IsNullOrEmpty(vid))
                queue.Add((id, vid, LivePhotoImage));
        }
        else
        {
            var leftId = Path.GetFileNameWithoutExtension(slide.Primary);
            if (_liveMap.TryGetValue(leftId, out var leftVid) && !string.IsNullOrEmpty(leftVid))
                queue.Add((leftId, leftVid, LeftLivePhotoImage));
            var rightId = Path.GetFileNameWithoutExtension(slide.Secondary);
            if (_liveMap.TryGetValue(rightId, out var rightVid) && !string.IsNullOrEmpty(rightVid))
                queue.Add((rightId, rightVid, RightLivePhotoImage));
        }

        if (queue.Count == 0)
            return;

        CancelLivePhoto();
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        _liveCts = cts;
        _ = RunLivePhotoSequenceAsync(queue, cts.Token);
    }

    async Task RunLivePhotoSequenceAsync(
        IReadOnlyList<(string AssetId, string VideoId, System.Windows.Controls.Image Target)> queue,
        CancellationToken ct)
    {
        foreach (var item in queue)
        {
            if (ct.IsCancellationRequested)
                break;
            try
            {
                var videoPath = await ImmichSync.EnsureLivePhotoVideoAsync(
                    _http!,
                    _config.Immich.BaseUrl,
                    item.VideoId,
                    _config.ImmichApiKey!,
                    _liveCacheFolder,
                    item.AssetId,
                    ct).ConfigureAwait(false);
                if (videoPath == null || ct.IsCancellationRequested)
                    continue;

                await LivePhotoPlayer.PlayAsync(videoPath, Dispatcher, item.Target, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Log.Info("livephoto " + ex.GetType().Name);
            }
            finally
            {
                if (!ct.IsCancellationRequested)
                {
                    await Dispatcher.InvokeAsync(() =>
                    {
                        if (!ct.IsCancellationRequested)
                        {
                            item.Target.Visibility = Visibility.Collapsed;
                            item.Target.Source = null;
                            item.Target.LayoutTransform = Transform.Identity;
                        }
                    }, DispatcherPriority.Background);
                }
            }
        }
    }

    void LoadActiveLiveMap()
    {
        _liveMap.Clear();
        _metaMap.Clear();
        if (!_immichEnabled)
            return;
        foreach (var kv in ImmichSync.LoadLiveMap(_cacheFolder))
            _liveMap[kv.Key] = kv.Value;
        foreach (var kv in ImmichSync.LoadMetaMap(_cacheFolder))
            _metaMap[kv.Key] = kv.Value;
    }

    void MaybeImmich(DateTime now)
    {
        if (_http == null || string.IsNullOrEmpty(_config.ImmichApiKey) || _config.Demo.Enabled)
            return;
        if (now - _lastAlbumsRefresh >= TimeSpan.FromMinutes(20))
        {
            _lastAlbumsRefresh = now;
            _ = FetchAlbumsListAsync();
        }
        if (!_immichEnabled)
            return;
        if (now - _lastImmich < TimeSpan.FromMinutes(20))
            return;
        TriggerImmichSync(now, showPill: false, isSwitch: false);
    }

    void TriggerImmichSync(DateTime now, bool showPill, bool isSwitch = false)
    {
        if (!_immichEnabled || _http == null || string.IsNullOrEmpty(_config.ImmichApiKey))
            return;
        if (Interlocked.Exchange(ref _immichGate, 1) != 0)
            return;
        _lastImmich = now;
        CancelSync();
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        _syncCts = cts;
        var targetAlbumId = _activeAlbumId;
        var targetCacheDir = _cacheFolder;
        if (showPill)
        {
            AlbumStatusText.Text = $"正在同步「{_activeAlbumName}」…";
            AlbumStatusPill.Visibility = Visibility.Visible;
        }
        _ = SyncImmichAsync(targetAlbumId, targetCacheDir, isSwitch, cts.Token);
    }

    void CancelSync()
    {
        try { _syncCts?.Cancel(); } catch { }
        _syncCts?.Dispose();
        _syncCts = null;
    }

    async Task SyncImmichAsync(string albumId, string cacheDir, bool isSwitch, CancellationToken ct)
    {
        try
        {
            var batchLimit = Math.Min(_config.Immich.MaxPhotos, OnlineRandomBatchSize);
            var result = await ImmichSync.SyncAsync(
                _http!,
                _config.Immich.BaseUrl,
                albumId,
                _config.ImmichApiKey!,
                cacheDir,
                _albumFolder,
                ct,
                batchLimit,
                batch =>
                {
                    if (ct.IsCancellationRequested || albumId != _activeAlbumId)
                        return;
                    Dispatcher.InvokeAsync(() =>
                    {
                        if (ct.IsCancellationRequested || albumId != _activeAlbumId)
                            return;
                        if (isSwitch || _deck.Current == null)
                        {
                            LoadActiveLiveMap();
                            ReloadAlbum(batch, usingCache: true);
                        }
                        AlbumStatusPill.Visibility = Visibility.Collapsed;
                    }, DispatcherPriority.Background);
                },
                randomSample: true).ConfigureAwait(false);

            if (ct.IsCancellationRequested || albumId != _activeAlbumId)
                return;

            var usingCache = result.Status is ImmichStatus.Updated or ImmichStatus.KeptCache;
            var queued = false;
            await Dispatcher.InvokeAsync(() =>
            {
                if (ct.IsCancellationRequested || albumId != _activeAlbumId)
                    return;
                if (result.Status == ImmichStatus.Updated && !isSwitch && _deck.Current != null && _metaMap.Count > 0)
                {
                    _pendingRandomFiles = result.Files;
                    queued = true;
                }
                else
                {
                    _pendingRandomFiles = null;
                    LoadActiveLiveMap();
                    ReloadAlbum(result.Files, usingCache);
                }
                AlbumStatusPill.Visibility = Visibility.Collapsed;
            }, DispatcherPriority.Background, ct);
            Log.Info(queued ? "immich Updated (queued random batch)" : "immich " + result.Status);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.Info("immich " + ex.GetType().Name);
            await Dispatcher.InvokeAsync(() => AlbumStatusPill.Visibility = Visibility.Collapsed);
        }
        finally
        {
            Interlocked.Exchange(ref _immichGate, 0);
        }
    }

    async Task FetchAlbumsListAsync()
    {
        if (_http == null || string.IsNullOrEmpty(_config.ImmichApiKey))
            return;
        if (Interlocked.Exchange(ref _albumsFetchGate, 1) != 0)
            return;
        try
        {
            var list = await ImmichSync.FetchAlbumsAsync(_http, _config.Immich.BaseUrl, _config.ImmichApiKey!, _stop.Token).ConfigureAwait(false);
            await Dispatcher.InvokeAsync(() =>
            {
                _albums = list;
                var prevName = _activeAlbumName;
                var match = list.FirstOrDefault(a => string.Equals(a.Id, _activeAlbumId, StringComparison.OrdinalIgnoreCase));
                if (match != null && _immichEnabled)
                    _activeAlbumName = match.Name;
                UpdateHeaderAlbums();
                SyncRemoteAlbumOptionsToHa(DateTime.UtcNow);
                if (match != null && _immichEnabled && !string.Equals(prevName, match.Name, StringComparison.Ordinal))
                    SyncSelectedAlbumToHa(match.Name, DateTime.UtcNow);
                if (_pendingRemoteAlbum != null)
                {
                    var pending = _pendingRemoteAlbum;
                    _pendingRemoteAlbum = null;
                    TryApplyRemoteAlbum(pending);
                }
                if (_pickerOpen && _pinUnlocked)
                    PopulateAlbumList();
            }, DispatcherPriority.Background, _stop.Token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Info("albums " + ex.GetType().Name);
        }
        finally
        {
            Interlocked.Exchange(ref _albumsFetchGate, 0);
        }
    }

    void BuildPinKeypad()
    {
        PinDotsPanel.Children.Clear();
        _pinDots.Clear();
        var len = Math.Max(4, _config.Album.Pin?.Length ?? 4);
        for (var i = 0; i < len; i++)
        {
            var dot = new Border
            {
                Width = 16,
                Height = 16,
                CornerRadius = new CornerRadius(8),
                Margin = new Thickness(8, 0, 8, 0),
                Background = PillBg,
                BorderBrush = CardBorder,
                BorderThickness = new Thickness(1)
            };
            _pinDots.Add(dot);
            PinDotsPanel.Children.Add(dot);
        }

        PinKeypadGrid.Children.Clear();
        var keys = new (string Label, string Tag)[]
        {
            ("1", "album:pin:1"), ("2", "album:pin:2"), ("3", "album:pin:3"),
            ("4", "album:pin:4"), ("5", "album:pin:5"), ("6", "album:pin:6"),
            ("7", "album:pin:7"), ("8", "album:pin:8"), ("9", "album:pin:9"),
            ("清空", "album:pin:clear"), ("0", "album:pin:0"), ("退格", "album:pin:back")
        };
        foreach (var (label, tag) in keys)
        {
            var isAction = label.Length > 1;
            PinKeypadGrid.Children.Add(new Border
            {
                Tag = tag,
                MinHeight = 64,
                Margin = new Thickness(6),
                CornerRadius = new CornerRadius(14),
                Background = isAction ? PillBg : SubCardBg,
                BorderBrush = CardBorder,
                BorderThickness = new Thickness(1),
                Child = new TextBlock
                {
                    Text = label,
                    FontSize = isAction ? 16 : 24,
                    FontWeight = FontWeights.Bold,
                    Foreground = isAction ? TextSecondary : TextPrimary,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            });
        }
    }

    void UpdatePinDots()
    {
        for (var i = 0; i < _pinDots.Count; i++)
        {
            var filled = i < _pinBuffer.Length;
            _pinDots[i].Background = filled ? BlueSolid : PillBg;
            _pinDots[i].BorderBrush = filled ? BlueSolid : CardBorder;
        }
    }

    void HandleAlbumAction(string action)
    {
        if (action == "open")
        {
            _pickerOpen = true;
            _pinBuffer = "";
            PinErrorText.Text = "";
            if (!string.IsNullOrEmpty(_config.Album.Pin))
            {
                _pinUnlocked = false;
                AlbumPickerTitle.Text = "输入相册密码";
                AlbumPickerSub.Text = "更改展示相册前需验证密码";
                PinLockPanel.Visibility = Visibility.Visible;
                AlbumListScroll.Visibility = Visibility.Collapsed;
                UpdatePinDots();
            }
            else
            {
                _pinUnlocked = true;
                AlbumPickerTitle.Text = "选择展示相册";
                AlbumPickerSub.Text = "从 NAS Immich 全相册联网随机播放或切换本地目录";
                PinLockPanel.Visibility = Visibility.Collapsed;
                AlbumListScroll.Visibility = Visibility.Visible;
                PopulateAlbumList();
                if (_http != null && !_config.Demo.Enabled)
                    _ = FetchAlbumsListAsync();
            }
            AlbumPickerOverlay.Visibility = Visibility.Visible;
            return;
        }
        if (action == "close")
        {
            CloseAlbumPicker();
            return;
        }
        if (action.StartsWith("pin:", StringComparison.Ordinal))
        {
            var expected = _config.Album.Pin;
            if (string.IsNullOrEmpty(expected) || _pinUnlocked)
                return;
            var key = action[4..];
            if (key == "clear")
            {
                _pinBuffer = "";
                PinErrorText.Text = "";
                UpdatePinDots();
                return;
            }
            if (key == "back")
            {
                if (_pinBuffer.Length > 0)
                    _pinBuffer = _pinBuffer[..^1];
                PinErrorText.Text = "";
                UpdatePinDots();
                return;
            }
            if (key.Length == 1 && char.IsDigit(key[0]))
            {
                _pinBuffer += key;
                PinErrorText.Text = "";
                UpdatePinDots();
                if (_pinBuffer.Length >= expected.Length)
                {
                    if (string.Equals(_pinBuffer, expected, StringComparison.Ordinal))
                    {
                        _pinUnlocked = true;
                        _pinBuffer = "";
                        AlbumPickerTitle.Text = "选择展示相册";
                        AlbumPickerSub.Text = "从 NAS Immich 全相册联网随机播放或切换本地目录";
                        PinLockPanel.Visibility = Visibility.Collapsed;
                        AlbumListScroll.Visibility = Visibility.Visible;
                        PopulateAlbumList();
                        if (_http != null && !_config.Demo.Enabled)
                            _ = FetchAlbumsListAsync();
                        Log.Info("album pin unlocked");
                    }
                    else
                    {
                        _pinBuffer = "";
                        PinErrorText.Text = "密码错误，请重试";
                        UpdatePinDots();
                        Log.Info("album pin denied");
                    }
                }
            }
            return;
        }
        if (action.StartsWith("pick:", StringComparison.Ordinal))
        {
            if (!string.IsNullOrEmpty(_config.Album.Pin) && !_pinUnlocked)
                return;
            var target = action[5..];
            SelectAlbum(target);
        }
    }

    void CloseAlbumPicker()
    {
        _pickerOpen = false;
        _pinUnlocked = false;
        _pinBuffer = "";
        PinErrorText.Text = "";
        AlbumPickerOverlay.Visibility = Visibility.Collapsed;
    }

    void SyncRemoteControls(HaSnapshot snap, DateTime now, bool maintenance)
    {
        if (snap.Phase != ConnPhase.Live)
            return;

        var lockId = _config.HomeAssistant.RemoteLockEntityId;
        if (!string.IsNullOrEmpty(lockId))
        {
            var lockedNow = IsOn(snap.AllStates, lockId);
            if (lockedNow && !_remoteLocked)
            {
                _remoteLocked = true;
                Log.Info("remote lock on");
                if (!maintenance)
                    BlackShieldThenMonitorOff();
            }
            else if (!lockedNow && _remoteLocked)
            {
                _remoteLocked = false;
                Log.Info("remote lock off");
                if (!_presence.Shield)
                    WakeToAlbum();
            }
        }

        var albumEntityId = _config.HomeAssistant.RemoteAlbumEntityId;
        if (!string.IsNullOrEmpty(albumEntityId) && Val(snap.AllStates, albumEntityId) is string remoteVal)
        {
            if (!string.Equals(remoteVal, _lastRemoteAlbum, StringComparison.Ordinal))
            {
                _lastRemoteAlbum = remoteVal;
                TryApplyRemoteAlbum(remoteVal);
            }
        }
    }

    void TryApplyRemoteAlbum(string remoteVal)
    {
        var trimmed = remoteVal.Trim();
        if (string.IsNullOrEmpty(trimmed))
            return;

        if (string.Equals(trimmed, "本地相册", StringComparison.OrdinalIgnoreCase)
            || string.Equals(trimmed, "local", StringComparison.OrdinalIgnoreCase))
        {
            if (_immichEnabled)
            {
                Log.Info("remote album switched local");
                SelectAlbum("local", fromRemote: true);
            }
            return;
        }

        if (Guid.TryParse(trimmed, out var guid))
        {
            var idStr = guid.ToString("D");
            if (!_immichEnabled || !string.Equals(idStr, _activeAlbumId, StringComparison.OrdinalIgnoreCase))
            {
                Log.Info("remote album switched " + idStr);
                SelectAlbum(idStr, fromRemote: true);
            }
            return;
        }

        if (_albums.Count == 0)
        {
            _pendingRemoteAlbum = trimmed;
            return;
        }

        var match = _albums.FirstOrDefault(a => string.Equals(a.Name.Trim(), trimmed, StringComparison.OrdinalIgnoreCase))
            ?? _albums.FirstOrDefault(a => a.Name.Contains(trimmed, StringComparison.OrdinalIgnoreCase));
        if (match == null)
        {
            Log.Info("remote album not found: " + trimmed);
            return;
        }
        if (!_immichEnabled || !string.Equals(match.Id, _activeAlbumId, StringComparison.OrdinalIgnoreCase))
        {
            Log.Info("remote album switched " + match.Id);
            SelectAlbum(match.Id, fromRemote: true);
        }
    }

    void SyncRemoteAlbumOptionsToHa(DateTime now)
    {
        var albumEntityId = _config.HomeAssistant.RemoteAlbumEntityId;
        if (string.IsNullOrEmpty(albumEntityId) || !albumEntityId.StartsWith("input_select.", StringComparison.OrdinalIgnoreCase))
            return;
        var snap = _runtime.Take();
        if (snap.Phase != ConnPhase.Live || _albums.Count == 0)
            return;

        var opts = _albums
            .OrderByDescending(a => a.AssetCount)
            .Select(a => a.Name.Trim())
            .Where(n => !string.IsNullOrEmpty(n))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        opts.Add("本地相册");
        var sig = string.Join("\u001F", opts);
        if (string.Equals(sig, _lastRemoteOptionsSignature, StringComparison.Ordinal))
            return;
        if (_runtime.CallServiceData("input_select", "set_options", albumEntityId, new { options = opts }, now))
        {
            _lastRemoteOptionsSignature = sig;
            Log.Info($"remote album options synced ({opts.Count})");
        }
    }

    void SyncSelectedAlbumToHa(string label, DateTime now)
    {
        var albumEntityId = _config.HomeAssistant.RemoteAlbumEntityId;
        if (string.IsNullOrEmpty(albumEntityId))
            return;
        _lastRemoteAlbum = label;
        if (albumEntityId.StartsWith("input_select.", StringComparison.OrdinalIgnoreCase))
            _runtime.CallServiceData("input_select", "select_option", albumEntityId, new { option = label }, now);
        else if (albumEntityId.StartsWith("input_text.", StringComparison.OrdinalIgnoreCase))
            _runtime.CallServiceData("input_text", "set_value", albumEntityId, new { value = label }, now);
    }

    void SelectAlbum(string target, bool fromRemote = false)
    {
        CloseAlbumPicker();
        CancelLivePhoto();
        CancelSync();
        _pendingRandomFiles = null;
        Interlocked.Exchange(ref _immichGate, 0);

        if (target == "local")
        {
            _immichEnabled = false;
            _activeAlbumName = "本地相册";
            _liveMap.Clear();
            _metaMap.Clear();
            ConfigLoader.SaveImmichSelection(_config.PublicPath, null);
            UpdateHeaderAlbums();
            AlbumStatusPill.Visibility = Visibility.Collapsed;
            if (!_remoteLocked && !ShieldOn)
                ShowPage(Page.Photos);
            ReloadAlbum(PhotoDeck.List(_albumFolder), usingCache: false);
            if (!fromRemote)
                SyncSelectedAlbumToHa("本地相册", DateTime.UtcNow);
            Log.Info("album switched local");
            return;
        }

        if (!Guid.TryParse(target, out var guid))
            return;

        _immichEnabled = true;
        _activeAlbumId = guid.ToString("D");
        _cacheFolder = Path.Combine(_cacheRoot, _activeAlbumId);
        _liveCacheFolder = Path.Combine(_cacheFolder, "live");
        var found = _albums.FirstOrDefault(a => string.Equals(a.Id, _activeAlbumId, StringComparison.OrdinalIgnoreCase));
        _activeAlbumName = found?.Name ?? "Immich 相册";
        ConfigLoader.SaveImmichSelection(_config.PublicPath, _activeAlbumId);
        UpdateHeaderAlbums();
        LoadActiveLiveMap();

        if (!_remoteLocked && !ShieldOn)
            ShowPage(Page.Photos);
        var cached = ImmichSync.ListCache(_cacheFolder);
        if (cached.Count > 0)
            ReloadAlbum(cached, usingCache: true);
        TriggerImmichSync(DateTime.UtcNow, showPill: cached.Count == 0 && !_remoteLocked && !ShieldOn, isSwitch: true);
        if (!fromRemote && found != null)
            SyncSelectedAlbumToHa(found.Name, DateTime.UtcNow);
        Log.Info("album switched " + _activeAlbumId);
    }

    void PopulateAlbumList()
    {
        AlbumListPanel.Children.Clear();
        if (_albums.Count == 0 && _http != null && !_config.Demo.Enabled)
        {
            AlbumListPanel.Children.Add(new TextBlock
            {
                Text = "正在加载 Immich 相册列表…",
                FontSize = 15,
                Foreground = TextSecondary,
                Margin = new Thickness(4, 8, 4, 12)
            });
        }

        var ordered = _albums
            .OrderByDescending(a => _immichEnabled && string.Equals(a.Id, _activeAlbumId, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(a => a.AssetCount);
        foreach (var album in ordered)
        {
            var isSelected = _immichEnabled && string.Equals(album.Id, _activeAlbumId, StringComparison.OrdinalIgnoreCase);
            var countLabel = $"{album.AssetCount:N0} 张 · 全相册联网随机播放";
            AlbumListPanel.Children.Add(MakeAlbumRow(album.Name, countLabel, "album:pick:" + album.Id, isSelected));
        }

        AlbumListPanel.Children.Add(MakeAlbumRow(
            "本地相册 (Photos)",
            "离线本地目录",
            "album:pick:local",
            !_immichEnabled));
    }

    Border MakeAlbumRow(string title, string subtitle, string tag, bool selected)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var left = new StackPanel { Orientation = Orientation.Vertical, VerticalAlignment = VerticalAlignment.Center };
        left.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 17,
            FontWeight = FontWeights.SemiBold,
            Foreground = TextPrimary,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        left.Children.Add(new TextBlock
        {
            Text = subtitle,
            FontSize = 13,
            Foreground = selected ? BlueText : TextSecondary,
            Margin = new Thickness(0, 2, 0, 0)
        });
        grid.Children.Add(left);

        if (selected)
        {
            var check = new Border
            {
                Background = BlueSolid,
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(10, 4, 10, 4),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = "当前展示",
                    FontSize = 12,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = Brushes.White
                }
            };
            Grid.SetColumn(check, 1);
            grid.Children.Add(check);
        }

        return new Border
        {
            Tag = tag,
            Background = selected ? SwitchOnBg : SubCardBg,
            BorderBrush = selected ? SwitchOnBorder : CardBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(16, 10, 16, 10),
            Margin = new Thickness(0, 0, 0, 8),
            MinHeight = 56,
            Child = grid
        };
    }

    HeaderUi BuildHeader(Grid host, Page activePage)
    {
        host.Children.Clear();
        host.ColumnDefinitions.Clear();
        host.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        host.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        host.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // Left: Time + Date + HA status pill
        var left = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var timeTxt = new TextBlock
        {
            Text = DateTime.Now.ToString("HH:mm"),
            FontSize = 26,
            FontWeight = FontWeights.Bold,
            Foreground = TextPrimary,
            VerticalAlignment = VerticalAlignment.Center
        };
        var dateTxt = new TextBlock
        {
            Text = FormatDate(DateTime.Now),
            FontSize = 13,
            Foreground = TextSecondary,
            Margin = new Thickness(10, 4, 12, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        var statusTxt = new TextBlock
        {
            Text = "同步中",
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = TextSecondary
        };
        var statusPill = new Border
        {
            Background = CardBg,
            BorderBrush = CardBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(10, 4, 10, 4),
            VerticalAlignment = VerticalAlignment.Center,
            Child = statusTxt
        };
        left.Children.Add(timeTxt);
        left.Children.Add(dateTxt);
        left.Children.Add(statusPill);
        Grid.SetColumn(left, 0);
        host.Children.Add(left);

        // Center: Segmented pill nav
        var segStack = new StackPanel { Orientation = Orientation.Horizontal };
        segStack.Children.Add(MakeSegBtn("状态展示", "nav:info", activePage == Page.Info));
        segStack.Children.Add(MakeSegBtn("返回相册", "nav:photos", false));
        segStack.Children.Add(MakeSegBtn("家居控制", "nav:ha", activePage == Page.Ha));
        var segHost = new Border
        {
            Background = SegmentBg,
            CornerRadius = new CornerRadius(18),
            Padding = new Thickness(4),
            VerticalAlignment = VerticalAlignment.Center,
            Child = segStack
        };
        Grid.SetColumn(segHost, 1);
        host.Children.Add(segHost);

        // Right: Album Picker Pill
        var albumTxt = new TextBlock
        {
            Text = "相册 · 加载中",
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = TextPrimary,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 210,
            VerticalAlignment = VerticalAlignment.Center
        };
        var albumRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        albumRow.Children.Add(new TextBlock
        {
            Text = "\uEB9F",
            FontFamily = Mdl2,
            FontSize = 14,
            Foreground = BlueSolid,
            Margin = new Thickness(0, 0, 7, 0),
            VerticalAlignment = VerticalAlignment.Center
        });
        albumRow.Children.Add(albumTxt);
        albumRow.Children.Add(new TextBlock
        {
            Text = "\uE70D",
            FontFamily = Mdl2,
            FontSize = 10,
            Foreground = TextSecondary,
            Margin = new Thickness(7, 1, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        });
        var albumBtn = new Border
        {
            Tag = "album:open",
            Background = CardBg,
            BorderBrush = CardBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(16),
            Padding = new Thickness(14, 8, 14, 8),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Child = albumRow
        };
        Grid.SetColumn(albumBtn, 2);
        host.Children.Add(albumBtn);

        return new HeaderUi(timeTxt, dateTxt, statusPill, statusTxt, albumTxt);
    }

    Border MakeSegBtn(string label, string tag, bool active)
    {
        return new Border
        {
            Tag = tag,
            Background = active ? CardBg : Brushes.Transparent,
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(16, 7, 16, 7),
            Margin = new Thickness(1, 0, 1, 0),
            Child = new TextBlock
            {
                Text = label,
                FontSize = 14,
                FontWeight = active ? FontWeights.Bold : FontWeights.SemiBold,
                Foreground = active ? TextPrimary : TextSecondary
            }
        };
    }

    void UpdateClockText()
    {
        var now = DateTime.Now;
        var t = now.ToString("HH:mm");
        var d = FormatDate(now);
        _haHeader.Time.Text = t;
        _haHeader.Date.Text = d;
        _infoHeader.Time.Text = t;
        _infoHeader.Date.Text = d;
    }

    static string FormatDate(DateTime dt)
    {
        var dow = dt.DayOfWeek switch
        {
            DayOfWeek.Monday => "周一",
            DayOfWeek.Tuesday => "周二",
            DayOfWeek.Wednesday => "周三",
            DayOfWeek.Thursday => "周四",
            DayOfWeek.Friday => "周五",
            DayOfWeek.Saturday => "周六",
            _ => "周日"
        };
        return $"{dt.Month}月{dt.Day}日 {dow}";
    }

    void UpdateHeaderAlbums()
    {
        var label = _activeAlbumName;
        _haHeader.AlbumLabel.Text = label;
        _infoHeader.AlbumLabel.Text = label;
    }

    void RefreshHeaderStatus(HaSnapshot snap)
    {
        var (text, bg, fg) = _runtime.MissingToken && snap.Phase == ConnPhase.Idle
            ? ("缺少令牌", RoseSoftBg, RoseSolid)
            : snap.Phase switch
            {
                ConnPhase.Live => ("● 已连接", GreenSoftBg, GreenText),
                ConnPhase.Disconnected => ("● 已断开", RoseSoftBg, RoseSolid),
                _ => ("● 同步中", PillBg, TextSecondary)
            };

        foreach (var h in new[] { _haHeader, _infoHeader })
        {
            h.StatusText.Text = text;
            h.StatusText.Foreground = fg;
            h.StatusPill.Background = bg;
        }
    }

    void BuildHaControls()
    {
        BuildSceneBar();
        LightsGrid.Children.Clear();
        BottomControlRow.Children.Clear();
        BottomControlRow.ColumnDefinitions.Clear();
        _controlCards.Clear();

        var lights = _config.HomeAssistant.Entities.Where(e => e.Kind == EntityKind.Light).Take(12).ToList();
        var others = _config.HomeAssistant.Entities.Where(e => e.Kind != EntityKind.Light).Take(6).ToList();

        LightsGrid.Columns = lights.Count <= 6 ? Math.Max(1, lights.Count) : 4;
        foreach (var entity in lights)
            LightsGrid.Children.Add(BuildLightTile(entity));

        if (others.Count == 0)
            return;

        for (var i = 0; i < others.Count; i++)
        {
            BottomControlRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var tile = others[i].Kind == EntityKind.Cover
                ? BuildCoverTile(others[i])
                : others[i].Kind == EntityKind.Switch
                    ? BuildSwitchTile(others[i])
                    : BuildSensorTile(others[i]);
            Grid.SetColumn(tile, i);
            BottomControlRow.Children.Add(tile);
        }
    }

    void BuildSceneBar()
    {
        SceneBar.Children.Clear();
        _sceneCards.Clear();
        var scenes = _config.HomeAssistant.Scenes?.Take(5).ToList() ?? new List<SceneConfig>();
        if (scenes.Count == 0)
        {
            SceneBar.Visibility = Visibility.Collapsed;
            SceneBarRow.Height = new GridLength(0);
            return;
        }
        SceneBar.Visibility = Visibility.Visible;
        SceneBarRow.Height = new GridLength(76);
        SceneBar.Columns = scenes.Count;
        foreach (var sc in scenes)
            AddScenePill(sc);
    }

    static string MapSceneGlyph(string icon) => icon switch
    {
        "moon" => "\uE708",
        "power" => "\uE7E8",
        "sun" => "\uE706",
        "bath" or "cloud" => "\uE753",
        "heat" or "flame" => "\uE9CA",
        _ => "\uEA80"
    };

    void AddScenePill(SceneConfig config)
    {
        var iconTxt = new TextBlock
        {
            Text = MapSceneGlyph(config.Icon),
            FontFamily = Mdl2,
            FontSize = 17,
            Foreground = TextSecondary,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        var iconBox = new Border
        {
            Width = 36,
            Height = 36,
            CornerRadius = new CornerRadius(18),
            Background = PillBg,
            Margin = new Thickness(0, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = iconTxt
        };
        var subTxt = new TextBlock
        {
            Text = config.Subtitle,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = TextSecondary,
            Margin = new Thickness(0, 2, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        var titleTxt = new TextBlock
        {
            Text = config.Name,
            FontSize = 15,
            FontWeight = FontWeights.Bold,
            Foreground = TextPrimary,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        stack.Children.Add(titleTxt);
        stack.Children.Add(subTxt);

        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.Children.Add(iconBox);
        Grid.SetColumn(stack, 1);
        row.Children.Add(stack);

        var pill = MakeCardBorder("scene:" + config.Id, new Thickness(4, 0, 4, 0), new Thickness(12, 8, 12, 8), row);
        pill.CornerRadius = new CornerRadius(16);
        SceneBar.Children.Add(pill);
        _sceneCards.Add(new SceneCardUi(config, pill, iconBox, iconTxt, subTxt));
    }

    bool ExecuteScene(string sceneId, DateTime now)
    {
        var snap = _runtime.Take();
        if (!snap.ControlsEnabled)
            return false;
        var sc = _config.HomeAssistant.Scenes?.FirstOrDefault(s => string.Equals(s.Id, sceneId, StringComparison.Ordinal));
        if (sc == null || sc.EntityIds.Count == 0)
            return false;

        object target = sc.EntityIds.Count == 1 ? sc.EntityIds[0] : sc.EntityIds;
        var service = sc.Mode switch
        {
            "turn_off" => "turn_off",
            "trigger" or "turn_on" => "turn_on",
            _ => AnyOn(snap.AllStates, sc.EntityIds) ? "turn_off" : "turn_on"
        };
        var ok = _runtime.CallService(sc.Domain, service, target, now);
        if (ok)
            _sceneFlashes[sceneId] = now + TimeSpan.FromSeconds(2.5);
        return ok;
    }

    static bool IsOn(IReadOnlyDictionary<string, CardView> s, string entityId)
        => s.TryGetValue(entityId, out var v) && string.Equals(v.State, "on", StringComparison.OrdinalIgnoreCase);

    static bool AnyOn(IReadOnlyDictionary<string, CardView> s, IReadOnlyList<string> ids)
        => ids.Any(id => IsOn(s, id));

    Border BuildLightTile(EntityConfig entity)
    {
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var topRow = new Grid();
        var iconGlyph = new TextBlock
        {
            Text = "\uEA80",
            FontFamily = Mdl2,
            FontSize = 18,
            Foreground = TextSecondary,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        var iconCircle = new Border
        {
            Width = 38,
            Height = 38,
            CornerRadius = new CornerRadius(19),
            Background = PillBg,
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = iconGlyph
        };
        topRow.Children.Add(iconCircle);

        if (!string.IsNullOrEmpty(entity.Group))
        {
            var groupPill = new Border
            {
                Background = PillBg,
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(9, 3, 9, 3),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Child = new TextBlock
                {
                    Text = entity.Group,
                    FontSize = 12,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = TextSecondary
                }
            };
            topRow.Children.Add(groupPill);
        }

        var retryBtn = MakeSmallPill("重试", "retry:" + entity.EntityId, RoseSoftBg, RoseSolid);
        retryBtn.HorizontalAlignment = HorizontalAlignment.Right;
        retryBtn.VerticalAlignment = VerticalAlignment.Bottom;
        retryBtn.Visibility = Visibility.Collapsed;

        var bottomGrid = new Grid { VerticalAlignment = VerticalAlignment.Bottom };
        var labelStack = new StackPanel { Orientation = Orientation.Vertical, VerticalAlignment = VerticalAlignment.Bottom };
        labelStack.Children.Add(new TextBlock
        {
            Text = entity.Name,
            FontSize = 17,
            FontWeight = FontWeights.Bold,
            Foreground = TextPrimary,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        var status = new TextBlock
        {
            Text = "等待状态",
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = TextSecondary,
            Margin = new Thickness(0, 2, 0, 0)
        };
        labelStack.Children.Add(status);
        bottomGrid.Children.Add(labelStack);
        bottomGrid.Children.Add(retryBtn);
        Grid.SetRow(bottomGrid, 1);

        root.Children.Add(topRow);
        root.Children.Add(bottomGrid);

        var card = MakeCardBorder("toggle:" + entity.EntityId, new Thickness(4), new Thickness(14, 10, 14, 10), root);
        _controlCards.Add(new ControlCardUi(entity, entity.EntityId, EntityKind.Light, card, iconCircle, iconGlyph, status, null, null, retryBtn));
        return card;
    }

    Border BuildSwitchTile(EntityConfig entity)
    {
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var glyphCode = entity.EntityId.Contains("pc", StringComparison.OrdinalIgnoreCase)
            || entity.EntityId.Contains("computer", StringComparison.OrdinalIgnoreCase) ? "\uE7F4" : "\uE7E8";
        var iconGlyph = new TextBlock
        {
            Text = glyphCode,
            FontFamily = Mdl2,
            FontSize = 18,
            Foreground = TextSecondary,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        var iconCircle = new Border
        {
            Width = 38,
            Height = 38,
            CornerRadius = new CornerRadius(19),
            Background = PillBg,
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = iconGlyph
        };

        var retryBtn = MakeSmallPill("重试", "retry:" + entity.EntityId, RoseSoftBg, RoseSolid);
        retryBtn.HorizontalAlignment = HorizontalAlignment.Right;
        retryBtn.VerticalAlignment = VerticalAlignment.Bottom;
        retryBtn.Visibility = Visibility.Collapsed;

        var bottomGrid = new Grid { VerticalAlignment = VerticalAlignment.Bottom };
        var stack = new StackPanel { Orientation = Orientation.Vertical };
        stack.Children.Add(new TextBlock
        {
            Text = entity.Name,
            FontSize = 17,
            FontWeight = FontWeights.Bold,
            Foreground = TextPrimary,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        var status = new TextBlock
        {
            Text = "等待状态",
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = TextSecondary,
            Margin = new Thickness(0, 2, 0, 0)
        };
        stack.Children.Add(status);
        bottomGrid.Children.Add(stack);
        bottomGrid.Children.Add(retryBtn);
        Grid.SetRow(bottomGrid, 1);

        root.Children.Add(iconCircle);
        root.Children.Add(bottomGrid);

        var card = MakeCardBorder("toggle:" + entity.EntityId, new Thickness(4), new Thickness(14, 11, 14, 11), root);
        _controlCards.Add(new ControlCardUi(entity, entity.EntityId, EntityKind.Switch, card, iconCircle, iconGlyph, status, null, null, retryBtn));
        return card;
    }

    Border BuildCoverTile(EntityConfig entity)
    {
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var header = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top };
        var iconGlyph = new TextBlock
        {
            Text = "\uE74A",
            FontFamily = Mdl2,
            FontSize = 17,
            Foreground = TextSecondary,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        var iconCircle = new Border
        {
            Width = 38,
            Height = 38,
            CornerRadius = new CornerRadius(19),
            Background = PillBg,
            Margin = new Thickness(0, 0, 10, 0),
            Child = iconGlyph
        };
        var titleStack = new StackPanel { Orientation = Orientation.Vertical, VerticalAlignment = VerticalAlignment.Center };
        titleStack.Children.Add(new TextBlock
        {
            Text = entity.Name,
            FontSize = 16,
            FontWeight = FontWeights.Bold,
            Foreground = TextPrimary,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        var status = new TextBlock
        {
            Text = "等待状态",
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = TextSecondary,
            Margin = new Thickness(0, 2, 0, 0)
        };
        titleStack.Children.Add(status);
        header.Children.Add(iconCircle);
        header.Children.Add(titleStack);

        var btnRow = new Grid { VerticalAlignment = VerticalAlignment.Bottom };
        btnRow.ColumnDefinitions.Add(new ColumnDefinition());
        btnRow.ColumnDefinitions.Add(new ColumnDefinition());

        var openBtn = MakeActionPill("打开", "open:" + entity.EntityId, new Thickness(0, 0, 4, 0));
        var closeBtn = MakeActionPill("关闭", "close:" + entity.EntityId, new Thickness(4, 0, 0, 0));
        var retryBtn = MakeActionPill("重试", "retry:" + entity.EntityId, new Thickness(0));
        retryBtn.Background = RoseSoftBg;
        retryBtn.BorderBrush = RoseBorder;
        if (retryBtn.Child is TextBlock rt) rt.Foreground = RoseSolid;
        retryBtn.Visibility = Visibility.Collapsed;

        Grid.SetColumn(openBtn, 0);
        Grid.SetColumn(closeBtn, 1);
        Grid.SetColumnSpan(retryBtn, 2);
        btnRow.Children.Add(openBtn);
        btnRow.Children.Add(closeBtn);
        btnRow.Children.Add(retryBtn);
        Grid.SetRow(btnRow, 1);

        root.Children.Add(header);
        root.Children.Add(btnRow);

        var card = MakeCardBorder(null, new Thickness(4), new Thickness(14, 11, 14, 11), root);
        _controlCards.Add(new ControlCardUi(entity, entity.EntityId, EntityKind.Cover, card, iconCircle, iconGlyph, status, openBtn, closeBtn, retryBtn));
        return card;
    }

    Border BuildSensorTile(EntityConfig entity)
    {
        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        stack.Children.Add(new TextBlock { Text = entity.Name, FontSize = 16, FontWeight = FontWeights.Bold, Foreground = TextPrimary });
        var status = new TextBlock { Text = "等待状态", FontSize = 22, FontWeight = FontWeights.Bold, Foreground = BlueText, Margin = new Thickness(0, 6, 0, 0) };
        stack.Children.Add(status);
        var card = MakeCardBorder(null, new Thickness(4), new Thickness(14), stack);
        var dummyGlyph = new TextBlock();
        var dummyCircle = new Border();
        _controlCards.Add(new ControlCardUi(entity, entity.EntityId, EntityKind.Sensor, card, dummyCircle, dummyGlyph, status, null, null, null));
        return card;
    }

    static Border MakeCardBorder(string? tag, Thickness margin, Thickness padding, UIElement child)
    {
        return new Border
        {
            Tag = tag,
            Background = CardBg,
            BorderBrush = CardBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(18),
            Margin = margin,
            Padding = padding,
            Effect = new DropShadowEffect { BlurRadius = 14, ShadowDepth = 2, Opacity = 0.05, Color = Color.FromRgb(15, 23, 42) },
            Child = child
        };
    }

    static Border MakeSmallPill(string label, string tag, Brush bg, Brush fg)
    {
        return new Border
        {
            Tag = tag,
            Background = bg,
            BorderBrush = RoseBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(12, 6, 12, 6),
            MinHeight = 36,
            Child = new TextBlock
            {
                Text = label,
                FontSize = 13,
                FontWeight = FontWeights.Bold,
                Foreground = fg,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
    }

    static Border MakeActionPill(string label, string tag, Thickness margin)
    {
        return new Border
        {
            Tag = tag,
            Background = PillBg,
            BorderBrush = CardBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            MinHeight = 42,
            Margin = margin,
            Child = new TextBlock
            {
                Text = label,
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                Foreground = TextPrimary,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
    }

    void RefreshControls(HaSnapshot snap)
    {
        RefreshScenes(snap);

        foreach (var card in _controlCards)
        {
            if (!snap.AllStates.TryGetValue(card.Id, out var view))
                continue;

            if (card.Kind == EntityKind.Sensor)
            {
                card.Status.Text = view.Display;
                continue;
            }

            if (card.Kind == EntityKind.Cover)
            {
                RefreshCoverCard(card, view, snap);
                continue;
            }

            var isOn = string.Equals(view.State, "on", StringComparison.OrdinalIgnoreCase);
            var isUnconfirmed = view.Pending == PendingKind.Unconfirmed;
            var isPending = view.Pending is PendingKind.TurningOn or PendingKind.TurningOff;

            card.Status.Text = view.Pending switch
            {
                PendingKind.TurningOn => "正在开启…",
                PendingKind.TurningOff => "正在关闭…",
                PendingKind.Unconfirmed => "未确认",
                _ when isOn => "已开启",
                _ when string.Equals(view.State, "unavailable", StringComparison.OrdinalIgnoreCase) => "不可用",
                _ when string.Equals(view.State, "unknown", StringComparison.OrdinalIgnoreCase) => "未知",
                _ when string.Equals(view.State, "off", StringComparison.OrdinalIgnoreCase) => "已关闭",
                _ => "等待状态"
            };

            if (isUnconfirmed)
            {
                card.Card.Background = RoseSoftBg;
                card.Card.BorderBrush = RoseBorder;
                card.IconCircle.Background = RoseSolid;
                card.IconGlyph.Foreground = Brushes.White;
                card.Status.Foreground = RoseSolid;
            }
            else if (isOn)
            {
                var isLight = card.Kind == EntityKind.Light;
                card.Card.Background = isLight ? LightOnBg : SwitchOnBg;
                card.Card.BorderBrush = isLight ? LightOnBorder : SwitchOnBorder;
                card.IconCircle.Background = isLight ? AmberSolid : BlueSolid;
                card.IconGlyph.Foreground = Brushes.White;
                card.Status.Foreground = isLight ? AmberText : BlueText;
            }
            else
            {
                card.Card.Background = CardBg;
                card.Card.BorderBrush = CardBorder;
                card.IconCircle.Background = PillBg;
                card.IconGlyph.Foreground = TextSecondary;
                card.Status.Foreground = isPending ? AmberText : TextSecondary;
            }

            var canToggle = snap.ControlsEnabled && view.State is "on" or "off";
            var canTapCard = canToggle && view.Pending == PendingKind.None;
            card.Card.IsEnabled = canTapCard || isUnconfirmed;
            card.Card.Opacity = !snap.ControlsEnabled ? 0.65 : 1.0;
            if (card.RetryBtn != null)
            {
                card.RetryBtn.Visibility = isUnconfirmed ? Visibility.Visible : Visibility.Collapsed;
                SetEnabled(card.RetryBtn, canToggle && isUnconfirmed);
            }
        }
    }

    void RefreshCoverCard(ControlCardUi card, CardView view, HaSnapshot snap)
    {
        var ids = card.Entity.EntityIds is { Count: > 1 } multi ? multi : new[] { card.Id };
        var positions = new List<int>();
        var states = new List<string>();
        foreach (var id in ids)
        {
            if (!snap.AllStates.TryGetValue(id, out var sub))
                continue;
            states.Add(sub.State ?? "");
            if (int.TryParse(Attr(sub, "current_position"), out var p))
                positions.Add(p);
        }

        string statusText;
        if (view.Pending == PendingKind.TurningOn)
            statusText = "正在打开…";
        else if (view.Pending == PendingKind.TurningOff)
            statusText = "正在关闭…";
        else if (view.Pending == PendingKind.Unconfirmed)
            statusText = "未确认";
        else if (states.Any(s => s == "opening"))
            statusText = "正在打开";
        else if (states.Any(s => s == "closing"))
            statusText = "正在关闭";
        else if (positions.Count == 2)
            statusText = positions[0] == positions[1]
                ? (positions[0] <= 2 ? "已关闭" : $"已打开 · {positions[0]}%")
                : $"左 {positions[0]}% · 右 {positions[1]}%";
        else if (positions.Count == 1)
            statusText = positions[0] <= 2 ? "已关闭" : $"已打开 · {positions[0]}%";
        else
            statusText = view.State == "open" ? "已打开" : view.State == "closed" ? "已关闭" : view.Display;

        card.Status.Text = statusText;

        var isOpen = states.Any(s => s == "open") || positions.Any(p => p > 2);
        var unconfirmed = view.Pending == PendingKind.Unconfirmed;
        card.Card.Background = unconfirmed ? RoseSoftBg : isOpen ? SwitchOnBg : CardBg;
        card.Card.BorderBrush = unconfirmed ? RoseBorder : isOpen ? SwitchOnBorder : CardBorder;
        card.IconCircle.Background = isOpen ? BlueSolid : PillBg;
        card.IconGlyph.Foreground = isOpen ? Brushes.White : TextSecondary;
        card.Status.Foreground = unconfirmed ? RoseSolid : isOpen ? BlueText : TextSecondary;

        var canCmd = snap.ControlsEnabled && view.State is "open" or "closed" or "opening" or "closing";
        var ready = canCmd && view.Pending == PendingKind.None;
        var canOpen = ready && (positions.Count > 0 ? positions.Any(p => p < 98) : view.State != "open");
        var canClose = ready && (positions.Count > 0 ? positions.Any(p => p > 2) : view.State != "closed");

        if (card.OpenBtn != null && card.CloseBtn != null && card.RetryBtn != null)
        {
            card.OpenBtn.Visibility = unconfirmed ? Visibility.Collapsed : Visibility.Visible;
            card.CloseBtn.Visibility = unconfirmed ? Visibility.Collapsed : Visibility.Visible;
            card.RetryBtn.Visibility = unconfirmed ? Visibility.Visible : Visibility.Collapsed;
            SetEnabled(card.OpenBtn, canOpen);
            SetEnabled(card.CloseBtn, canClose);
            SetEnabled(card.RetryBtn, canCmd && unconfirmed);
        }
    }

    void RefreshScenes(HaSnapshot snap)
    {
        var now = DateTime.UtcNow;
        var s = snap.AllStates;

        foreach (var sc in _sceneCards)
        {
            var cfg = sc.Config;
            var flashed = _sceneFlashes.TryGetValue(cfg.Id, out var until) && now < until;
            if (!flashed && _sceneFlashes.ContainsKey(cfg.Id))
                _sceneFlashes.Remove(cfg.Id);

            var onCount = cfg.EntityIds.Count(id => IsOn(s, id));
            bool active;
            string subText;
            if (cfg.Mode == "turn_off")
            {
                active = flashed;
                subText = flashed ? "已发送全关" : onCount > 0 ? $"当前 {onCount} 项开启" : "全部已关";
            }
            else if (cfg.Mode is "trigger" or "turn_on")
            {
                active = onCount > 0 || flashed;
                subText = active ? "已触发" : cfg.Subtitle;
            }
            else
            {
                active = onCount > 0;
                subText = active ? "已开启" : cfg.Subtitle;
            }

            var warm = cfg.WarmAccent;
            sc.Sub.Text = subText;
            sc.Pill.IsEnabled = snap.ControlsEnabled;
            sc.Pill.Opacity = snap.ControlsEnabled ? 1.0 : 0.6;
            if (active)
            {
                sc.Pill.Background = warm ? LightOnBg : SwitchOnBg;
                sc.Pill.BorderBrush = warm ? LightOnBorder : SwitchOnBorder;
                sc.IconBox.Background = warm ? AmberSolid : BlueSolid;
                sc.Icon.Foreground = Brushes.White;
                sc.Sub.Foreground = warm ? AmberText : BlueText;
            }
            else
            {
                sc.Pill.Background = CardBg;
                sc.Pill.BorderBrush = CardBorder;
                sc.IconBox.Background = PillBg;
                sc.Icon.Foreground = TextSecondary;
                sc.Sub.Foreground = TextSecondary;
            }
        }
    }

    InfoUi BuildInfoBento()
    {
        var dash = _config.HomeAssistant.Dashboard;
        InfoBentoGrid.Children.Clear();
        InfoBentoGrid.RowDefinitions.Clear();
        InfoBentoGrid.ColumnDefinitions.Clear();

        InfoBentoGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(0.92, GridUnitType.Star) });
        InfoBentoGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1.18, GridUnitType.Star) });

        InfoBentoGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.12, GridUnitType.Star) });
        InfoBentoGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.0, GridUnitType.Star) });
        InfoBentoGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.96, GridUnitType.Star) });

        // Card (0,0): Weather & Indoor Rooms
        var weatherGrid = new Grid();
        weatherGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        weatherGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var weatherTop = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        var wLeft = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var wIcon = new TextBlock { Text = "\uE753", FontFamily = Mdl2, FontSize = 26, Foreground = AmberSolid, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
        var wText = new StackPanel();
        var wCond = new TextBlock { Text = "室外天气", FontSize = 17, FontWeight = FontWeights.Bold, Foreground = TextPrimary };
        var wHum = new TextBlock { Text = "湿度 --%", FontSize = 12, Foreground = TextSecondary, Margin = new Thickness(0, 2, 0, 0) };
        wText.Children.Add(wCond);
        wText.Children.Add(wHum);
        wLeft.Children.Add(wIcon);
        wLeft.Children.Add(wText);
        var wTemp = new TextBlock { Text = "--°C", FontSize = 32, FontWeight = FontWeights.Bold, Foreground = TextPrimary, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        weatherTop.Children.Add(wLeft);
        weatherTop.Children.Add(wTemp);

        var roomsGrid = new Grid();
        roomsGrid.RowDefinitions.Add(new RowDefinition());
        roomsGrid.RowDefinitions.Add(new RowDefinition());
        roomsGrid.ColumnDefinitions.Add(new ColumnDefinition());
        roomsGrid.ColumnDefinitions.Add(new ColumnDefinition());
        var roomSlots = new List<(RoomSensorConfig Config, TextBlock Value)>();
        var rooms = dash?.Rooms ?? Array.Empty<RoomSensorConfig>();
        for (var i = 0; i < Math.Min(4, rooms.Count); i++)
        {
            var tb = MakeMetricTile(rooms[i].Name, "--°C · --%", i / 2, i % 2, roomsGrid);
            roomSlots.Add((rooms[i], tb));
        }
        if (roomSlots.Count == 0)
        {
            MakeMetricTile("客厅", "--°C · --%", 0, 0, roomsGrid);
            MakeMetricTile("卧室", "--°C · --%", 0, 1, roomsGrid);
        }
        Grid.SetRow(roomsGrid, 1);

        weatherGrid.Children.Add(weatherTop);
        weatherGrid.Children.Add(roomsGrid);
        AddBentoCard(weatherGrid, 0, 0);

        // Card (0,1): Vehicle / EV Status
        var vehicleGrid = new Grid();
        vehicleGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        vehicleGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        vehicleGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var vehicleHdr = new Grid();
        vehicleHdr.Children.Add(new TextBlock { Text = dash?.Vehicle?.Title ?? "电动车状态", FontSize = 16, FontWeight = FontWeights.Bold, Foreground = TextPrimary, VerticalAlignment = VerticalAlignment.Center });
        var vehicleBadgeTxt = new TextBlock { Text = "离线", FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = TextSecondary };
        var vehicleBadge = new Border
        {
            Background = PillBg,
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(10, 3, 10, 3),
            HorizontalAlignment = HorizontalAlignment.Right,
            Child = vehicleBadgeTxt
        };
        vehicleHdr.Children.Add(vehicleBadge);

        var vehicleHero = new StackPanel { Margin = new Thickness(0, 8, 0, 8) };
        var vehicleTopNums = new Grid();
        var vehicleBatt = new TextBlock { Text = "--%", FontSize = 32, FontWeight = FontWeights.Bold, Foreground = TextPrimary };
        var vehicleRange = new TextBlock { Text = "续航 -- km", FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = TextSecondary, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 4) };
        vehicleTopNums.Children.Add(vehicleBatt);
        vehicleTopNums.Children.Add(vehicleRange);
        var vehicleBarFill = new Border { Background = GreenSolid, CornerRadius = new CornerRadius(4), HorizontalAlignment = HorizontalAlignment.Left, Width = 12 };
        var vehicleBarTrack = new Border
        {
            Height = 8,
            Background = PillBg,
            CornerRadius = new CornerRadius(4),
            Margin = new Thickness(0, 6, 0, 0),
            Child = vehicleBarFill
        };
        vehicleHero.Children.Add(vehicleTopNums);
        vehicleHero.Children.Add(vehicleBarTrack);
        Grid.SetRow(vehicleHero, 1);

        var vehicleSub = new Grid();
        vehicleSub.RowDefinitions.Add(new RowDefinition());
        vehicleSub.RowDefinitions.Add(new RowDefinition());
        vehicleSub.ColumnDefinitions.Add(new ColumnDefinition());
        vehicleSub.ColumnDefinitions.Add(new ColumnDefinition());
        var vehicleIn = MakeMetricTile("车内温度", "--°C", 0, 0, vehicleSub);
        var vehicleOut = MakeMetricTile("车外温度", "--°C", 0, 1, vehicleSub);
        var vehicleDoors = MakeMetricTile("车门状态", "--", 1, 0, vehicleSub);
        var vehicleSentry = MakeMetricTile("哨兵模式", "--", 1, 1, vehicleSub);
        Grid.SetRow(vehicleSub, 2);

        vehicleGrid.Children.Add(vehicleHdr);
        vehicleGrid.Children.Add(vehicleHero);
        vehicleGrid.Children.Add(vehicleSub);
        AddBentoCard(vehicleGrid, 0, 1);

        // Card (0,2): Heating & Climate
        var heatGrid = new Grid();
        heatGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        heatGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        heatGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var heatHdr = new Grid();
        heatHdr.Children.Add(new TextBlock { Text = dash?.Climate?.Title ?? "暖通与空调", FontSize = 16, FontWeight = FontWeights.Bold, Foreground = TextPrimary, VerticalAlignment = VerticalAlignment.Center });
        var heatBadgeTxt = new TextBlock { Text = "待机", FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = TextSecondary };
        var heatBadge = new Border { Background = PillBg, CornerRadius = new CornerRadius(10), Padding = new Thickness(10, 3, 10, 3), HorizontalAlignment = HorizontalAlignment.Right, Child = heatBadgeTxt };
        heatHdr.Children.Add(heatBadge);

        var heatObs = new TextBlock { Text = "采暖状态同步中…", FontSize = 12, Foreground = TextSecondary, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 4, 0, 8) };
        Grid.SetRow(heatObs, 1);

        var heatBody = new Grid();
        heatBody.RowDefinitions.Add(new RowDefinition());
        heatBody.RowDefinitions.Add(new RowDefinition());
        var waterStrip = new Grid();
        waterStrip.ColumnDefinitions.Add(new ColumnDefinition());
        waterStrip.ColumnDefinitions.Add(new ColumnDefinition());
        waterStrip.ColumnDefinitions.Add(new ColumnDefinition());
        var wSupply = MakeMetricTile("供水", "--°C", 0, 0, waterStrip);
        var wReturn = MakeMetricTile("回水", "--°C", 0, 1, waterStrip);
        var wTarget = MakeMetricTile("设定", "--°C", 0, 2, waterStrip);
        var acStrip = new Grid { Margin = new Thickness(0, 4, 0, 0) };
        acStrip.ColumnDefinitions.Add(new ColumnDefinition());
        acStrip.ColumnDefinitions.Add(new ColumnDefinition());
        var ac1 = MakeMetricTile(dash?.Climate?.Ac1Name ?? "客厅空调", "--", 0, 0, acStrip);
        var ac2 = MakeMetricTile(dash?.Climate?.Ac2Name ?? "卧室空调", "--", 0, 1, acStrip);
        Grid.SetRow(acStrip, 1);
        heatBody.Children.Add(waterStrip);
        heatBody.Children.Add(acStrip);
        Grid.SetRow(heatBody, 2);

        heatGrid.Children.Add(heatHdr);
        heatGrid.Children.Add(heatObs);
        heatGrid.Children.Add(heatBody);
        AddBentoCard(heatGrid, 0, 2);

        // Card (1,0 Span 2): Monthly Electricity & Gas Cost + Stacked Chart
        var cur = dash?.Energy?.CurrencySymbol ?? "¥";
        var utilityGrid = new Grid();
        utilityGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        utilityGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        utilityGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var uHdr = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        var uLeft = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        uLeft.Children.Add(new TextBlock { Text = dash?.Energy?.Title ?? "本月用能走势与费用", FontSize = 16, FontWeight = FontWeights.Bold, Foreground = TextPrimary, VerticalAlignment = VerticalAlignment.Center });
        var tierTxt = new TextBlock { Text = "阶梯计费", FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = TextSecondary };
        var tierBadge = new Border { Background = PillBg, CornerRadius = new CornerRadius(9), Padding = new Thickness(9, 3, 9, 3), Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Child = tierTxt };
        uLeft.Children.Add(tierBadge);
        uHdr.Children.Add(uLeft);

        var uRight = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        uRight.Children.Add(MakeLegendDot("电费", ElecBarBrush));
        uRight.Children.Add(MakeLegendDot("燃气", GasBarBrush));
        var powerNow = new TextBlock { Text = "实时 -- W", FontSize = 12, FontWeight = FontWeights.Bold, Foreground = BlueText };
        uRight.Children.Add(new Border
        {
            Background = BlueSoftBg,
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(10, 3, 10, 3),
            Margin = new Thickness(8, 0, 0, 0),
            Child = powerNow
        });
        uHdr.Children.Add(uRight);

        var kpiStrip = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        kpiStrip.ColumnDefinitions.Add(new ColumnDefinition());
        kpiStrip.ColumnDefinitions.Add(new ColumnDefinition());
        kpiStrip.ColumnDefinitions.Add(new ColumnDefinition());
        kpiStrip.ColumnDefinitions.Add(new ColumnDefinition());
        var totalCost = MakeMetricSubTile("本月合计", $"{cur} --", "电 + 气累计", 0, kpiStrip);
        var elecCost = MakeMetricSubTile("本月电费", $"{cur} --", $"上月 {cur} --", 1, kpiStrip);
        var gasCost = MakeMetricSubTile("本月燃气", $"{cur} --", "等待统计", 2, kpiStrip);
        var weekUsage = MakeMetricSubTile("近7天用量", "-- 度", "气 -- m³", 3, kpiStrip);
        Grid.SetRow(kpiStrip, 1);

        var chartCanvas = new Canvas { ClipToBounds = true, Background = CardBg };
        chartCanvas.SizeChanged += (_, _) => RenderUtilityChart();
        Grid.SetRow(chartCanvas, 2);

        utilityGrid.Children.Add(uHdr);
        utilityGrid.Children.Add(kpiStrip);
        utilityGrid.Children.Add(chartCanvas);
        AddBentoCard(utilityGrid, 1, 0, colSpan: 2);

        // Card (1,2): Appliances & Devices
        var devGrid = new Grid();
        devGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        devGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        devGrid.Children.Add(new TextBlock { Text = "家电与设备状态", FontSize = 16, FontWeight = FontWeights.Bold, Foreground = TextPrimary, Margin = new Thickness(0, 0, 0, 8) });
        var appRows = new Grid();
        var deviceRows = new List<(DeviceStatusConfig Config, TextBlock Value)>();
        var devs = dash?.Devices ?? Array.Empty<DeviceStatusConfig>();
        var rowCount = Math.Max(1, devs.Count);
        for (var r = 0; r < rowCount; r++)
            appRows.RowDefinitions.Add(new RowDefinition());
        if (devs.Count == 0)
        {
            MakeStatusRow("未配置状态设备", "在 frame.json 中配置", 0, appRows);
        }
        else
        {
            for (var r = 0; r < devs.Count; r++)
            {
                var rowVal = MakeStatusRow(devs[r].Name, "--", r, appRows);
                deviceRows.Add((devs[r], rowVal));
            }
        }
        Grid.SetRow(appRows, 1);
        devGrid.Children.Add(appRows);
        AddBentoCard(devGrid, 1, 2);

        return new InfoUi(
            wCond, wTemp, wHum, wIcon,
            roomSlots,
            vehicleBadge, vehicleBadgeTxt, vehicleBatt, vehicleRange, vehicleBarFill, vehicleIn, vehicleOut, vehicleDoors, vehicleSentry,
            heatBadge, heatBadgeTxt, heatObs, wSupply, wReturn, wTarget, ac1, ac2,
            powerNow, tierTxt, totalCost.Value, totalCost.Sub, elecCost.Value, elecCost.Sub, gasCost.Value, gasCost.Sub, weekUsage.Value, weekUsage.Sub, chartCanvas,
            deviceRows);
    }

    void AddBentoCard(UIElement child, int row, int col, int colSpan = 1)
    {
        var border = MakeCardBorder(null, new Thickness(5), new Thickness(16, 13, 16, 13), child);
        Grid.SetRow(border, row);
        Grid.SetColumn(border, col);
        if (colSpan > 1)
            Grid.SetColumnSpan(border, colSpan);
        InfoBentoGrid.Children.Add(border);
    }

    static FrameworkElement MakeLegendDot(string label, Brush brush)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        sp.Children.Add(new Border { Width = 9, Height = 9, CornerRadius = new CornerRadius(3), Background = brush, Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center });
        sp.Children.Add(new TextBlock { Text = label, FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = TextSecondary, VerticalAlignment = VerticalAlignment.Center });
        return sp;
    }

    static (TextBlock Value, TextBlock Sub) MakeMetricSubTile(string label, string initialValue, string initialSub, int col, Grid host)
    {
        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        stack.Children.Add(new TextBlock { Text = label, FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = TextSecondary });
        var val = new TextBlock { Text = initialValue, FontSize = 18, FontWeight = FontWeights.Bold, Foreground = TextPrimary, Margin = new Thickness(0, 2, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
        var sub = new TextBlock { Text = initialSub, FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = TextMuted, Margin = new Thickness(0, 1, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
        stack.Children.Add(val);
        stack.Children.Add(sub);
        var box = new Border
        {
            Background = SubCardBg,
            BorderBrush = SubCardBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(11, 7, 11, 7),
            Margin = new Thickness(3, 0, 3, 0),
            Child = stack
        };
        Grid.SetColumn(box, col);
        host.Children.Add(box);
        return (val, sub);
    }

    static TextBlock MakeMetricTile(string label, string initialValue, int row, int col, Grid host)
    {
        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        stack.Children.Add(new TextBlock { Text = label, FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = TextSecondary });
        var val = new TextBlock { Text = initialValue, FontSize = 16, FontWeight = FontWeights.Bold, Foreground = TextPrimary, Margin = new Thickness(0, 3, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
        stack.Children.Add(val);
        var box = new Border
        {
            Background = SubCardBg,
            BorderBrush = SubCardBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(11, 8, 11, 8),
            Margin = new Thickness(3),
            Child = stack
        };
        Grid.SetRow(box, row);
        Grid.SetColumn(box, col);
        host.Children.Add(box);
        return val;
    }

    static TextBlock MakeStatusRow(string label, string initialValue, int row, Grid host)
    {
        var g = new Grid { VerticalAlignment = VerticalAlignment.Center };
        g.Children.Add(new TextBlock { Text = label, FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = TextPrimary, VerticalAlignment = VerticalAlignment.Center });
        var val = new TextBlock { Text = initialValue, FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = TextSecondary, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        g.Children.Add(val);
        var box = new Border
        {
            Background = SubCardBg,
            CornerRadius = new CornerRadius(11),
            Padding = new Thickness(12, 7, 12, 7),
            Margin = new Thickness(0, 3, 0, 3),
            Child = g
        };
        Grid.SetRow(box, row);
        host.Children.Add(box);
        return val;
    }

    void RefreshInfoBento(HaSnapshot snap)
    {
        var dash = _config.HomeAssistant.Dashboard;
        if (dash == null)
            return;
        var s = snap.AllStates;

        // 1. Weather + Rooms
        if (!string.IsNullOrEmpty(dash.WeatherEntity) && s.TryGetValue(dash.WeatherEntity, out var w))
        {
            _infoUi.WeatherCond.Text = MapWeather(w.State);
            _infoUi.WeatherIcon.Text = w.State is "sunny" or "clear-night" ? "\uE706" : w.State is "rainy" or "pouring" ? "\uE9C1" : "\uE753";
            var wt = Attr(w, "temperature");
            var wh = Attr(w, "humidity");
            _infoUi.WeatherTemp.Text = wt != null ? $"{wt}°C" : "--°C";
            _infoUi.WeatherHum.Text = wh != null ? $"室外湿度 {wh}%" : "室外湿度 --%";
        }
        foreach (var (room, tb) in _infoUi.RoomSlots)
            tb.Text = FormatRoom(s, room.TempEntity, room.HumidityEntity);

        // 2. Vehicle
        if (dash.Vehicle is { } veh)
        {
            var battStr = ValOpt(s, veh.BatteryEntity);
            _infoUi.VehicleBatt.Text = battStr != null ? $"{battStr}%" : "--%";
            if (double.TryParse(battStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var pct))
            {
                var clamped = Math.Clamp(pct, 0, 100);
                _infoUi.VehicleBarFill.Width = Math.Max(12, 290.0 * clamped / 100.0);
                _infoUi.VehicleBarFill.Background = clamped <= 20 ? AmberSolid : GreenSolid;
            }
            var range = ValOpt(s, veh.RangeEntity);
            _infoUi.VehicleRange.Text = double.TryParse(range, NumberStyles.Float, CultureInfo.InvariantCulture, out var km)
                ? $"表显 {km:0} km" : "续航 -- km";

            var vState = ValOpt(s, veh.StateEntity);
            var plugged = ValOpt(s, veh.PluggedEntity) == "on";
            (_infoUi.VehicleBadgeText.Text, _infoUi.VehicleBadge.Background, _infoUi.VehicleBadgeText.Foreground) =
                plugged ? ("充电中", GreenSoftBg, GreenText)
                : vState == "online" ? ("在线", BlueSoftBg, BlueText)
                : vState == "driving" ? ("行车中", LightOnBg, AmberText)
                : ("休眠", PillBg, TextSecondary);

            _infoUi.VehicleIn.Text = FormatTemp(ValOpt(s, veh.InsideTempEntity));
            _infoUi.VehicleOut.Text = FormatTemp(ValOpt(s, veh.OutsideTempEntity));
            var doors = ValOpt(s, veh.DoorsEntity);
            _infoUi.VehicleDoors.Text = doors == null ? "--" : doors == "on" ? "未关" : "已锁闭";
            var sentry = ValOpt(s, veh.SentryEntity);
            _infoUi.VehicleSentry.Text = sentry == null ? "--" : sentry == "on" ? "开启" : "关闭";
        }

        // 3. Heating & AC
        if (dash.Climate is { } cl)
        {
            CardView? heaterView = null;
            if (!string.IsNullOrEmpty(cl.HeaterEntity))
                s.TryGetValue(cl.HeaterEntity, out heaterView);
            var hAction = Attr(heaterView, "hvac_action");
            var hRunning = hAction == "heating" || (heaterView != null && heaterView.State == "heat");
            _infoUi.HeatBadgeText.Text = hRunning ? "采暖中" : "待机";
            _infoUi.HeatBadge.Background = hRunning ? LightOnBg : PillBg;
            _infoUi.HeatBadgeText.Foreground = hRunning ? AmberText : TextSecondary;
            _infoUi.HeatObs.Text = ValOpt(s, cl.ObserverEntity) ?? "采暖状态就绪";
            _infoUi.WaterSupply.Text = FormatTemp(ValOpt(s, cl.SupplyTempEntity));
            _infoUi.WaterReturn.Text = FormatTemp(ValOpt(s, cl.ReturnTempEntity));
            _infoUi.WaterTarget.Text = FormatTemp(ValOpt(s, cl.TargetTempEntity));
            _infoUi.Ac1.Text = !string.IsNullOrEmpty(cl.Ac1Entity) ? FormatAc(s, cl.Ac1Entity) : "--";
            _infoUi.Ac2.Text = !string.IsNullOrEmpty(cl.Ac2Entity) ? FormatAc(s, cl.Ac2Entity) : "--";
        }

        // 4. Energy & Monthly Stacked Chart
        if (dash.Energy is { } en)
        {
            var cur = en.CurrencySymbol;
            var pwr = ValOpt(s, en.PowerEntity);
            _infoUi.PowerNow.Text = double.TryParse(pwr, NumberStyles.Float, CultureInfo.InvariantCulture, out var watts)
                ? $"实时 {watts:0} W" : "实时 -- W";

            CardView? elecView = null;
            CardView? gasView = null;
            if (!string.IsNullOrEmpty(en.ElectricityEntity)) s.TryGetValue(en.ElectricityEntity, out elecView);
            if (!string.IsNullOrEmpty(en.GasEntity)) s.TryGetValue(en.GasEntity, out gasView);

            var eMonth = ParseDouble(Attr(elecView, "month_cost") ?? elecView?.State);
            var gMonth = ParseDouble(Attr(gasView, "month_cost") ?? gasView?.State);
            var eLast = ParseDouble(Attr(elecView, "last_month_cost"));
            var gMissing = ParseDouble(Attr(gasView, "month_missing_days"));
            var eTier = Attr(elecView, "tier");
            var eRate = ParseDouble(Attr(elecView, "rate"));
            var gRate = ParseDouble(Attr(gasView, "rate"));

            _infoUi.TierText.Text = string.Format(
                CultureInfo.InvariantCulture,
                "{0} · 电 {1}{2} · 气 {1}{3}",
                eTier != null ? $"第{eTier}阶梯" : "阶梯计费",
                cur,
                eRate is double er ? $"{er:0.##}" : "--",
                gRate is double gr ? $"{gr:0.##}" : "--");

            if (eMonth is double || gMonth is double)
            {
                var tot = (eMonth ?? 0) + (gMonth ?? 0);
                _infoUi.TotalCost.Text = $"{cur} {tot:N2}";
                _infoUi.TotalSub.Text = "本月电 + 气累计";
            }
            else
            {
                _infoUi.TotalCost.Text = $"{cur} --";
                _infoUi.TotalSub.Text = "等待账单数据";
            }

            _infoUi.ElecCost.Text = eMonth is double em2 ? $"{cur} {em2:N2}" : $"{cur} --";
            _infoUi.ElecSub.Text = eLast is double el ? $"上月 {cur} {el:N2}" : "本月电费";
            _infoUi.GasCost.Text = gMonth is double gm2 ? $"{cur} {gm2:N2}" : $"{cur} --";
            _infoUi.GasSub.Text = gMissing is double md && md > 0 ? $"缺 {md:0} 天抄表" : "本月燃气";

            var summary = UtilityChartData.ParseMonth(
                Attr(elecView, "daily_cost"),
                Attr(gasView, "daily_cost"),
                Attr(elecView, "daily_usage"),
                Attr(gasView, "daily_usage"));
            _infoUi.WeekUsage.Text = $"{summary.Elec7dKwh:0.0} 度";
            _infoUi.WeekSub.Text = $"近7天燃气 {summary.Gas7dM3:0.0} m³";
            _lastUtilitySummary = summary;
            RenderUtilityChart();
        }

        // 5. Appliances & Devices
        foreach (var (dev, rowVal) in _infoUi.DeviceRows)
            rowVal.Text = FormatDeviceStatus(s, dev);
    }

    static string FormatDeviceStatus(IReadOnlyDictionary<string, CardView> s, DeviceStatusConfig dev)
    {
        var primary = Val(s, dev.EntityId);
        var extra = ValOpt(s, dev.ExtraEntityId);
        return dev.Kind switch
        {
            "vacuum" => MapVacuum(primary),
            "dishwasher" => primary != "on"
                ? "已关闭"
                : (extra != null && extra != "0" ? $"运行中 · {extra} min" : "已开启"),
            "media" => primary is "on" or "playing" or "paused" ? "运行中" : "待机",
            "ups" => primary != null
                ? $"{primary}% · {(extra != null && extra.Contains("Online", StringComparison.OrdinalIgnoreCase) ? "市电在线" : extra ?? "--")}"
                : "--",
            "binary" => extra != null
                ? $"{(primary == "on" ? "在线" : "已关机")} · {(dev.ExtraLabel ?? "")} {extra}".Trim()
                : (primary == "on" ? "在线" : primary == "off" ? "已关机" : "--"),
            _ => primary == "sleep" ? "休眠" : primary == "idle" ? "就绪" : (primary ?? "--")
        };
    }

    static string? ValOpt(IReadOnlyDictionary<string, CardView> s, string? id)
        => string.IsNullOrEmpty(id) ? null : Val(s, id);

    void RenderUtilityChart()
    {
        if (_infoUi == null)
            return;
        var canvas = _infoUi.ChartCanvas;
        canvas.Children.Clear();
        var w = canvas.ActualWidth;
        var h = canvas.ActualHeight;
        if (w < 80 || h < 60)
            return;

        var summary = _lastUtilitySummary;
        if (summary == null || summary.Days.Count == 0)
        {
            var empty = new TextBlock
            {
                Text = "等待本月每日用能数据…",
                FontSize = 13,
                Foreground = TextMuted
            };
            Canvas.SetLeft(empty, (w - 140) / 2);
            Canvas.SetTop(empty, (h - 20) / 2);
            canvas.Children.Add(empty);
            return;
        }

        const double padLeft = 36;
        const double padRight = 8;
        const double padTop = 14;
        const double padBottom = 22;
        var plotW = Math.Max(40, w - padLeft - padRight);
        var plotH = Math.Max(24, h - padTop - padBottom);
        var maxVal = Math.Max(4.0, Math.Ceiling(summary.PeakTotalCost));

        for (var i = 0; i <= 2; i++)
        {
            var ratio = i / 2.0;
            var y = padTop + plotH * (1 - ratio);
            canvas.Children.Add(new System.Windows.Shapes.Line
            {
                X1 = padLeft,
                Y1 = y,
                X2 = padLeft + plotW,
                Y2 = y,
                Stroke = GridLineStroke,
                StrokeThickness = 1
            });
            var tickVal = maxVal * ratio;
            var lbl = new TextBlock
            {
                Text = i == 0 ? "¥0" : $"¥{tickVal:0.#}",
                FontSize = 10,
                Foreground = TextMuted,
                Width = padLeft - 6,
                TextAlignment = TextAlignment.Right
            };
            Canvas.SetLeft(lbl, 0);
            Canvas.SetTop(lbl, y - 7);
            canvas.Children.Add(lbl);
        }

        var n = summary.Days.Count;
        var slotW = plotW / n;
        var barW = Math.Clamp(slotW * 0.62, 6, 22);
        var labelStep = n > 18 ? 3 : n > 10 ? 2 : 1;

        for (var i = 0; i < n; i++)
        {
            var d = summary.Days[i];
            var xCenter = padLeft + slotW * (i + 0.5);
            var barLeft = xCenter - barW / 2;
            var eHeight = (d.ElecCost / maxVal) * plotH;
            var gHeight = (d.GasCost / maxVal) * plotH;
            var yBottom = padTop + plotH;

            if (eHeight > 0.5)
            {
                var hRect = Math.Max(2, eHeight);
                var rect = new System.Windows.Shapes.Rectangle
                {
                    Width = barW,
                    Height = hRect,
                    Fill = ElecBarBrush,
                    RadiusX = 3,
                    RadiusY = 3
                };
                Canvas.SetLeft(rect, barLeft);
                Canvas.SetTop(rect, yBottom - hRect);
                canvas.Children.Add(rect);
                yBottom -= hRect + (gHeight > 0.5 ? 1.5 : 0);
            }

            if (gHeight > 0.5)
            {
                var hRect = Math.Max(2, gHeight);
                var rect = new System.Windows.Shapes.Rectangle
                {
                    Width = barW,
                    Height = hRect,
                    Fill = GasBarBrush,
                    RadiusX = 3,
                    RadiusY = 3
                };
                Canvas.SetLeft(rect, barLeft);
                Canvas.SetTop(rect, yBottom - hRect);
                canvas.Children.Add(rect);
            }

            if (i % labelStep == 0 || i == n - 1)
            {
                var dayTxt = new TextBlock
                {
                    Text = d.DayLabel,
                    FontSize = 10,
                    FontWeight = i == n - 1 ? FontWeights.Bold : FontWeights.Normal,
                    Foreground = i == n - 1 ? TextPrimary : TextMuted,
                    Width = Math.Max(18, slotW),
                    TextAlignment = TextAlignment.Center
                };
                Canvas.SetLeft(dayTxt, xCenter - dayTxt.Width / 2);
                Canvas.SetTop(dayTxt, padTop + plotH + 4);
                canvas.Children.Add(dayTxt);
            }
        }
    }

    static double? ParseDouble(string? s)
        => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;

    static string? Val(IReadOnlyDictionary<string, CardView> s, string id)
        => s.TryGetValue(id, out var v) && v.State is not ("unknown" or "unavailable" or "") ? v.State : null;

    static string? Attr(CardView? v, string key)
        => v?.Attributes != null && v.Attributes.TryGetValue(key, out var val) ? val : null;

    static string FormatRoom(IReadOnlyDictionary<string, CardView> s, string? tempId, string? humId)
    {
        var t = ValOpt(s, tempId);
        var h = ValOpt(s, humId);
        return $"{(t != null ? t + "°C" : "--")} · {(h != null ? h + "%" : "--")}";
    }

    static string FormatTemp(string? t) => t != null ? $"{t}°C" : "--°C";

    static string FormatMoney(string? m)
        => double.TryParse(m, NumberStyles.Float, CultureInfo.InvariantCulture, out var val) ? $"¥ {val:N2}" : "¥ --";

    static string FormatAc(IReadOnlyDictionary<string, CardView> s, string id)
    {
        if (!s.TryGetValue(id, out var v))
            return "--";
        var cur = Attr(v, "current_temperature");
        var st = v.State == "off" ? "关闭" : v.State == "heat" ? "制热" : v.State == "cool" ? "制冷" : v.State ?? "--";
        return cur != null ? $"{st} · {cur}°C" : st;
    }

    static string MapWeather(string? state) => state switch
    {
        "sunny" => "晴朗",
        "clear-night" => "晴夜",
        "partlycloudy" => "多云",
        "cloudy" => "阴天",
        "rainy" => "小雨",
        "pouring" => "大雨",
        "snowy" => "降雪",
        "fog" => "大雾",
        "lightning-rainy" => "雷阵雨",
        _ => state ?? "室外"
    };

    static string MapVacuum(string? state) => state switch
    {
        "docked" => "已回充就绪",
        "cleaning" => "清扫中",
        "returning" => "返回基站中",
        "paused" => "已暂停",
        "error" => "异常告警",
        _ => state ?? "--"
    };

    void BuildDemo()
    {
        DemoBar.Children.Clear();
        if (!_config.Demo.Enabled)
        {
            DemoHost.Visibility = Visibility.Collapsed;
            return;
        }
        AddDemo("在家", "demo:home");
        AddDemo("离家", "demo:away");
        AddDemo("断开", "demo:drop");
        AddDemo("恢复", "demo:resume");
        AddDemo("快速", "demo:quick");
        AddDemo("慢速", "demo:slow");
    }

    void AddDemo(string label, string tag)
        => DemoBar.Children.Add(new Border
        {
            Tag = tag,
            Background = CardBg,
            BorderBrush = CardBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            MinHeight = 46,
            MinWidth = 76,
            Margin = new Thickness(4, 0, 4, 0),
            Padding = new Thickness(12, 6, 12, 6),
            Child = new TextBlock
            {
                Text = label,
                FontSize = 15,
                FontWeight = FontWeights.SemiBold,
                Foreground = TextPrimary,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        });

    static void SetEnabled(Border button, bool enabled)
    {
        button.IsEnabled = enabled;
        button.Opacity = enabled ? 1 : 0.4;
    }

    static SolidColorBrush Freeze(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    sealed record HeaderUi(TextBlock Time, TextBlock Date, Border StatusPill, TextBlock StatusText, TextBlock AlbumLabel);

    sealed record SceneCardUi(
        SceneConfig Config,
        Border Pill,
        Border IconBox,
        TextBlock Icon,
        TextBlock Sub);

    sealed record ControlCardUi(
        EntityConfig Entity,
        string Id,
        EntityKind Kind,
        Border Card,
        Border IconCircle,
        TextBlock IconGlyph,
        TextBlock Status,
        Border? OpenBtn,
        Border? CloseBtn,
        Border? RetryBtn);

    sealed record InfoUi(
        TextBlock WeatherCond,
        TextBlock WeatherTemp,
        TextBlock WeatherHum,
        TextBlock WeatherIcon,
        IReadOnlyList<(RoomSensorConfig Config, TextBlock Value)> RoomSlots,
        Border VehicleBadge,
        TextBlock VehicleBadgeText,
        TextBlock VehicleBatt,
        TextBlock VehicleRange,
        Border VehicleBarFill,
        TextBlock VehicleIn,
        TextBlock VehicleOut,
        TextBlock VehicleDoors,
        TextBlock VehicleSentry,
        Border HeatBadge,
        TextBlock HeatBadgeText,
        TextBlock HeatObs,
        TextBlock WaterSupply,
        TextBlock WaterReturn,
        TextBlock WaterTarget,
        TextBlock Ac1,
        TextBlock Ac2,
        TextBlock PowerNow,
        TextBlock TierText,
        TextBlock TotalCost,
        TextBlock TotalSub,
        TextBlock ElecCost,
        TextBlock ElecSub,
        TextBlock GasCost,
        TextBlock GasSub,
        TextBlock WeekUsage,
        TextBlock WeekSub,
        Canvas ChartCanvas,
        IReadOnlyList<(DeviceStatusConfig Config, TextBlock Value)> DeviceRows);
}
