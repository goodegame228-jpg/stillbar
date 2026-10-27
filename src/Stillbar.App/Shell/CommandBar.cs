using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Stillbar.App.Design;
using Stillbar.App.Services;
using Stillbar.Core.Commands;
using Stillbar.Core.Display;

namespace Stillbar.App.Shell;

/// <summary>
/// Alt+Space: one field, results as you type (answers first, then apps and files). ↑↓ choose, Enter does it,
/// Esc or a click elsewhere closes, and the keyboard goes back to the window that had it.
/// </summary>
internal sealed class CommandBar : Window
{
    private sealed record Row(CommandResult Result, string Glyph, ImageSource? Icon)
    {
        public string Title => Result.Title;
        public string Detail => Result.Detail;
        public bool HasIcon => Icon is not null;
        public bool HasGlyph => Icon is null;
    }

    private static CommandBar? _open;

    private readonly BarViewModel _vm;
    private readonly LaunchIndex _index;
    private readonly nint _returnTo;
    private readonly TextBox _input;
    private readonly ListBox _list;
    private readonly ObservableCollection<Row> _rows = [];
    private bool _launched;

    private CommandBar(BarViewModel vm, LaunchIndex index, MonitorArea monitor)
    {
        _vm = vm;
        _index = index;
        _returnTo = NativeWindow.Foreground();
        Title = "Быстрая команда";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        Width = 640;
        SizeToContent = SizeToContent.Height;

        _input = new TextBox { Style = Tokens.Style("Field"), Tag = "Приложение, файл, 2+2, «таймер 5 мин», «напомни через 10 минут…»", FontSize = 16 };
        _input.TextChanged += (_, _) => Refresh();
        _list = new ListBox
        {
            ItemsSource = _rows,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Margin = new Thickness(0, 8, 0, 0),
            Focusable = false,
            ItemTemplate = RowTemplate(),
            ItemContainerStyle = RowContainer(),
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(_list, ScrollBarVisibility.Disabled);
        _list.MouseLeftButtonUp += (_, _) => Run();

        var hint = new TextBlock
        {
            Style = Tokens.Style("Text.Muted"),
            FontSize = Tokens.Number("Type.Xs"),
            Margin = new Thickness(6, 8, 6, 0),
            Text = "↑↓ выбрать · Enter выполнить · Esc закрыть · текстовые команды («транслит», «json», «раскладка») работают с текстом в буфере",
            TextWrapping = TextWrapping.Wrap,
        };
        Content = new Border
        {
            Background = Tokens.Brush("Brush.Bg"),
            CornerRadius = new CornerRadius(Tokens.Number("Radius.Panel")),
            Padding = new Thickness(14),
            BorderThickness = new Thickness(1),
            BorderBrush = Tokens.Brush("Brush.SurfaceActive"),
            Child = new StackPanel { Children = { _input, _list, hint } },
        };

        PreviewKeyDown += OnKey;
        Deactivated += (_, _) => Close();
        Closed += (_, _) =>
        {
            _open = null;
            if (!_launched) NativeWindow.Focus(_returnTo);
        };
        SourceInitialized += (_, _) =>
        {
            // Centred on the bar's monitor, a little below the pill.
            int w = (int)Math.Round(Width * monitor.Scale);
            NativeWindow.MoveTo(new WindowInteropHelper(this).Handle, monitor.Bounds.Left + (monitor.Width - w) / 2, monitor.Bounds.Top + (int)(96 * monitor.Scale));
        };
        Loaded += (_, _) =>
        {
            Activate();
            _input.Focus();
            Keyboard.Focus(_input);
        };
    }

    /// <summary>Opens the bar (or brings the open one back).</summary>
    public static void Open(BarViewModel vm, LaunchIndex index, MonitorArea monitor)
    {
        if (_open is { } existing)
        {
            existing.Activate();
            return;
        }
        _open = new CommandBar(vm, index, monitor);
        _open.Show();
    }

    private void Refresh()
    {
        _rows.Clear();
        foreach (var r in CommandRouter.Route(_input.Text, DateTimeOffset.Now, _index.Items))
            _rows.Add(new Row(r, Glyph(r.Kind), r.Kind is CommandKind.App or CommandKind.File ? AppDock.LoadIcon((string)r.Payload!) : null));
        _list.SelectedIndex = _rows.Count > 0 ? 0 : -1;
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                Close();
                break;
            case Key.Down when _rows.Count > 0:
                _list.SelectedIndex = (_list.SelectedIndex + 1) % _rows.Count;
                _list.ScrollIntoView(_list.SelectedItem);
                break;
            case Key.Up when _rows.Count > 0:
                _list.SelectedIndex = (_list.SelectedIndex - 1 + _rows.Count) % _rows.Count;
                _list.ScrollIntoView(_list.SelectedItem);
                break;
            case Key.Enter:
                Run();
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    private void Run()
    {
        if (_list.SelectedItem is not Row row) return;
        _launched = row.Result.Kind is CommandKind.App or CommandKind.File; // the opened app takes the focus itself
        // Give the keyboard back first: text commands and pastes belong to the user's window, not ours.
        Hide();
        if (!_launched) NativeWindow.Focus(_returnTo);
        _vm.ExecuteCommand(row.Result);
        _launched = true; // focus is already handled
        Close();
    }

    private static string Glyph(CommandKind k) => k switch
    {
        CommandKind.Calc => "",
        CommandKind.Convert => "",
        CommandKind.Timer => "",
        CommandKind.Reminder => "",
        CommandKind.Note => "",
        CommandKind.TextTool => "",
        _ => "",
    };

    private static DataTemplate RowTemplate()
    {
        // Built in code: this window has no XAML (see the BarView note on local types).
        var root = new FrameworkElementFactory(typeof(DockPanel));
        root.SetValue(HeightProperty, 46.0);

        var iconBox = new FrameworkElementFactory(typeof(Grid));
        iconBox.SetValue(WidthProperty, 32.0);
        iconBox.SetValue(DockPanel.DockProperty, Dock.Left);
        iconBox.SetValue(MarginProperty, new Thickness(4, 0, 12, 0));
        var image = new FrameworkElementFactory(typeof(Image));
        image.SetBinding(Image.SourceProperty, new System.Windows.Data.Binding(nameof(Row.Icon)));
        image.SetValue(WidthProperty, 24.0);
        image.SetValue(HeightProperty, 24.0);
        var glyph = new FrameworkElementFactory(typeof(TextBlock));
        glyph.SetValue(StyleProperty, Tokens.Style("Icon"));
        glyph.SetValue(TextBlock.FontSizeProperty, 16.0);
        glyph.SetValue(TextBlock.ForegroundProperty, Tokens.Brush("Brush.Accent"));
        glyph.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(Row.Glyph)));
        glyph.SetBinding(VisibilityProperty, new System.Windows.Data.Binding(nameof(Row.HasGlyph)) { Converter = new BooleanToVisibilityConverter() });
        iconBox.AppendChild(image);
        iconBox.AppendChild(glyph);

        var text = new FrameworkElementFactory(typeof(StackPanel));
        text.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        var title = new FrameworkElementFactory(typeof(TextBlock));
        title.SetValue(StyleProperty, Tokens.Style("Text.Base"));
        title.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
        title.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        title.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(Row.Title)));
        var detail = new FrameworkElementFactory(typeof(TextBlock));
        detail.SetValue(StyleProperty, Tokens.Style("Text.Muted"));
        detail.SetValue(TextBlock.FontSizeProperty, Tokens.Number("Type.Xs"));
        detail.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        detail.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(Row.Detail)));
        text.AppendChild(title);
        text.AppendChild(detail);

        root.AppendChild(iconBox);
        root.AppendChild(text);
        return new DataTemplate { VisualTree = root };
    }

    private static Style RowContainer()
    {
        var bg = new FrameworkElementFactory(typeof(Border), "Bg");
        bg.SetValue(Border.CornerRadiusProperty, (CornerRadius)Application.Current.FindResource("Radius.Md"));
        bg.SetValue(Border.PaddingProperty, new Thickness(4, 0, 8, 0));
        bg.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        bg.AppendChild(new FrameworkElementFactory(typeof(ContentPresenter)));
        var template = new ControlTemplate(typeof(ListBoxItem)) { VisualTree = bg };
        template.Triggers.Add(new Trigger { Property = ListBoxItem.IsSelectedProperty, Value = true, Setters = { new Setter(Border.BackgroundProperty, Tokens.Brush("Brush.SurfaceActive"), "Bg") } });
        template.Triggers.Add(new Trigger { Property = IsMouseOverProperty, Value = true, Setters = { new Setter(Border.BackgroundProperty, Tokens.Brush("Brush.SurfaceHover"), "Bg") } });
        var style = new Style(typeof(ListBoxItem));
        style.Setters.Add(new Setter(TemplateProperty, template));
        style.Setters.Add(new Setter(FocusableProperty, false));
        style.Setters.Add(new Setter(CursorProperty, Cursors.Hand));
        return style;
    }
}
