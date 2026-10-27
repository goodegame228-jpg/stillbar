using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Stillbar.App.Shell;

internal static class ArtSampler
{
    /// <summary>
    /// Draws any ImageSource (bitmap cover or vector) into a tiny BGRA buffer for colour analysis.
    /// 32x32 is enough to find the dominant hue and costs nothing. UI thread only.
    /// </summary>
    public static byte[] Bgra(ImageSource source, int size = 32)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
            dc.DrawImage(source, new Rect(0, 0, size, size));

        var rtb = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        var pixels = new byte[size * size * 4];
        rtb.CopyPixels(pixels, size * 4, 0);
        return pixels;
    }
}
