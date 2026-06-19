#:package SkiaSharp@2.88.9
// Generates apps/Voxa.Studio/Assets/voxa.ico from the brand geometry (mirrors Assets/voxa-icon.svg).
// Run from the repo root:  dotnet run tools/voxa-icon-gen.cs
using SkiaSharp;

int[] sizes = { 16, 32, 48, 64, 128, 256 };
var accent = SKColor.Parse("#4FC3F7");
var bgColor = SKColor.Parse("#11161D");

var pngs = new List<byte[]>();
foreach (var size in sizes)
{
    using var bmp = new SKBitmap(size, size, SKColorType.Bgra8888, SKAlphaType.Premul);
    using (var canvas = new SKCanvas(bmp))
    {
        canvas.Clear(SKColors.Transparent);
        canvas.Scale(size / 96f); // the source art is a 96×96 viewBox

        using (var bg = new SKPaint { Color = bgColor, IsAntialias = true, Style = SKPaintStyle.Fill })
            canvas.DrawRoundRect(new SKRect(0, 0, 96, 96), 22, 22, bg);

        // The V mark + three waveform bars: group transform translate(17,18) scale(0.62).
        canvas.Save();
        canvas.Translate(17, 18);
        canvas.Scale(0.62f);
        using (var stroke = new SKPaint
        {
            Color = accent, IsAntialias = true, Style = SKPaintStyle.Stroke,
            StrokeWidth = 7, StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round,
        })
        using (var v = new SKPath())
        {
            v.MoveTo(14, 18); v.LineTo(50, 86); v.LineTo(86, 18);
            canvas.DrawPath(v, stroke);
        }
        using (var fill = new SKPaint { Color = accent, IsAntialias = true, Style = SKPaintStyle.Fill })
        {
            canvas.DrawRoundRect(new SKRect(38, 34, 45, 56), 3.5f, 3.5f, fill);
            canvas.DrawRoundRect(new SKRect(50, 26, 57, 56), 3.5f, 3.5f, fill);
            canvas.DrawRoundRect(new SKRect(62, 38, 69, 56), 3.5f, 3.5f, fill);
        }
        canvas.Restore();
    }

    using var img = SKImage.FromBitmap(bmp);
    using var data = img.Encode(SKEncodedImageFormat.Png, 100);
    pngs.Add(data.ToArray());
}

// Pack the PNGs into an ICO (PNG-compressed entries — Windows Vista+).
var outPath = Path.Combine("apps", "Voxa.Studio", "Assets", "voxa.ico");
using var fs = File.Create(outPath);
using var w = new BinaryWriter(fs);
w.Write((short)0);             // reserved
w.Write((short)1);             // type: icon
w.Write((short)sizes.Length);  // image count
var offset = 6 + 16 * sizes.Length;
for (var i = 0; i < sizes.Length; i++)
{
    var dim = sizes[i];
    w.Write((byte)(dim >= 256 ? 0 : dim)); // width  (0 means 256)
    w.Write((byte)(dim >= 256 ? 0 : dim)); // height
    w.Write((byte)0);   // palette colours
    w.Write((byte)0);   // reserved
    w.Write((short)1);  // colour planes
    w.Write((short)32); // bits per pixel
    w.Write(pngs[i].Length);
    w.Write(offset);
    offset += pngs[i].Length;
}
foreach (var png in pngs) w.Write(png);

Console.WriteLine($"Wrote {outPath} — {sizes.Length} sizes, {fs.Length} bytes");
