using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using Stillbar.App.Services;
using Stillbar.Core.Display;
using Stillbar.Core.Motion;
using Stillbar.Core.Pill;

namespace Stillbar.App.Shell;

/// <summary>
/// The black shape and its two faces (pill, panel). Owns the hover logic, the spring morph between them,
/// the liquid bottom edge and the pill's equaliser.
/// </summary>
public partial class BarView : UserControl
{
    private const double WaveLength = 46;
    private const double WaveSpeed = 0.9;     // cycles per second
    private const double WaveFrame = 1.0 / 30; // the wave alone does not need 60 fps

    private readonly DispatcherTimer _expandTimer;
    private readonly DispatcherTimer _collapseTimer;
    private readonly double _stiffness;
    private readonly double _damping;
    private readonly double _panelRadius;
    private readonly double _panelWave;  // depth of the troughs while open
    private readonly double _pillWave;   // a still, shallow wave on the collapsed pill: the look without the per-frame cost
    private Spring _width;
    private Spring _height;
    private Spring _amplitude = new(0);
    private double _phase;
    private double _sinceWaveFrame;
    private bool _animating;
    private TimeSpan _lastFrame;
    private bool _eqRunning;

    public BarView() : this(expandDelayMs: 120, collapseDelayMs: 350) { }

