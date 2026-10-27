using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Win32;
using Stillbar.App.Services;
using Stillbar.Core.Display;
using Stillbar.Core.Settings;

namespace Stillbar.App.Shell;

public partial class BarWindow : Window
{
    private readonly DispatcherTimer _topmost;
    private readonly AppBar _appBar = new();
    private readonly Action<AppSettings> _save;
    private AppSettings _settings;

    public BarWindow(BarView view) : this(view, new AppSettings(), _ => { }) { }

    public BarWindow(BarView view, AppSettings settings, Action<AppSettings> save)
    {
        InitializeComponent();
        Content = view;
        View = view;
        _settings = settings;
        _save = save;

        _topmost = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _topmost.Tick += (_, _) => NativeWindow.KeepTopmost(Handle);

        SourceInitialized += (_, _) =>
        {
            NativeWindow.MakeNoActivateToolWindow(Handle);
            ApplyLook();
            Place();
            RegisterCommandHotkey();
            _topmost.Start();
        };
        // Moving to a monitor with another scale makes WPF resize us around its own anchor; centre again after.
        DpiChanged += (_, _) => Dispatcher.InvokeAsync(Place, DispatcherPriority.Background);
        SystemEvents.DisplaySettingsChanged += OnDisplayChanged;
        SystemParameters.StaticPropertyChanged += (_, e) =>
        {
            // The taskbar moved or resized. Our own reservation also changes the work area: that one is ignored.
            if (e.PropertyName == nameof(SystemParameters.WorkArea) && !_settings.ReserveStrip) Place();
        };
        _appBar.Moved += (_, _) => Place();
        Closed += (_, _) =>
        {
            SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
            _appBar.Dispose();
        };

        view.ContextMenu = new ContextMenu();
        view.ContextMenuOpening += (_, _) => FillMenu(view.ContextMenu);
        view.SlideStarted += (_, _) => _slideBase = NativeWindow.Left(Handle);
        view.Sliding += (_, dx) => SlideTo(_slideBase + dx);
        view.SlideEnded += (_, _) =>
        {
            var m = Placement.Pick(Monitors.All(), _settings.Monitor);
            Update(_settings with { Offset = Placement.OffsetFor(m, Width, NativeWindow.Left(Handle)) });
        };
        view.TypingStarted += (_, _) => BeginTyping();
        view.TypingEnded += (_, _) => EndTyping();
        Deactivated += (_, _) => EndTyping(); // the user clicked elsewhere: nothing to hand back
    }

    private nint _returnFocusTo;
    private bool _typing;

    /// <summary>Let a text field in the panel take the keyboard; remember whose it was.</summary>
    public void BeginTyping()
    {
        if (_typing) return;
        _typing = true;
        _returnFocusTo = NativeWindow.Foreground();
        NativeWindow.AllowActivation(Handle, true);
        NativeWindow.Focus(Handle);
        Activate();
    }

    /// <summary>Back to never taking focus, and give the keyboard back to the window that had it.</summary>
    public void EndTyping()
    {
        if (!_typing) return;
        _typing = false;
        System.Windows.Input.Keyboard.ClearFocus();
        NativeWindow.AllowActivation(Handle, false);
        if (IsActive && _returnFocusTo != Handle) NativeWindow.Focus(_returnFocusTo);
        _returnFocusTo = 0;
    }

    public BarView View { get; }

    // The see-through host at 100 %; the scale setting multiplies it (BarWindow.xaml has the same numbers).
    private const double BaseWidth = 760, BaseHeight = 640;

    public AppSettings Settings => _settings;

    /// <summary>Settings changed from the menu or the settings window (the app updates weather, the full-screen check).</summary>
    public event EventHandler<AppSettings>? SettingsChanged;

    /// <summary>«Настройки…» in the menu or the gear on the panel.</summary>
    public event EventHandler? SettingsRequested;

