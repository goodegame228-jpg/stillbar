using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using Stillbar.App.Design;
using Stillbar.App.Services;
using Stillbar.Core.Controls;
using Stillbar.Core.Settings;

namespace Stillbar.App.Shell;

/// <summary>
/// Everything that used to live only in settings.json. Most changes apply at once through the bar window; the few
/// that are wired at startup (theme, clipboard history, lyrics, self-restart) say so and offer a restart.
/// </summary>
internal sealed class SettingsWindow : Window
{
    private static SettingsWindow? _open;

    private readonly BarWindow _bar;
    private readonly StackPanel _root = new() { Margin = new Thickness(24, 16, 24, 24) };
    private readonly TextBlock _city;
    private readonly StackPanel _cityResults = new();
    private readonly Button _restart;
    private readonly AppSettings _atStart;

    private SettingsWindow(BarWindow bar)
    {
        _bar = bar;
        _atStart = bar.Settings;
        Title = "Настройки — Stillbar";
        Width = 560;
        SizeToContent = SizeToContent.Height;
        MaxHeight = SystemParameters.WorkArea.Height * 0.9;
        ResizeMode = ResizeMode.CanMinimize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Tokens.Brush("Brush.Bg");
        SourceInitialized += (_, _) => NativeWindow.DarkTitleBar(new WindowInteropHelper(this).Handle);
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
        Closed += (_, _) => _open = null;

        // ---- weather
        Section("Погода");
        _city = Muted($"Город: {S.City.Name}");
        _root.Children.Add(_city);
        var search = new TextBox { Style = Tokens.Style("Field"), Tag = "Найти город: Казань, Минск, Алматы…", Margin = new Thickness(0, 8, 0, 0) };
        search.KeyDown += async (_, e) => { if (e.Key == Key.Enter) await FindCity(search.Text); };
        var find = new Button { Style = Tokens.Style("TextButton"), Content = "Найти", Margin = new Thickness(8, 8, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        find.Click += async (_, _) => await FindCity(search.Text);
        var row = new DockPanel();
        DockPanel.SetDock(find, Dock.Right);
        row.Children.Add(find);
        row.Children.Add(search);
        _root.Children.Add(row);
        _root.Children.Add(_cityResults);
        Check("Предупреждать о дожде и снеге примерно за час", S.RainWarnings, v => Set(S with { RainWarnings = v }));

        // ---- look
        Section("Вид");
        Label("Тема пилюли");
        Choice([("dark", "Тёмная"), ("light", "Светлая"), ("system", "Как в Windows")], S.Theme, v => Set(S with { Theme = v }));
        _root.Children.Add(Muted("Тема применится после перезапуска Stillbar."));

        Label("Размер");
        var scaleText = Muted($"{S.Scale} %");
        var scale = new Slider
        {
            Style = Tokens.Style("Slider.Thin"), Minimum = 80, Maximum = 200, TickFrequency = 10, IsSnapToTickEnabled = true,
            Value = S.Scale, Foreground = Tokens.Brush("Brush.Accent"), Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center,
        };
        scale.ValueChanged += (_, e) => { scaleText.Text = $"{(int)e.NewValue} %"; Set(S with { Scale = (int)e.NewValue }); };
        var scaleRow = new DockPanel { Margin = new Thickness(0, 4, 0, 0) };
        DockPanel.SetDock(scaleText, Dock.Right);
        scaleRow.Children.Add(scaleText);
        scaleRow.Children.Add(scale);
        _root.Children.Add(scaleRow);

        Label("Край экрана");
        Choice([("top", "Сверху"), ("bottom", "Снизу")], S.Edge, v => Set(S with { Edge = v }));

        var monitors = Monitors.All();
        if (monitors.Count > 1)
        {
            Label("Монитор");
            var current = Core.Display.Placement.Pick(monitors, S.Monitor).Name;
            Choice(monitors.Select((m, i) => (m.Name, $"{i + 1}{(m.IsPrimary ? " · основной" : "")}")).ToList(), current,
                v => Set(S with { Monitor = monitors.First(m => m.Name == v).IsPrimary ? null : v }));
        }

        // ---- behaviour
        Section("Поведение");
        Check("Не закрывать окна: развёрнутые окна и вкладки начинаются под полоской", S.ReserveStrip, v => Set(S with { ReserveStrip = v }));
        Check("Прятать пилюлю в тонкую линию, когда нечего показывать", S.AutoHide, v => Set(S with { AutoHide = v }));
        Check("Пропускать клики сквозь пилюлю (чтобы пользоваться ей, держите Ctrl)", S.ClickThrough, v => Set(S with { ClickThrough = v }));
        Check("Прятать, когда игра или видео на весь экран", S.HideInFullscreen, v => Set(S with { HideInFullscreen = v }));
        Check("Показывать раскладку (RU, EN) и Caps Lock, когда они меняются", S.KeyboardIndicator, v => Set(S with { KeyboardIndicator = v }));
        Label("Прятать, когда впереди эти программы");
        var excluded = new TextBox
        {
            Style = Tokens.Style("Field"), Tag = "Например: obs64, zoom, powerpnt", Text = S.ExcludedApps, Margin = new Thickness(0, 4, 0, 0),
        };
        excluded.LostKeyboardFocus += (_, _) => Set(S with { ExcludedApps = string.Join(", ", Exclusions.Parse(excluded.Text)) });
        excluded.KeyDown += (_, e) => { if (e.Key == Key.Enter) Set(S with { ExcludedApps = string.Join(", ", Exclusions.Parse(excluded.Text)) }); };
        _root.Children.Add(excluded);
        _root.Children.Add(Muted("Имя процесса, как в Диспетчере задач на вкладке «Подробности», без .exe."));

        // ---- features
        Section("Функции");
        Check("Слова песен из LRCLIB", S.LyricsEnabled, v => Set(S with { LyricsEnabled = v }));
        Check("История буфера обмена (то, что программы помечают как приватное, не сохраняется)", S.ClipboardHistory, v => Set(S with { ClipboardHistory = v }));
        Check("Перезапускаться, если Stillbar завис или упал", S.RestartOnHang, v => Set(S with { RestartOnHang = v }));
        Check("Запускать вместе с Windows", Startup.IsEnabled(), Startup.Set);

        // ---- Claude Code
        Section("Claude Code");
        _root.Children.Add(Muted("Пилюля покажет, над чем работает Claude, и позволит разрешать или запрещать его действия, не переключаясь в окно Claude. Добавляет хуки в ~/.claude/settings.json; копия прежнего файла сохраняется рядом."));
        var claudeState = Muted("");
        var claudeButton = new Button { Style = Tokens.Style("TextButton"), Margin = new Thickness(0, 8, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
        void ShowClaude()
        {
            bool on = ClaudeCodeSetup.IsConnected();
            claudeState.Text = on ? "Подключено. Новые сессии Claude Code будут видны в пилюле." : "Не подключено.";
            claudeButton.Content = on ? "Отключить" : "Подключить";
        }
        claudeButton.Click += (_, _) =>
        {
            var error = ClaudeCodeSetup.IsConnected() ? ClaudeCodeSetup.Disconnect() : ClaudeCodeSetup.Connect();
            ShowClaude();
            if (error is not null) claudeState.Text = error;
        };
        ShowClaude();
        _root.Children.Add(claudeState);
        _root.Children.Add(claudeButton);

        // ---- footer
        _restart = new Button { Style = Tokens.Style("TextButton"), Content = "Перезапустить Stillbar, чтобы применить", Visibility = Visibility.Collapsed, Margin = new Thickness(0, 16, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
        _restart.Click += (_, _) => RestartApp();
        _root.Children.Add(_restart);
        var links = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 16, 0, 0) };
        var data = new Button { Style = Tokens.Style("TextButton"), Content = "Папка с данными" };
        data.Click += (_, _) => Process.Start(new ProcessStartInfo("explorer.exe", $"\"{Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Stillbar")}\"") { UseShellExecute = true });
        var log = new Button { Style = Tokens.Style("TextButton"), Content = "Журнал", Margin = new Thickness(8, 0, 0, 0) };
        log.Click += (_, _) => Log.OpenFolder();
        links.Children.Add(data);
        links.Children.Add(log);
        _root.Children.Add(links);

        Content = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _root };
    }

    /// <summary>For --snapshot: the window's content, detached, at its normal width.</summary>
    internal static FrameworkElement Render(BarWindow bar)
    {
        var w = new SettingsWindow(bar);
        var content = (FrameworkElement)w.Content;
        w.Content = null;
        w.Close();
        return new Border { Width = 560, Background = Tokens.Brush("Brush.Bg"), Child = content };
    }

    public static void Open(BarWindow bar)
    {
        if (_open is { } w) { w.Activate(); return; }
        _open = new SettingsWindow(bar);
        _open.Show();
        _open.Activate();
    }

    private AppSettings S => _bar.Settings;

    private void Set(AppSettings next)
    {
        _bar.Apply(next);
        // Wired once at startup: these need a restart to take effect.
        bool needsRestart = next.Theme != _atStart.Theme || next.ClipboardHistory != _atStart.ClipboardHistory
            || next.LyricsEnabled != _atStart.LyricsEnabled || next.RestartOnHang != _atStart.RestartOnHang;
        _restart.Visibility = needsRestart ? Visibility.Visible : Visibility.Collapsed;
    }

    private async Task FindCity(string name)
    {
        _cityResults.Children.Clear();
        _cityResults.Children.Add(Muted("Ищу…"));
        var places = await OpenMeteoWeatherService.SearchAsync(name);
        _cityResults.Children.Clear();
        if (places.Count == 0) { _cityResults.Children.Add(Muted("Ничего не нашлось. Проверьте название или интернет.")); return; }
        foreach (var p in places)
        {
            var b = new Button { Style = Tokens.Style("TextButton"), Content = p.Label, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 6, 0, 0) };
            b.Click += (_, _) =>
            {
                Set(S with { City = new CitySetting(p.Name, p.Lat, p.Lon) });
                _city.Text = $"Город: {p.Name}";
                _cityResults.Children.Clear();
            };
            _cityResults.Children.Add(b);
        }
    }

