using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using ZXing;
using ZXing.Common;

namespace Stillbar.App.Services;

public interface IScreenActions
{
    /// <summary>Opens Windows' own area-snip overlay; the result lands in the clipboard.</summary>
    void SnipArea();

    /// <summary>Copies the primary monitor to the clipboard. False if the clipboard was busy.</summary>
    bool CopyPrimaryScreen();

    /// <summary>Opens the snip overlay and waits for the picture it puts in the clipboard. Null if the user cancelled.</summary>
    Task<BitmapSource?> SnipImageAsync(CancellationToken ct = default);

    /// <summary>Text in the picture, lines joined with \n. Empty if none (or no OCR language is installed).</summary>
    Task<string> RecognizeTextAsync(BitmapSource image);

    /// <summary>Contents of the first QR code in the picture, or null.</summary>
    string? DecodeQr(BitmapSource image);

    /// <summary>False if the clipboard stayed busy.</summary>
    bool SetClipboardText(string text);

    /// <summary>Full-screen picker over the bar's monitor: the clicked colour as #RRGGBB, or null if cancelled.</summary>
    Task<string?> PickColorAsync() => Task.FromResult<string?>(null);

    /// <summary>Opens the markup editor on a snip (its own window; copy or save from there).</summary>
    void OpenMarkup(BitmapSource image) { }

    /// <summary>"Без формата": the clipboard's text put back as plain text. Its length, or null if there was no text (or the clipboard was busy).</summary>
    int? StripClipboardFormatting() => null;

    /// <summary>The text in the clipboard, or null (no text, or busy).</summary>
    string? GetClipboardText() => null;

    /// <summary>Opens Windows' voice typing (Win+H) in the window the user is typing in. False if a policy turns it off.</summary>
    bool StartVoiceTyping() => true;

    /// <summary>Types text where the cursor is (dictation).</summary>
    void TypeText(string text) { }
}

public sealed partial class ScreenActions(Func<Stillbar.Core.Display.PixelRect>? monitorArea = null) : IScreenActions
{
    private static readonly TimeSpan SnipTimeout = TimeSpan.FromSeconds(45);

    public async Task<string?> PickColorAsync()
    {
        var area = monitorArea?.Invoke() ?? new Stillbar.Core.Display.PixelRect(0, 0, GetSystemMetrics(SM_CXSCREEN), GetSystemMetrics(SM_CYSCREEN));
        var shot = Grab(area.Left, area.Top, area.Right - area.Left, area.Bottom - area.Top);
        return await Stillbar.App.Capture.ColorPicker.PickAsync(shot, area);
    }

    public void OpenMarkup(BitmapSource image) =>
        Stillbar.App.Capture.MarkupEditor.Open(image, file => Retry(() => Clipboard.SetImage(ClipboardService.LoadPng(file))));

    /// <summary>
    /// Windows' voice typing needs the speech services; a policy (AllowInputPersonalization = 0, set by an
    /// administrator or management software) turns them off, and Win+H then only shows «управляет ваша организация».
    /// Read-only check; the policy is the owner's to change, not ours.
    /// </summary>
    public void TypeText(string text) => Keys.Type(text);

    public bool StartVoiceTyping()
    {
        if (SpeechBlockedByPolicy())
        {
            Log.Info("voice", "voice typing is turned off by policy (AllowInputPersonalization = 0)");
            return false;
        }
        Keys.WinH();
        return true;
    }

    private static bool SpeechBlockedByPolicy()
    {
        foreach (var root in new[] { Microsoft.Win32.Registry.LocalMachine, Microsoft.Win32.Registry.CurrentUser })
        {
            using var key = root.OpenSubKey(@"SOFTWARE\Policies\Microsoft\InputPersonalization");
            if (key?.GetValue("AllowInputPersonalization") is int v && v == 0) return true;
        }
        return false;
    }

    public string? GetClipboardText()
    {
        try { return Clipboard.ContainsText() ? Clipboard.GetText() : null; }
        catch (COMException) { return null; }
    }

    public int? StripClipboardFormatting()
    {
        try
        {
            if (!Clipboard.ContainsText()) return null;
            var text = Clipboard.GetText();
            return Retry(() => Clipboard.SetText(text)) ? text.Length : null;
        }
        catch (COMException) { return null; }
    }

    public void SnipArea() => Process.Start(new ProcessStartInfo("ms-screenclip:") { UseShellExecute = true });

    public bool CopyPrimaryScreen()
    {
        var image = Grab(0, 0, GetSystemMetrics(SM_CXSCREEN), GetSystemMetrics(SM_CYSCREEN));
        return Retry(() => Clipboard.SetImage(image));
    }