    /// <summary>Saves and applies at once: size, auto-hide, click-through, edge, monitor, reserved strip.</summary>
    public void Apply(AppSettings next) => Update(next);

    /// <summary>Scale, auto-hide and click-through: the parts of the look that do not need the window moved.</summary>
    private void ApplyLook()
    {
        double s = Math.Clamp(_settings.Scale, 80, 200) / 100.0;
        View.LayoutTransform = Math.Abs(s - 1) < 0.001 ? System.Windows.Media.Transform.Identity : new System.Windows.Media.ScaleTransform(s, s);
        Width = BaseWidth * s;
        Height = BaseHeight * s;
        View.AutoHide = _settings.AutoHide;
        View.MaxPanelHeight = BaseHeight - 12; // in the view's own (unscaled) units
        UpdateClickThrough();
    }

    // ---- click-through: clicks go to the window below; holding Ctrl over the bar makes it usable for a moment

    private DispatcherTimer? _clickThroughTimer;
    private bool _transparent;

    private void UpdateClickThrough()
    {
        if (Handle == 0) return;
        if (!_settings.ClickThrough)
        {
            _clickThroughTimer?.Stop();
            SetTransparent(false);
            return;
        }
        _clickThroughTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(120), DispatcherPriority.Background, (_, _) =>
            SetTransparent(!(View.IsExpanded || (NativeWindow.CtrlDown() && NativeWindow.CursorIn(Handle)))), Dispatcher);
        _clickThroughTimer.Start();
        SetTransparent(true);
    }

    private void SetTransparent(bool on)
    {
        if (on == _transparent) return;
        _transparent = on;
        NativeWindow.SetTransparent(Handle, on);
    }

    /// <summary>Alt+Space (or Ctrl+Alt+Space if another app holds it) was pressed anywhere.</summary>
    public event EventHandler? CommandHotkey;

    /// <summary>Which shortcut opens the command bar, for tooltips; empty if none could be registered.</summary>
    public string CommandHotkeyText { get; private set; } = "";

    private void RegisterCommandHotkey()
    {
        const int HotkeyId = 0x5B01, WM_HOTKEY = 0x0312;
        if (NativeWindow.RegisterHotkey(Handle, HotkeyId, alt: true, ctrl: false)) CommandHotkeyText = "Alt+Пробел";
        else if (NativeWindow.RegisterHotkey(Handle, HotkeyId, alt: true, ctrl: true)) CommandHotkeyText = "Ctrl+Alt+Пробел";
        Log.Info("hotkey", CommandHotkeyText.Length > 0 ? $"command bar on {CommandHotkeyText}" : "no free shortcut for the command bar");
        HwndSource.FromHwnd(Handle)?.AddHook((nint hwnd, int msg, nint wParam, nint lParam, ref bool handled) =>
        {
            if (msg == WM_HOTKEY && (int)wParam == HotkeyId)
            {
                CommandHotkey?.Invoke(this, EventArgs.Empty);
                handled = true;
            }
            return 0;
        });
    }

    /// <summary>The monitor the bar is on, for windows that open next to it (the command bar).</summary>
    public MonitorArea CurrentMonitor => Placement.Pick(Monitors.All(), _settings.Monitor);

    /// <summary>Device name of the monitor the bar is on; the full-screen check looks only there.</summary>
    public string? MonitorName { get; private set; }

    /// <summary>That monitor's rectangle in physical pixels (colour picker, screen recording).</summary>
    public PixelRect MonitorBounds { get; private set; }

    private nint Handle => new WindowInteropHelper(this).Handle;

    private BarEdge Edge => _settings.Edge == "bottom" ? BarEdge.Bottom : BarEdge.Top;

    private void OnDisplayChanged(object? sender, EventArgs e) => Dispatcher.InvokeAsync(Place);

    /// <summary>Chosen monitor (or the primary one), chosen edge, centred; reserve the strip first if asked to.</summary>
    private void Place()
    {
        if (Handle == 0) return;
        var monitors = Monitors.All();
        if (monitors.Count == 0) return;
        var m = Placement.Pick(monitors, _settings.Monitor);
        MonitorName = m.Name;
        MonitorBounds = m.Bounds;

        PixelRect? reserved = null;
        if (_settings.ReserveStrip)
            reserved = _appBar.Reserve(Placement.Strip(m, Edge, (double)FindResource("Layout.PillHeight") * Math.Clamp(_settings.Scale, 80, 200) / 100.0), Edge);
        else
            _appBar.Release();

        View.Edge = Edge;
        var (x, y) = Placement.Window(m, Edge, Width, Height, reserved, _settings.Offset);
        NativeWindow.MoveTo(Handle, x, y);
    }

    private int _slideBase;

    /// <summary>Follows the mouse while the bar is dragged, kept so the open panel stays on the monitor.</summary>
    private void SlideTo(int x)
    {
        var m = Placement.Pick(Monitors.All(), _settings.Monitor);
        int w = (int)Math.Round(Width * m.Scale);
        NativeWindow.MoveTo(Handle, Math.Clamp(x, m.Bounds.Left, Math.Max(m.Bounds.Left, m.Bounds.Right - w)), NativeWindow.Top(Handle));
    }

    private void Update(AppSettings next)
    {
        _settings = next;
        _save(next);
        ApplyLook();
        Place();
        SettingsChanged?.Invoke(this, next);
    }

    private void FillMenu(ContextMenu menu)
    {
        menu.Items.Clear();

        var strip = new MenuItem
        {
            Header = "Не закрывать окна",
            ToolTip = "Окна и вкладки браузера будут начинаться под полоской",
            IsCheckable = true,
            IsChecked = _settings.ReserveStrip,
        };
        strip.Click += (_, _) => Update(_settings with { ReserveStrip = strip.IsChecked });
        menu.Items.Add(strip);

        var edge = new MenuItem { Header = "Край экрана" };
        foreach (var (key, label) in new[] { ("top", "Сверху"), ("bottom", "Снизу") })
        {
            var item = new MenuItem { Header = label, IsCheckable = true, IsChecked = _settings.Edge == key };
            item.Click += (_, _) => Update(_settings with { Edge = key });
            edge.Items.Add(item);
        }
        menu.Items.Add(edge);

        var monitors = Monitors.All();
        if (monitors.Count > 1)
        {
            var screen = new MenuItem { Header = "Монитор" };
            for (int i = 0; i < monitors.Count; i++)
            {
                var m = monitors[i];
                var item = new MenuItem
                {
                    Header = $"Монитор {i + 1}{(m.IsPrimary ? " (основной)" : "")} · {m.Width}×{m.Height}",
                    IsCheckable = true,
                    IsChecked = m.Name == MonitorName,
                };
                item.Click += (_, _) => Update(_settings with { Monitor = m.IsPrimary ? null : m.Name });
                screen.Items.Add(item);
            }
            menu.Items.Add(screen);
        }

        menu.Items.Add(new Separator());
        if (_settings.Offset != 0)
        {
            var centre = new MenuItem { Header = "Вернуть в центр" };
            centre.Click += (_, _) => Update(_settings with { Offset = 0 });
            menu.Items.Add(centre);
        }
        var settings = new MenuItem { Header = "Настройки…" };
        settings.Click += (_, _) => SettingsRequested?.Invoke(this, EventArgs.Empty);
        menu.Items.Add(settings);
        var gallery = new MenuItem { Header = "Галерея дизайна" };
        gallery.Click += (_, _) => new Design.DesignGallery().Show();
        menu.Items.Add(gallery);
        var log = new MenuItem { Header = "Открыть журнал" };
        log.Click += (_, _) => Log.OpenFolder();
        menu.Items.Add(log);
        menu.Items.Add(new Separator());
        var quit = new MenuItem { Header = "Выход" };
        quit.Click += (_, _) => Application.Current.Shutdown();
        menu.Items.Add(quit);
    }
}