    private static void RestartApp()
    {
        Process.Start(new ProcessStartInfo(Environment.ProcessPath!, $"--after {Environment.ProcessId}") { UseShellExecute = false });
        Application.Current.Shutdown();
    }

    // ------------------------------------------------------------------ building blocks

    private void Section(string title) =>
        _root.Children.Add(new TextBlock { Style = Tokens.Style("Text.Title"), Text = title, Margin = new Thickness(0, _root.Children.Count == 0 ? 0 : 24, 0, 8) });

    private void Label(string text) =>
        _root.Children.Add(new TextBlock { Style = Tokens.Style("Text.Base"), Text = text, Margin = new Thickness(0, 12, 0, 6) });

    private static TextBlock Muted(string text) =>
        new() { Style = Tokens.Style("Text.Muted"), Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };

    private void Check(string text, bool value, Action<bool> changed)
    {
        var c = new CheckBox { Style = Tokens.Style("Check"), Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap }, IsChecked = value };
        c.Checked += (_, _) => changed(true);
        c.Unchecked += (_, _) => changed(false);
        _root.Children.Add(c);
    }

    /// <summary>A row of tab-style buttons, one of them chosen.</summary>
    private void Choice(IReadOnlyList<(string Value, string Label)> options, string current, Action<string> changed)
    {
        var row = new WrapPanel();
        var group = Guid.NewGuid().ToString("N");
        foreach (var (value, label) in options)
        {
            var b = new RadioButton { Style = Tokens.Style("Segment"), Content = label, GroupName = group, IsChecked = value == current, Margin = new Thickness(0, 0, 6, 0), Focusable = false };
            b.Checked += (_, _) => changed(value);
            row.Children.Add(b);
        }
        _root.Children.Add(row);
    }
}