    /// <summary>A rectangle of the desktop in physical pixels (the process is per-monitor DPI aware).</summary>
    private static BitmapSource Grab(int x, int y, int w, int h)
    {
        nint screen = GetDC(0);
        nint mem = CreateCompatibleDC(screen);
        nint bmp = CreateCompatibleBitmap(screen, w, h);
        nint old = SelectObject(mem, bmp);
        try
        {
            BitBlt(mem, 0, 0, w, h, screen, x, y, SRCCOPY | CAPTUREBLT);
            SelectObject(mem, old);
            var image = Imaging.CreateBitmapSourceFromHBitmap(bmp, 0, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            image.Freeze();
            return image;
        }
        finally
        {
            DeleteObject(bmp);
            DeleteDC(mem);
            ReleaseDC(0, screen);
        }
    }

    public async Task<BitmapSource?> SnipImageAsync(CancellationToken ct = default)
    {
        // The clipboard's sequence number changes on every write: wait for the snip tool's write, not an old picture.
        uint before = GetClipboardSequenceNumber();
        SnipArea();
        var deadline = DateTime.UtcNow + SnipTimeout;
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(250, ct);
            if (GetClipboardSequenceNumber() == before) continue;
            try
            {
                if (!Clipboard.ContainsImage()) { before = GetClipboardSequenceNumber(); continue; } // something else copied text
                var img = Clipboard.GetImage();
                if (img is null) continue;
                var frozen = new FormatConvertedBitmap(img, PixelFormats.Bgra32, null, 0);
                frozen.Freeze();
                return frozen;
            }
            catch (COMException)
            {
                // the snip tool is still writing; try on the next round
            }
        }
        Log.Info("screen", "snip timed out or was cancelled");
        return null;
    }

    public async Task<string> RecognizeTextAsync(BitmapSource image)
    {
        var engine = OcrEngine.TryCreateFromUserProfileLanguages()
                     ?? OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language("en-US"));
        if (engine is null)
        {
            Log.Warn("screen", "no OCR language installed");
            return "";
        }

        // OCR refuses pictures above MaxImageDimension; shrink large ones, and enlarge tiny ones (small UI text reads better).
        double scale = Math.Min(1.0, OcrEngine.MaxImageDimension / (double)Math.Max(image.PixelWidth, image.PixelHeight));
        if (Math.Max(image.PixelWidth, image.PixelHeight) < 600) scale = Math.Min(2.0, OcrEngine.MaxImageDimension / (double)Math.Max(image.PixelWidth, image.PixelHeight));
        BitmapSource src = Math.Abs(scale - 1) > 0.01 ? new TransformedBitmap(image, new ScaleTransform(scale, scale)) : image;

        var (pixels, w, h) = Bgra(src);
        using var bitmap = new SoftwareBitmap(BitmapPixelFormat.Bgra8, w, h, BitmapAlphaMode.Premultiplied);
        bitmap.CopyFromBuffer(pixels.AsBuffer());
        var result = await engine.RecognizeAsync(bitmap);
        Log.Info("screen", $"OCR ({engine.RecognizerLanguage.LanguageTag}): {result.Lines.Count} lines");
        return string.Join("\n", result.Lines.Select(l => l.Text)).Trim();
    }

    public string? DecodeQr(BitmapSource image)
    {
        var (pixels, w, h) = Bgra(image);
        var reader = new BarcodeReaderGeneric
        {
            AutoRotate = true,
            Options = new DecodingOptions { PossibleFormats = [BarcodeFormat.QR_CODE], TryHarder = true },
        };
        return reader.Decode(new RGBLuminanceSource(pixels, w, h, RGBLuminanceSource.BitmapFormat.BGRA32))?.Text;
    }

    public bool SetClipboardText(string text) => Retry(() => Clipboard.SetText(text));

    private static (byte[] Pixels, int W, int H) Bgra(BitmapSource image)
    {
        var bgra = image.Format == PixelFormats.Bgra32 ? image : new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        int w = bgra.PixelWidth, h = bgra.PixelHeight;
        var px = new byte[w * h * 4];
        bgra.CopyPixels(px, w * 4, 0);
        return (px, w, h);
    }

    private static bool Retry(Action clipboardWrite)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                clipboardWrite();
                return true;
            }
            catch (COMException)
            {
                Thread.Sleep(50); // another app holds the clipboard for a moment
            }
        }
        return false;
    }

    private const int SM_CXSCREEN = 0, SM_CYSCREEN = 1;
    private const uint SRCCOPY = 0x00CC0020, CAPTUREBLT = 0x40000000;

    [LibraryImport("user32.dll")] private static partial uint GetClipboardSequenceNumber();
    [LibraryImport("user32.dll")] private static partial int GetSystemMetrics(int index);
    [LibraryImport("user32.dll")] private static partial nint GetDC(nint hwnd);
    [LibraryImport("user32.dll")] private static partial int ReleaseDC(nint hwnd, nint dc);
    [LibraryImport("gdi32.dll")] private static partial nint CreateCompatibleDC(nint dc);
    [LibraryImport("gdi32.dll")] private static partial nint CreateCompatibleBitmap(nint dc, int w, int h);
    [LibraryImport("gdi32.dll")] private static partial nint SelectObject(nint dc, nint obj);
    [LibraryImport("gdi32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static partial bool BitBlt(nint dst, int x, int y, int w, int h, nint src, int sx, int sy, uint rop);
    [LibraryImport("gdi32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static partial bool DeleteObject(nint obj);
    [LibraryImport("gdi32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static partial bool DeleteDC(nint dc);
}

public sealed class NoScreenActions : IScreenActions
{
    public void SnipArea() { }
    public bool CopyPrimaryScreen() => true;
    public Task<BitmapSource?> SnipImageAsync(CancellationToken ct = default) => Task.FromResult<BitmapSource?>(null);
    public Task<string> RecognizeTextAsync(BitmapSource image) => Task.FromResult("");
    public string? DecodeQr(BitmapSource image) => null;
    public bool SetClipboardText(string text) => true;
}
