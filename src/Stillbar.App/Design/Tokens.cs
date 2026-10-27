using System.Windows;
using System.Windows.Media;
using Stillbar.Core.Media;

namespace Stillbar.App.Design;

/// <summary>Read design tokens from code (view models that compute brushes). Keys as in Tokens.xaml.</summary>
public static class Tokens
{
    public static Color Color(string key) => (Color)Application.Current.FindResource(key);

    public static Rgb Rgb(string key)
    {
        var c = Color(key);
        return new Rgb(c.R, c.G, c.B);
    }

    public static Brush Brush(string key) => (Brush)Application.Current.FindResource(key);

    public static Geometry Geometry(string key) => (Geometry)Application.Current.FindResource(key);

    public static Style Style(string key) => (Style)Application.Current.FindResource(key);

    public static double Number(string key) => (double)Application.Current.FindResource(key);

    public static SolidColorBrush Solid(Rgb c)
    {
        var b = new SolidColorBrush(System.Windows.Media.Color.FromRgb(c.R, c.G, c.B));
        b.Freeze();
        return b;
    }
}
