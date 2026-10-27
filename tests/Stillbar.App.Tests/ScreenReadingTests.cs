using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Stillbar.App.Services;
using ZXing;
using ZXing.QrCode;
using ZXing.Rendering;

namespace Stillbar.App.Tests;

/// <summary>A-H2 / A-H3: the real recognisers on pictures we draw ourselves (no screen, no clipboard).</summary>
[Collection("wpf")]
public class ScreenReadingTests(WpfHost host)
{
    private readonly ScreenActions _screen = new();

    [Fact]
    public Task A_H3_reads_a_qr_code() => host.RunAsync(() =>
    {
        var writer = new BarcodeWriterPixelData
        {
            Format = BarcodeFormat.QR_CODE,
            // UTF-8 with an ECI marker, as real generators do; ZXing's default ISO-8859-1 would garble the Cyrillic.
            Options = new QrCodeEncodingOptions { Width = 240, Height = 240, Margin = 2, CharacterSet = "UTF-8" },
        };
        PixelData qr = writer.Write("https://example.com/полоса?x=1");
        var image = BitmapSource.Create(qr.Width, qr.Height, 96, 96, PixelFormats.Bgra32, null, qr.Pixels, qr.Width * 4);
        Assert.Equal("https://example.com/полоса?x=1", _screen.DecodeQr(image));
    });

    [Fact]
    public Task A_E3_no_qr_code_is_null() => host.RunAsync(() =>
        Assert.Null(_screen.DecodeQr(Draw("no code here", 400, 120))));

    [Fact]
    public Task A_H2_reads_text_from_a_picture() => host.RunAsync(async () =>
    {
        var text = await _screen.RecognizeTextAsync(Draw("Stillbar 2026", 600, 140));
        Assert.Contains("2026", text);
    });

    private static BitmapSource Draw(string text, int w, int h)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, w, h));
            var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI"), 48, Brushes.Black, 1.0);
            dc.DrawText(ft, new Point(20, (h - ft.Height) / 2));
        }
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        rtb.Freeze();
        return rtb;
    }
}
