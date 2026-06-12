using SkiaSharp;

namespace Voxa.Studio.Tests;

/// <summary>
/// Not assertions — the brand asset pipeline (VST-002 brand reach): VOXA_BRAND_EXPORT=1
/// regenerates the committed raster assets from the same geometry as <c>voxa-icon.svg</c>, so
/// the NuGet icon is reproducible from code instead of being a binary nobody can re-make.
/// Skipped (trivially green) otherwise.
/// </summary>
public class BrandAssetExportTests
{
    [Fact]
    public void Export_NuGet_Icon_When_Requested()
    {
        if (Environment.GetEnvironmentVariable("VOXA_BRAND_EXPORT") != "1") return;

        // Walk up to the repo root (the dir holding Voxa.slnx) from the test output dir.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Voxa.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        var path = Path.Combine(dir!.FullName, "assets", "voxa-icon.png");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        // NuGet's recommended 128×128, drawn in voxa-icon.svg's 96-unit art space.
        const int size = 128;
        using var bitmap = new SKBitmap(size, size, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);
        canvas.Scale(size / 96f);

        // The app-icon tile: panel ink, ~23% radius, hairline edge (corners stay transparent).
        using (var tile = new SKPaint { Color = SKColor.Parse("#11161D"), IsAntialias = true })
            canvas.DrawRoundRect(new SKRect(0, 0, 96, 96), 22, 22, tile);
        using (var edge = new SKPaint
               {
                   Color = new SKColor(0xA6, 0xB2, 0xC2, 0x29), IsAntialias = true,
                   Style = SKPaintStyle.Stroke, StrokeWidth = 1,
               })
            canvas.DrawRoundRect(new SKRect(0.5f, 0.5f, 95.5f, 95.5f), 21.5f, 21.5f, edge);

        // The mark, exactly as the SVG places it: translate(17,18) scale(0.62) of the 100-art.
        canvas.Translate(17, 18);
        canvas.Scale(0.62f);
        var cyan = SKColor.Parse("#4FC3F7");
        using (var stroke = new SKPaint
               {
                   Color = cyan, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 7,
                   StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round,
               })
        {
            using var v = new SKPath();
            v.MoveTo(14, 18); v.LineTo(50, 86); v.LineTo(86, 18);
            canvas.DrawPath(v, stroke);
        }
        using (var fill = new SKPaint { Color = cyan, IsAntialias = true })
        {
            canvas.DrawRoundRect(new SKRect(38, 34, 45, 56), 3.5f, 3.5f, fill); // bars: 22/30/18
            canvas.DrawRoundRect(new SKRect(50, 26, 57, 56), 3.5f, 3.5f, fill);
            canvas.DrawRoundRect(new SKRect(62, 38, 69, 56), 3.5f, 3.5f, fill);
        }

        using var image = SKImage.FromBitmap(bitmap);
        using var png = image.Encode(SKEncodedImageFormat.Png, 100);
        using var file = File.Create(path);
        png.SaveTo(file);
    }
}