    public BarView(int expandDelayMs, int collapseDelayMs)
    {
        InitializeComponent();
        _stiffness = (double)FindResource("Motion.SpringStiffness");
        _damping = (double)FindResource("Motion.SpringDamping");
        _panelRadius = (double)FindResource("Radius.Panel");
        _panelWave = (double)FindResource("Motion.PanelWave");
        _pillWave = (double)FindResource("Motion.PillWave");
        _width = new Spring(Bar.Width);
        _height = new Spring(Bar.Height);
        _amplitude = new Spring(_pillWave);
        _phase = Math.PI / 2;
        ApplyShape();

        _expandTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(expandDelayMs) };
        _expandTimer.Tick += (_, _) => { _expandTimer.Stop(); SetExpanded(true); };
        _collapseTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(collapseDelayMs) };
        _collapseTimer.Tick += (_, _) => { _collapseTimer.Stop(); SetExpanded(false); };

        Bar.MouseEnter += (_, _) =>
        {
            _collapseTimer.Stop();
            if (IsHiddenLine) { _peek = true; Retarget(); }
            // The finished-timer pill has its own buttons; opening the panel over them would make them unreachable.
            if (!IsExpanded && Vm is { IsAlertPill: false, IsListeningPill: false }) _expandTimer.Start();
        };
        Bar.MouseLeave += (_, _) =>
        {
            _expandTimer.Stop();
            // While typing (search, a note) the panel stays open until Esc, a paste or a click elsewhere.
            if (IsExpanded && !_dragging && !IsKeyboardFocusWithin) _collapseTimer.Start();
            if (_peek && !IsExpanded) { _peek = false; Retarget(); }
        };

        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is BarViewModel o)
            {
                o.PropertyChanged -= OnViewModelChanged;
                o.CollapseRequested -= OnCollapseRequested;
                o.ReturnFocusRequested -= OnReturnFocusRequested;
                o.ExpandRequested -= OnExpandRequested;
            }
            if (e.NewValue is BarViewModel n)
            {
                n.PropertyChanged += OnViewModelChanged;
                n.CollapseRequested += OnCollapseRequested;
                n.ReturnFocusRequested += OnReturnFocusRequested;
                n.ExpandRequested += OnExpandRequested;
            }
            UpdateEqualiser();
        };
        IsVisibleChanged += (_, _) => UpdateEqualiser();

        // Seeking: stop following the player on press, seek on release. The release is caught even when the
        // thumb has captured the mouse (handledEventsToo), and a lost capture also ends the drag.
        foreach (var slider in new[] { Progress, PlayerProgress })
        {
            slider.PreviewMouseLeftButtonDown += (_, _) => Vm?.BeginScrub();
            slider.AddHandler(PreviewMouseLeftButtonUpEvent, new MouseButtonEventHandler((_, _) => Vm?.CommitScrub()), handledEventsToo: true);
            slider.LostMouseCapture += (_, _) => { if (Mouse.LeftButton == MouseButtonState.Released) Vm?.CommitScrub(); };
        }

        // Typing mode: a click into a text field lets the window take the keyboard (BarWindow.BeginTyping);
        // closing the panel, Esc or a paste hands it back.
        PanelContent.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (FindParent<TextBox>(e.OriginalSource as DependencyObject) is { } box)
            {
                TypingStarted?.Invoke(this, EventArgs.Empty);
                Dispatcher.InvokeAsync(() => { box.Focus(); Keyboard.Focus(box); }, DispatcherPriority.Input);
            }
        };
        PanelContent.PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            TypingEnded?.Invoke(this, EventArgs.Empty);
            SetExpanded(false);
            e.Handled = true;
        };

        // Shelf: drop files in; drag a file out to any app or folder.
        ShelfBar.DragOver += (_, e) =>
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Link : DragDropEffects.None;
            e.Handled = true;
        };
        ShelfBar.Drop += (_, e) =>
        {
            if (e.Data.GetData(DataFormats.FileDrop) is string[] files) Vm?.AddToShelf(files);
        };
        ShelfBar.PreviewMouseLeftButtonDown += (_, e) => _dragStart = e.GetPosition(ShelfBar);
        ShelfBar.PreviewMouseMove += (_, e) =>
        {
            if (e.LeftButton != MouseButtonState.Pressed || _dragStart is not { } start) return;
            var d = e.GetPosition(ShelfBar) - start;
            if (Math.Abs(d.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(d.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            _dragStart = null;
            if ((e.OriginalSource as FrameworkElement)?.DataContext is ShelfRow row)
            {
                _dragging = true;
                try { DragDrop.DoDragDrop(ShelfBar, new DataObject(DataFormats.FileDrop, new[] { row.Path }), DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link); }
                finally { _dragging = false; }
                CollapseWhenCursorLeaves(); // dropped somewhere else: the panel should not stay open
            }
        };

        // Moving the bar sideways: press on any empty part of the pill or panel (not a button, field, slider, the
        // shelf or the dock) and drag. Screen pixels from GetCursorPos, so the window moving under the mouse does
        // not feed back into the distance.
        Bar.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (Vm?.IsAlertPill == true || IsControl(e.OriginalSource as DependencyObject) || !GetCursorPos(out var p)) return;
            _slideFrom = p.X;
            _sliding = false;
        };
        Bar.PreviewMouseMove += (_, e) =>
        {
            if (_slideFrom is not { } from || e.LeftButton != MouseButtonState.Pressed || !GetCursorPos(out var p)) { _slideFrom = null; return; }
            int dx = p.X - from;
            if (!_sliding)
            {
                if (Math.Abs(dx) < 6) return;
                _sliding = true;
                _expandTimer.Stop();
                _collapseTimer.Stop();
                Bar.CaptureMouse();
                Mouse.OverrideCursor = Cursors.SizeWE;
                SlideStarted?.Invoke(this, EventArgs.Empty);
            }
            Sliding?.Invoke(this, dx);
            e.Handled = true;
        };
        Bar.PreviewMouseLeftButtonUp += (_, e) =>
        {
            _slideFrom = null;
            if (!_sliding) return;
            _sliding = false;
            Bar.ReleaseMouseCapture();
            Mouse.OverrideCursor = null;
            SlideEnded?.Invoke(this, EventArgs.Empty);
            e.Handled = true; // the release must not click whatever is under it now
            CollapseWhenCursorLeaves();
        };

        // Files dragged from Explorer: Windows sends no MouseEnter during a drag, so the hover never opened the
        // panel and the shelf could not be reached. A file over the bar opens it on the Clipboard tab; it stays
        // open while the drag is over it and closes once the cursor is gone.
        Bar.AllowDrop = true;
        Bar.DragEnter += (_, e) =>
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
            _collapseTimer.Stop();
            if (!IsExpanded && Vm is { IsAlertPill: false } vm)
            {
                vm.IsClipboardPage = true;
                SetExpanded(true);
            }
        };
        Bar.DragOver += (_, e) =>
        {
            // Over the shelf or the dock their own handlers say yes; anywhere else on the bar is not a target.
            _collapseTimer.Stop();
            e.Effects = DragDropEffects.None;
            e.Handled = true;
        };
        Bar.DragLeave += (_, _) => CollapseWhenCursorLeaves();
        Bar.Drop += (_, _) => CollapseWhenCursorLeaves();

        DockBar.DragOver += (_, e) =>
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Link : DragDropEffects.None;
            e.Handled = true;
        };
        DockBar.Drop += (_, e) =>
        {
            if (e.Data.GetData(DataFormats.FileDrop) is string[] files) Vm?.AddApps(files);
        };
    }

    public bool IsExpanded { get; private set; }

    /// <summary>
    /// Auto-hide: with nothing to show (no music, timer, download, toast) the collapsed pill shrinks to a thin line
    /// at the edge. The cursor on the line brings it back (and opens the panel as usual).
    /// </summary>
    public bool AutoHide
    {
        get => _autoHide;
        set
        {
            if (_autoHide == value) return;
            _autoHide = value;
            Retarget();
        }
    }

    private bool _autoHide;
    private bool _warnedTall;

    /// <summary>The tallest the open panel may be: the host window's height minus room for the wave. Unlimited in snapshots.</summary>
    public double MaxPanelHeight { get; set; } = double.PositiveInfinity;
    private bool _peek;
    private const double LineHeight = 5;

    private bool IsHiddenLine => AutoHide && !IsExpanded && !_peek && !_dragging && Vm is { PillKind: PillKind.Idle, HasStatus: false };

    /// <summary>A text field was clicked: the window should take the keyboard for a while.</summary>
    public event EventHandler? TypingStarted;
    /// <summary>Give the keyboard back to the window the user was typing in.</summary>
    public event EventHandler? TypingEnded;

    /// <summary>The user started moving the bar sideways.</summary>
    public event EventHandler? SlideStarted;
    /// <summary>Pixels moved since the press (screen pixels, positive = right).</summary>
    public event EventHandler<int>? Sliding;
    /// <summary>Released: the window saves where it is.</summary>
    public event EventHandler? SlideEnded;

    private int? _slideFrom;
    private bool _sliding;

    /// <summary>Things that keep their own mouse meaning: buttons, fields, sliders, scroll bars, the shelf and the dock.</summary>
    private bool IsControl(DependencyObject? d)
    {
        for (; d is not null; d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
        {
            if (d is System.Windows.Controls.Primitives.ButtonBase or TextBox or Slider or System.Windows.Controls.Primitives.ScrollBar) return true;
            if (ReferenceEquals(d, ShelfBar) || ReferenceEquals(d, DockBar) || ReferenceEquals(d, this)) return !ReferenceEquals(d, this);
        }
        return false;
    }

    private Point? _dragStart;
    private bool _dragging; // a drag out of the shelf leaves the panel: do not collapse under it
    private DispatcherTimer? _leaveWatch;

    /// <summary>
    /// After a drag (in or out) WPF's hover state is stale: MouseLeave may never come. Watch the real cursor and
    /// close the panel once it has been off the bar for a moment.
    /// </summary>
    private void CollapseWhenCursorLeaves()
    {
        _leaveWatch ??= new DispatcherTimer(TimeSpan.FromMilliseconds(400), DispatcherPriority.Background, (_, _) =>
        {
            if (_dragging || IsKeyboardFocusWithin) return;
            if (!CursorOverBar())
            {
                _leaveWatch!.Stop();
                SetExpanded(false);
            }
        }, Dispatcher);
        _leaveWatch.Stop();
        _leaveWatch.Start();
    }

    private bool CursorOverBar()
    {
        if (!GetCursorPos(out var p) || PresentationSource.FromVisual(Bar) is null) return false;
        var local = Bar.PointFromScreen(new Point(p.X, p.Y));
        return local.X >= 0 && local.Y >= 0 && local.X <= Bar.ActualWidth && local.Y <= Bar.ActualHeight;
    }

    private struct POINT { public int X, Y; }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT p);

    private static T? FindParent<T>(DependencyObject? d) where T : DependencyObject
    {
        while (d is not null and not T) d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        return d as T;
    }

    /// <summary>
    /// Top: glued to the top edge, opens downwards. Bottom: rests on the taskbar, opens upwards, and the shape is
    /// mirrored so the flat side faces the screen edge and the wave faces the desktop. The content is not mirrored.
    /// </summary>
    public BarEdge Edge
    {
        get => Bar.VerticalAlignment == VerticalAlignment.Bottom ? BarEdge.Bottom : BarEdge.Top;
        set
        {
            Bar.VerticalAlignment = value == BarEdge.Bottom ? VerticalAlignment.Bottom : VerticalAlignment.Top;
            Outline.RenderTransformOrigin = new Point(0.5, 0.5);
            Outline.RenderTransform = value == BarEdge.Bottom ? new ScaleTransform(1, -1) : Transform.Identity;
        }
    }

    /// <summary>
    /// Battery saver is on: the open panel's wave stands still and the equaliser rests. The spring morph still runs,
    /// so opening and closing look the same; only the endless motion stops.
    /// </summary>
    public bool ReduceMotion
    {
        get => _reduceMotion;
        set
        {
            if (_reduceMotion == value) return;
            _reduceMotion = value;
            UpdateEqualiser();
            if (!value && IsExpanded) StartFrames();
        }
    }

    private bool _reduceMotion;

    private BarViewModel? Vm => DataContext as BarViewModel;

    /// <summary>Jump straight to a state, no animation. For snapshots, first show and hiding.</summary>
    public void ShowInstant(bool expanded)
    {
        _expandTimer.Stop();
        _collapseTimer.Stop();
        IsExpanded = expanded;
        if (!expanded) TypingEnded?.Invoke(this, EventArgs.Empty);
        Vm?.OnExpandedChanged(expanded);
        ApplyFaces(animate: false);
        var (w, h) = TargetSize();
        _width.Snap(w);
        _height.Snap(h);
        _amplitude.Snap(expanded ? _panelWave : IsHiddenLine ? 0 : _pillWave);
        PillContent.Visibility = IsHiddenLine ? Visibility.Hidden : Visibility.Visible;
        ApplyShape();
        UpdateEqualiser();
        // The wave keeps moving while open; collapsed (or on battery saver), it stands still and nothing animates.
        if (expanded && !ReduceMotion) StartFrames(); else StopFrames();
    }

    public void SetExpanded(bool expanded)
    {
        if (IsExpanded == expanded) return;
        IsExpanded = expanded;
        if (!expanded) TypingEnded?.Invoke(this, EventArgs.Empty);
        Vm?.OnExpandedChanged(expanded);
        ApplyFaces(animate: true);
        UpdateEqualiser();
        Retarget();
    }

    private void OnCollapseRequested(object? sender, EventArgs e) => ShowInstant(expanded: false);

    private void OnReturnFocusRequested(object? sender, EventArgs e) => TypingEnded?.Invoke(this, EventArgs.Empty);

    /// <summary>Opened by the view model (the rain alert's «Прогноз»); closes as usual once the cursor has left.</summary>
    private void OnExpandRequested(object? sender, EventArgs e)
    {
        SetExpanded(true);
        CollapseWhenCursorLeaves();
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            // Pill width depends on what it shows; panel height on which sections are visible.
            case nameof(BarViewModel.PillKind) or nameof(BarViewModel.HasStatus) or nameof(BarViewModel.HasMedia)
                or nameof(BarViewModel.IsDeviceListOpen) or nameof(BarViewModel.HasDeviceError) or nameof(BarViewModel.IsMixerOpen)
                or nameof(BarViewModel.HasAudioDevice) or nameof(BarViewModel.HasWeather)
                or nameof(BarViewModel.Page) or nameof(BarViewModel.ShowLyricsColumn)
                or nameof(BarViewModel.ClipCount) or nameof(BarViewModel.ShelfCount) or nameof(BarViewModel.HasReminders)
                or nameof(BarViewModel.ReminderCount) or nameof(BarViewModel.VpnCount) or nameof(BarViewModel.HasNoVpn)
                or nameof(BarViewModel.HasRainNote) or nameof(BarViewModel.HasForecast)
                or nameof(BarViewModel.PairedCount) or nameof(BarViewModel.HasBluetooth) or nameof(BarViewModel.AlertPrimaryLabel):
                Dispatcher.InvokeAsync(Retarget, DispatcherPriority.Loaded);
                break;
            case nameof(BarViewModel.ShowPillMusic) or nameof(BarViewModel.IsPlaying):
                UpdateEqualiser();
                break;
            case nameof(BarViewModel.CurrentLine):
                AnimateLyricLine();
                break;
        }
    }

    // ---------------------------------------------------------------- shape

    private void Retarget()
    {
        (_width.Target, _height.Target) = TargetSize();
        _amplitude.Target = IsExpanded ? _panelWave : IsHiddenLine ? 0 : _pillWave;
        PillContent.Visibility = IsHiddenLine ? Visibility.Hidden : Visibility.Visible;
        StartFrames();
    }

    private (double W, double H) TargetSize()
    {
        if (IsExpanded)
        {
            // The layout pass measured the panel inside the 38 DIP pill; without invalidating, Measure returns that cached answer.
            PanelContent.InvalidateMeasure();
            PanelContent.Measure(new Size(PanelContent.Width, double.PositiveInfinity));
            double h = PanelContent.DesiredSize.Height;
            // The host window has a fixed height: a taller panel would be cut off with no edge at the bottom.
            // Keep the shape inside it (the content clips at the edge) and say so in the log, so a page that grew is noticed.
            if (h > MaxPanelHeight)
            {
                if (!_warnedTall) Log.Warn("layout", $"panel page {Vm?.Page} is {h:0} DIP, more than the window allows ({MaxPanelHeight:0})");
                _warnedTall = true;
                h = MaxPanelHeight;
            }
            return (PanelContent.Width, h);
        }

        var vm = Vm;
        double R(string key) => (double)FindResource(key);
        if (IsHiddenLine) return (R("Layout.PillWidth") * 0.6, LineHeight);
        if (vm?.IsAlertPill == true) return (R("Layout.WidePillWidth"), R("Layout.AlertHeight"));
        double w = vm is null ? R("Layout.PillWidth")
            : vm.HasStatus || vm.PillKind is PillKind.Download or PillKind.Listening or PillKind.Agent ? R("Layout.WidePillWidth")
            : vm.PillKind == PillKind.Music ? R("Layout.MusicPillWidth")
            : R("Layout.PillWidth");
        return (w, R("Layout.PillHeight"));
    }

    private void StartFrames()
    {
        if (_animating) return;
        _animating = true;
        _lastFrame = TimeSpan.Zero;
        CompositionTarget.Rendering += OnFrame;
    }

    private void StopFrames()
    {
        if (!_animating) return;
        CompositionTarget.Rendering -= OnFrame;
        _animating = false;
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        var now = ((RenderingEventArgs)e).RenderingTime;
        double dt = _lastFrame == TimeSpan.Zero ? 1.0 / 60 : (now - _lastFrame).TotalSeconds;
        _lastFrame = now;
        if (dt <= 0) return; // Rendering can fire twice for one frame

        bool doneW = _width.Step(dt, _stiffness, _damping);
        bool doneH = _height.Step(dt, _stiffness, _damping);
        bool doneA = _amplitude.Step(dt, _stiffness, _damping);
        _phase = (_phase + dt * WaveSpeed * 2 * Math.PI) % (2 * Math.PI);

        bool morphing = !(doneW && doneH && doneA);
        _sinceWaveFrame += dt;
        if (morphing || _sinceWaveFrame >= WaveFrame)
        {
            _sinceWaveFrame = 0;
            ApplyShape();
        }

        // Collapsed and at rest: stop the per-frame callback (the pill's wave stands still). An idle bar must cost nothing.
        // On battery saver the open panel's wave stands still too.
        if (!morphing && (!IsExpanded || ReduceMotion))
        {
            ApplyShape();
            StopFrames();
        }
    }

    private void ApplyShape()
    {
        double w = Math.Max(1, _width.Value), h = Math.Max(1, _height.Value);
        Bar.Width = w;
        Bar.Height = h;
        Outline.Data = BuildOutline(w, h, Math.Min(h / 2, _panelRadius), _amplitude.Value, _phase);
    }

    /// <summary>Square top (glued to the screen edge), rounded bottom corners, wavy bottom edge between them.</summary>
    private static Geometry BuildOutline(double w, double h, double r, double amplitude, double phase)
    {
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(new Point(0, 0), isFilled: true, isClosed: true);
            c.LineTo(new Point(w, 0), false, false);
            c.LineTo(new Point(w, h - r), false, false);
            c.ArcTo(new Point(w - r, h), new Size(r, r), 0, false, SweepDirection.Clockwise, false, false);
            var edge = Wave.BottomEdge(w, h, r, amplitude, WaveLength, phase);
            c.PolyLineTo(edge.Select(p => new Point(p.X, p.Y)).ToList(), false, false);
            c.ArcTo(new Point(0, h - r), new Size(r, r), 0, false, SweepDirection.Clockwise, false, false);
        }
        g.Freeze();
        return g;
    }

    // ---------------------------------------------------------------- faces

    private void ApplyFaces(bool animate)
    {
        var fast = (Duration)FindResource("Motion.Fast");
        var baseDur = (Duration)FindResource("Motion.Base");

        PanelContent.IsHitTestVisible = IsExpanded;
        PillContent.IsHitTestVisible = !IsExpanded;
        if (IsExpanded) PanelContent.Visibility = Visibility.Visible;

        if (!animate)
        {
            PanelContent.BeginAnimation(OpacityProperty, null);
            PillContent.BeginAnimation(OpacityProperty, null);
            PanelContent.Opacity = IsExpanded ? 1 : 0;
            PillContent.Opacity = IsExpanded ? 0 : 1;
            if (!IsExpanded) PanelContent.Visibility = Visibility.Hidden;
            return;
        }

        // Outgoing face fades fast; incoming waits until the shape is most of the way there.
        var outgoing = new DoubleAnimation(0, fast);
        var incoming = new DoubleAnimation(1, baseDur) { BeginTime = TimeSpan.FromMilliseconds(90) };
        if (IsExpanded)
        {
            PillContent.BeginAnimation(OpacityProperty, outgoing);
            PanelContent.BeginAnimation(OpacityProperty, incoming);
        }
        else
        {
            outgoing.Completed += (_, _) => { if (!IsExpanded) PanelContent.Visibility = Visibility.Hidden; };
            PanelContent.BeginAnimation(OpacityProperty, outgoing);
            PillContent.BeginAnimation(OpacityProperty, incoming);
        }
    }

    // ---------------------------------------------------------------- equaliser

    /// <summary>
    /// Bars bounce only while the music pill is actually on screen and playing. Each bar loops through its own
    /// heights at a different pace, so together they never look like one repeating pattern. Capped at 20 fps.
    /// </summary>
    private void UpdateEqualiser()
    {
        bool run = Vm is { ShowPillMusic: true, IsPlaying: true } && !IsExpanded && IsVisible && !ReduceMotion;
        if (run == _eqRunning) return;
        _eqRunning = run;

        var bars = new[] { EqBar1, EqBar2, EqBar3, EqBar4 };
        double[][] heights = [[5, 12, 7, 14, 6], [12, 6, 15, 9, 13], [8, 14, 5, 11, 9], [4, 9, 12, 6, 10]];
        double[] periods = [0.84, 1.06, 0.92, 1.18];
        for (int i = 0; i < bars.Length; i++)
        {
            if (!run)
            {
                bars[i].BeginAnimation(HeightProperty, null);
                continue;
            }
            var anim = new DoubleAnimationUsingKeyFrames { RepeatBehavior = RepeatBehavior.Forever, AutoReverse = true };
            Timeline.SetDesiredFrameRate(anim, 20);
            var step = periods[i] / heights[i].Length;
            for (int k = 0; k < heights[i].Length; k++)
                anim.KeyFrames.Add(new EasingDoubleKeyFrame(heights[i][k], KeyTime.FromTimeSpan(TimeSpan.FromSeconds(step * (k + 1))),
                    new SineEase { EasingMode = EasingMode.EaseInOut }));
            bars[i].BeginAnimation(HeightProperty, anim);
        }
    }

    // ---------------------------------------------------------------- lyrics

    private void AnimateLyricLine()
    {
        if (!IsExpanded || CurrentLyric.RenderTransform is not TranslateTransform t) return;
        var dur = (Duration)FindResource("Motion.Base");
        t.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(8, 0, dur) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        CurrentLyric.BeginAnimation(OpacityProperty, new DoubleAnimation(0.3, 1, dur));
    }
}
