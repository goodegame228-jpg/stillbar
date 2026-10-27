using System.Windows;
using System.Windows.Media;
using Stillbar.Core.Media;

namespace Stillbar.App.Services;

/// <summary>Fixed, made-up track for --snapshot renders, so screenshots do not depend on what is playing.</summary>
public sealed class SampleMediaService : IMediaService
{
    public SampleMediaService(bool playing = true, bool empty = false)
    {
        Current = empty
            ? MediaSnapshot.None
            : new MediaSnapshot("Тихая гавань", "Северный ветер", "Маяки", playing,
                TimeSpan.FromSeconds(86), TimeSpan.FromSeconds(214), DateTimeOffset.Now, "sample");
        Artwork = empty ? null : MakeArt();
    }

    /// <summary>The pink/navy cover used to check the art tint in snapshots.</summary>
    public static SampleMediaService Pink() => new(MakeArt(Color.FromRgb(0xF2, 0x6A, 0x8D), Color.FromRgb(0xC2, 0x2E, 0x6A), Color.FromRgb(0x2A, 0x1B, 0x5E)));

    private SampleMediaService(ImageSource art)
    {
        Current = new MediaSnapshot("Огни над водой", "Полночный трамвай", "Город", true,
            TimeSpan.FromSeconds(48), TimeSpan.FromSeconds(228), DateTimeOffset.Now, "sample");
        Artwork = art;
    }

    public MediaSnapshot Current { get; }
    public ImageSource? Artwork { get; }
    public event EventHandler? Changed { add { } remove { } }

    public Task StartAsync() => Task.CompletedTask;
    public Task PlayPauseAsync() => Task.CompletedTask;
    public Task NextAsync() => Task.CompletedTask;
    public Task PreviousAsync() => Task.CompletedTask;
    public Task SeekAsync(TimeSpan position) => Task.CompletedTask;

    private static ImageSource MakeArt() =>
        MakeArt(Color.FromRgb(0x5B, 0xE0, 0xC2), Color.FromRgb(0x1F, 0x8A, 0x7A), Color.FromRgb(0x1B, 0x2F, 0x6B));

    private static ImageSource MakeArt(Color inner, Color mid, Color outer)
    {
        var brush = new RadialGradientBrush(
            new GradientStopCollection
            {
                new(inner, 0),
                new(mid, 0.45),
                new(outer, 1),
            })
        { GradientOrigin = new Point(0.3, 0.3), Center = new Point(0.4, 0.4), RadiusX = 0.9, RadiusY = 0.9 };
        var img = new DrawingImage(new GeometryDrawing(brush, null, new RectangleGeometry(new Rect(0, 0, 96, 96))));
        img.Freeze();
        return img;
    }
}
