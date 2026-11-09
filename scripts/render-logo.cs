// Makes the logo PNGs from their SVG source in assets/logo, or checks that the PNGs in the
// repository still match what their source makes. Runs as a .NET file-based app.
//
// Run it from the repository root:
//   dotnet run scripts/render-logo.cs            writes every PNG
//   dotnet run scripts/render-logo.cs -- --check  fails when a PNG differs from its source
//
// The check compares pixels, not bytes, so a different PNG encoder on another platform
// does not fail it. Anti-aliased edges may come out a few levels apart on another
// platform, so a few channel values may differ slightly; any wider change fails.

#:package Svg.Skia@5.2.3
#:package SkiaSharp@4.148.0
#:package SkiaSharp.NativeAssets.Linux.NoDependencies@4.148.0
#:property PublishAot=false

using SkiaSharp;
using Svg.Skia;

// At most this many levels apart, in at most this share of the channel values.
const int Tolerance = 8;
const double TolerantShare = 0.002;

(string Source, string Png)[] images =
[
    ("assets/logo/catalog.svg", "src/Jellyfin.Plugin.MediaReports/media-reports.png"),
    ("assets/logo/social-preview.svg", "assets/logo/social-preview.png"),
];

var check = args is ["--check"];
if (args.Length > 0 && !check)
{
    Console.Error.WriteLine("usage: dotnet run scripts/render-logo.cs [-- --check]");
    return 2;
}

var failures = 0;
foreach (var (source, png) in images)
{
    using var rendered = Render(source);
    if (!check)
    {
        using var data = rendered.Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(png, data.ToArray());
        Console.WriteLine($"{png} {rendered.Width}x{rendered.Height}");
        continue;
    }

    var difference = Compare(rendered, png);
    if (difference is null)
    {
        Console.WriteLine($"{png} matches {source}");
    }
    else
    {
        Console.Error.WriteLine($"FAIL: {png} {difference}; run dotnet run scripts/render-logo.cs");
        failures++;
    }
}

return failures == 0 ? 0 : 1;

static SKBitmap Render(string svgPath)
{
    using var svg = new SKSvg();
    var picture = svg.Load(svgPath) ?? throw new InvalidDataException($"{svgPath} could not be read");
    var bounds = picture.CullRect;
    var bitmap = new SKBitmap(new SKImageInfo((int)bounds.Width, (int)bounds.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
    using var canvas = new SKCanvas(bitmap);
    canvas.Clear(SKColors.Transparent);
    canvas.DrawPicture(picture);
    canvas.Flush();
    return bitmap;
}

static string? Compare(SKBitmap rendered, string pngPath)
{
    if (!File.Exists(pngPath))
    {
        return "is missing";
    }

    using var decoded = SKBitmap.Decode(pngPath);
    if (decoded is null)
    {
        return "is not a PNG";
    }

    using var stored = decoded.Copy(SKColorType.Rgba8888);
    if (stored.Width != rendered.Width || stored.Height != rendered.Height)
    {
        return $"is {stored.Width}x{stored.Height}, its source makes {rendered.Width}x{rendered.Height}";
    }

    var expected = rendered.GetPixelSpan();
    var actual = stored.GetPixelSpan();
    var worst = 0;
    var differing = 0;
    for (var i = 0; i < expected.Length; i++)
    {
        var delta = Math.Abs(expected[i] - actual[i]);
        worst = Math.Max(worst, delta);
        if (delta > 0)
        {
            differing++;
        }
    }

    return worst <= Tolerance && differing <= expected.Length * TolerantShare
        ? null
        : $"differs from its source in {differing} channel value(s), by up to {worst}";
}
