using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Stillbar.App.Design;
using Stillbar.App.Services;

namespace Stillbar.App.Capture;

/// <summary>
/// Marks up a snip before it goes anywhere: pen, highlighter, arrow and frame in four colours, undo. Shapes are
/// drawn in the picture's own pixels (the canvas sits inside a Viewbox), so the exported image is full size.
/// Ctrl+C or Enter copies and closes, Ctrl+S saves to Pictures\Stillbar, Ctrl+Z undoes, Esc closes.
/// </summary>
internal sealed class MarkupEditor : Window
{
    private enum Tool { Pen, Marker, Arrow, Frame }

    private readonly BitmapSource _image;
    private readonly Grid _surface;
    private readonly Canvas _ink = new() { ClipToBounds = true, Background = Brushes.Transparent };
    private readonly Stack<UIElement> _done = new();
    private readonly Func<string, bool> _copy;
    private readonly TextBlock _status;
    private Tool _tool = Tool.Arrow;
    private Brush _color;
    private Point _from;
    private Shape? _drawing;

    private MarkupEditor(BitmapSource image, Func<string, bool> copyImageFile)
    {
        _image = image;
        _copy = copyImageFile;
        _color = Tokens.Brush("Brush.Danger");
        Title = "Разметка снимка — Stillbar";
        Background = Tokens.Brush("Brush.Bg");
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        var work = SystemParameters.WorkArea;
        Width = Math.Min(work.Width * 0.85, Math.Max(640, image.PixelWidth + 48));
        Height = Math.Min(work.Height * 0.85, Math.Max(420, image.PixelHeight + 120));

        _surface = new Grid { Width = image.PixelWidth, Height = image.PixelHeight, Children = { new Image { Source = image, Stretch = Stretch.Fill }, _ink } };
        _ink.MouseLeftButtonDown += Begin;
        _ink.MouseMove += Drag;
        _ink.MouseLeftButtonUp += End;
        _ink.Cursor = Cursors.Pen;

        _status = new TextBlock { Style = Tokens.Style("Text.Muted"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
        var bar = new DockPanel { Margin = new Thickness(16, 12, 16, 12), LastChildFill = true };
        var right = new StackPanel { Orientation = Orientation.Horizontal };
        right.Children.Add(TextButton("Сохранить", Save, "Ctrl+S: в папку «Изображения\\Stillbar»"));
        right.Children.Add(TextButton("Копировать", () => { if (Copy()) Close(); }, "Ctrl+C или Enter"));
        DockPanel.SetDock(right, Dock.Right);
        bar.Children.Add(right);

        var tools = new StackPanel { Orientation = Orientation.Horizontal };
        var group = $"tools{GetHashCode()}";
        foreach (var (tool, glyph, label) in new[] { (Tool.Arrow, "", "Стрелка"), (Tool.Frame, "", "Рамка"), (Tool.Pen, "", "Перо"), (Tool.Marker, "", "Маркер") })
        {
            var b = new RadioButton { Style = Tokens.Style("Tab"), Tag = glyph, Content = label, GroupName = group, IsChecked = tool == _tool, Margin = new Thickness(0, 0, 4, 0), Focusable = false };
            b.Checked += (_, _) => _tool = tool;
            tools.Children.Add(b);
        }
        tools.Children.Add(new Border { Width = 1, Background = Tokens.Brush("Brush.Track"), Margin = new Thickness(8, 6, 8, 6) });
        foreach (var key in new[] { "Brush.Danger", "Brush.Warning", "Brush.Accent", "Brush.Control" })
        {
            var swatch = new Button
            {
                Width = 22, Height = 22, Margin = new Thickness(3, 0, 3, 0), Cursor = Cursors.Hand, Focusable = false, ToolTip = "Цвет",
                Template = SwatchTemplate(), Background = Tokens.Brush(key),
            };
            swatch.Click += (_, _) => _color = Tokens.Brush(key);
            tools.Children.Add(swatch);
        }
        tools.Children.Add(new Border { Width = 1, Background = Tokens.Brush("Brush.Track"), Margin = new Thickness(8, 6, 8, 6) });
        var undo = new Button { Style = Tokens.Style("IconButton.Ghost"), Content = "", ToolTip = "Отменить (Ctrl+Z)", Focusable = false };
        undo.Click += (_, _) => Undo();
        tools.Children.Add(undo);
        tools.Children.Add(_status);
        bar.Children.Add(tools);

        var root = new DockPanel();
        DockPanel.SetDock(bar, Dock.Top);
        root.Children.Add(bar);
        root.Children.Add(new Viewbox { Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, Margin = new Thickness(16, 0, 16, 16), Child = _surface });
        Content = root;

        KeyDown += (_, e) =>
        {
            bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
            if (e.Key == Key.Escape) Close();
            else if (e.Key == Key.Enter || (ctrl && e.Key == Key.C)) { if (Copy()) Close(); }
            else if (ctrl && e.Key == Key.S) Save();
            else if (ctrl && e.Key == Key.Z) Undo();
            else return;
            e.Handled = true;
        };
    }

    /// <param name="copyImageFile">Puts a PNG file on the clipboard; false if the clipboard was busy.</param>
    public static void Open(BitmapSource image, Func<string, bool> copyImageFile)
    {
        var w = new MarkupEditor(image, copyImageFile);
        w.Show();
        w.Activate();
    }

    // ------------------------------------------------------------------ drawing

    private double Stroke => Math.Max(3, Math.Round(Math.Max(_image.PixelWidth, _image.PixelHeight) / 300.0));

    private void Begin(object sender, MouseButtonEventArgs e)
    {
        _from = e.GetPosition(_ink);
        _drawing = _tool switch
        {
            Tool.Pen => new Polyline { Stroke = _color, StrokeThickness = Stroke, StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, Points = { _from } },
            Tool.Marker => new Polyline { Stroke = _color, StrokeThickness = Stroke * 5, Opacity = 0.4, StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Square, StrokeEndLineCap = PenLineCap.Square, Points = { _from } },
            Tool.Frame => new Rectangle { Stroke = _color, StrokeThickness = Stroke, RadiusX = Stroke, RadiusY = Stroke },
            _ => new System.Windows.Shapes.Path { Stroke = _color, Fill = _color, StrokeThickness = Stroke, StrokeLineJoin = PenLineJoin.Round, StrokeEndLineCap = PenLineCap.Round },
        };
        _ink.Children.Add(_drawing);
        _ink.CaptureMouse();
        Drag(sender, e);
    }

    private void Drag(object sender, MouseEventArgs e)
    {
        if (_drawing is null) return;
        var to = e.GetPosition(_ink);
        switch (_drawing)
        {
            case Polyline line:
                line.Points.Add(to);
                break;
            case Rectangle frame:
                Canvas.SetLeft(frame, Math.Min(_from.X, to.X));
                Canvas.SetTop(frame, Math.Min(_from.Y, to.Y));
                frame.Width = Math.Abs(to.X - _from.X);
                frame.Height = Math.Abs(to.Y - _from.Y);
                break;
            case System.Windows.Shapes.Path arrow:
                arrow.Data = Arrow(_from, to, Stroke * 4);
                break;
        }
    }

    private void End(object sender, MouseButtonEventArgs e)
    {
        if (_drawing is null) return;
        _ink.ReleaseMouseCapture();
        var to = e.GetPosition(_ink);
        // A click without a drag leaves nothing worth keeping (except a pen dot).
        if (_drawing is not Polyline && (to - _from).Length < 4) _ink.Children.Remove(_drawing);
        else _done.Push(_drawing);
        _drawing = null;
    }

    private static Geometry Arrow(Point from, Point to, double head)
    {
        var v = to - from;
        if (v.Length < 1) return Geometry.Empty;
        v.Normalize();
        var n = new Vector(-v.Y, v.X);
        var baseCentre = to - v * head;
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(from, isFilled: false, isClosed: false);
            c.LineTo(baseCentre, isStroked: true, isSmoothJoin: true);
            c.BeginFigure(to, isFilled: true, isClosed: true);
            c.LineTo(baseCentre + n * head * 0.6, true, true);
            c.LineTo(baseCentre - n * head * 0.6, true, true);
        }
        g.Freeze();
        return g;
    }

    private void Undo()
    {
        if (_done.TryPop(out var last)) _ink.Children.Remove(last);
    }

    // ------------------------------------------------------------------ export

    /// <summary>The picture with the markup, at its own pixel size.</summary>
    private BitmapSource Render()
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
            dc.DrawRectangle(new VisualBrush(_surface) { Stretch = Stretch.Fill }, null, new Rect(0, 0, _image.PixelWidth, _image.PixelHeight));
        var rtb = new RenderTargetBitmap(_image.PixelWidth, _image.PixelHeight, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        rtb.Freeze();
        return rtb;
    }

    private string WritePng(string path)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(Render()));
        using (var fs = File.Create(path)) enc.Save(fs);
        return path;
    }

    private bool Copy()
    {
        var tmp = WritePng(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Stillbar", "snip.png"));
        bool ok = _copy(tmp);
        _status.Text = ok ? "" : "Буфер обмена занят. Попробуйте ещё раз";
        return ok;
    }

    private void Save()
    {
        var dir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Stillbar");
        var path = WritePng(System.IO.Path.Combine(dir, $"Снимок {DateTime.Now:yyyy-MM-dd HH-mm-ss}.png"));
        _status.Text = $"Сохранено: {System.IO.Path.GetFileName(path)}";
        Log.Info("screen", $"markup saved to {path}");
    }

    // ------------------------------------------------------------------ small parts

    private static Button TextButton(string text, Action click, string tip)
    {
        var b = new Button { Style = Tokens.Style("TextButton"), Content = text, Margin = new Thickness(8, 0, 0, 0), ToolTip = tip, VerticalAlignment = VerticalAlignment.Center };
        b.Click += (_, _) => click();
        return b;
    }

    private static ControlTemplate SwatchTemplate()
    {
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(11));
        border.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding(nameof(Background)) { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        border.SetValue(Border.BorderThicknessProperty, new Thickness(2));
        border.SetValue(Border.BorderBrushProperty, Tokens.Brush("Brush.SurfaceActive"));
        return new ControlTemplate(typeof(Button)) { VisualTree = border };
    }
}
