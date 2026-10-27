using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Stillbar.App.Design;
using Stillbar.App.Shell;
using Stillbar.Core.Capture;
using Stillbar.Core.Display;

namespace Stillbar.App.Capture;

/// <summary>
/// Full-screen colour picker: a frozen picture of the monitor with a loupe (11×11 pixels, magnified) that follows
/// the cursor and shows the colour under it. Click takes the colour, Esc or right-click cancels.
/// </summary>
internal sealed class ColorPicker : Window
{
    private const int Cells = 11;
    private const int Zoom = 12;

    private readonly BitmapSource _shot;
    private readonly PixelRect _area;
    private readonly byte[] _pixels;
    private readonly Image _loupeImage = new() { Width = Cells * Zoom, Height = Cells * Zoom, Stretch = Stretch.Fill };
    private readonly TextBlock _hex;
    private readonly Border _swatch;
    private readonly Canvas _canvas = new();
    private readonly Border _loupe;
    private readonly TaskCompletionSource<string?> _result = new();

    private ColorPicker(BitmapSource shot, PixelRect area)
    {
        _shot = shot.Format == PixelFormats.Bgra32 ? shot : new FormatConvertedBitmap(shot, PixelFormats.Bgra32, null, 0);
        _area = area;
        _pixels = new byte[_shot.PixelWidth * _shot.PixelHeight * 4];
        _shot.CopyPixels(_pixels, _shot.PixelWidth * 4, 0);

        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        Cursor = Cursors.Cross;
        Title = "Цвет с экрана";
        RenderOptions.SetBitmapScalingMode(_loupeImage, BitmapScalingMode.NearestNeighbor);

        _hex = new TextBlock { Style = Tokens.Style("Text.Pill"), Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        _swatch = new Border { Width = 18, Height = 18, CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(1), BorderBrush = Tokens.Brush("Brush.Track") };
        var reticle = new Border
        {
            Width = Zoom + 2, Height = Zoom + 2, BorderThickness = new Thickness(1.5), BorderBrush = Tokens.Brush("Brush.Control"),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };
        _loupe = new Border
        {
            Background = Tokens.Brush("Brush.Bg"),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(6),
            IsHitTestVisible = false,
            Child = new StackPanel
            {
                Children =
                {
                    new Border { CornerRadius = new CornerRadius(8), ClipToBounds = true, Child = new Grid { Children = { _loupeImage, reticle } } },
                    new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4, 8, 4, 2), Children = { _swatch, _hex } },
                },
            },
        };
        _canvas.Children.Add(_loupe);
        Content = new Grid { Children = { new Image { Source = _shot, Stretch = Stretch.Fill }, _canvas } };

        SourceInitialized += (_, _) =>
        {
            // Cover exactly the monitor, in physical pixels (the picture is 1:1 with it).
            var hwnd = new WindowInteropHelper(this).Handle;
            NativeWindow.Cover(hwnd, area.Left, area.Top, area.Right - area.Left, area.Bottom - area.Top);
        };
        MouseMove += (_, e) => Follow(e.GetPosition(this));
        MouseLeftButtonUp += (_, e) => Finish(ColorAt(e.GetPosition(this)) is var (r, g, b) ? ColorText.Hex(r, g, b) : null);
        MouseRightButtonUp += (_, _) => Finish(null);
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Finish(null); };
        Deactivated += (_, _) => Finish(null);
    }

    /// <summary>Shows the picker over the given monitor area; the hex of the clicked colour, or null if cancelled.</summary>
    public static Task<string?> PickAsync(BitmapSource shot, PixelRect area)
    {
        var w = new ColorPicker(shot, area);
        w.Show();
        w.Activate();
        return w._result.Task;
    }

    private (int X, int Y) ToPixel(Point p) =>
        ((int)Math.Clamp(p.X * _shot.PixelWidth / Math.Max(1, ActualWidth), 0, _shot.PixelWidth - 1),
         (int)Math.Clamp(p.Y * _shot.PixelHeight / Math.Max(1, ActualHeight), 0, _shot.PixelHeight - 1));

    private (byte R, byte G, byte B)? ColorAt(Point p)
    {
        var (x, y) = ToPixel(p);
        int i = (y * _shot.PixelWidth + x) * 4;
        return i + 2 < _pixels.Length ? (_pixels[i + 2], _pixels[i + 1], _pixels[i]) : null;
    }

    private void Follow(Point p)
    {
        var (x, y) = ToPixel(p);
        int half = Cells / 2;
        int left = Math.Clamp(x - half, 0, Math.Max(0, _shot.PixelWidth - Cells));
        int top = Math.Clamp(y - half, 0, Math.Max(0, _shot.PixelHeight - Cells));
        _loupeImage.Source = new CroppedBitmap(_shot, new Int32Rect(left, top, Math.Min(Cells, _shot.PixelWidth), Math.Min(Cells, _shot.PixelHeight)));

        if (ColorAt(p) is var (r, g, b))
        {
            _hex.Text = ColorText.Hex(r, g, b);
            _swatch.Background = new SolidColorBrush(Color.FromRgb(r, g, b));
        }

        // Down and to the right of the cursor; flipped near the edges so it never leaves the screen.
        _loupe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = _loupe.DesiredSize;
        double lx = p.X + 24 + size.Width > ActualWidth ? p.X - 24 - size.Width : p.X + 24;
        double ly = p.Y + 24 + size.Height > ActualHeight ? p.Y - 24 - size.Height : p.Y + 24;
        Canvas.SetLeft(_loupe, lx);
        Canvas.SetTop(_loupe, ly);
    }

    private void Finish(string? hex)
    {
        if (_result.Task.IsCompleted) return;
        _result.TrySetResult(hex);
        Close();
    }
}
