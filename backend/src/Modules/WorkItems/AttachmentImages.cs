using SkiaSharp;

namespace Aictiq.Modules.WorkItems;

/// <summary>
/// Re-encodes uploaded images before they are stored: WebP, no wider than
/// <see cref="AttachmentsOptions.MaxImageWidth"/>, EXIF orientation applied. A pasted
/// screenshot shrinks to a fraction of its PNG, and a re-encode keeps only the pixels -
/// a phone photo's GPS position and other metadata do not survive it.
/// </summary>
internal static class AttachmentImages
{
    private static readonly HashSet<string> Raster = new(StringComparer.OrdinalIgnoreCase)
        { "image/png", "image/jpeg", "image/webp", "image/gif" };

    public const string WebpContentType = "image/webp";

    public static bool IsImage(string contentType) => Raster.Contains(contentType);

    public abstract record Outcome;
    public sealed record Encoded(byte[] Bytes, int Width, int Height) : Outcome;
    /// <summary>An animated GIF: re-encoding would keep only its first frame, so it is stored as uploaded.</summary>
    public sealed record KeepOriginal : Outcome;
    public sealed record Rejected(string Reason) : Outcome;

    public static Outcome Process(byte[] source, AttachmentsOptions policy)
    {
        using var data = SKData.CreateCopy(source);
        using var codec = SKCodec.Create(data);
        if (codec is null) return new Rejected("The file is not a readable image.");
        if ((long)codec.Info.Width * codec.Info.Height > policy.MaxImagePixels)
            return new Rejected($"Images may have at most {policy.MaxImagePixels / 1_000_000} megapixels.");
        if (codec.FrameCount > 1) return new KeepOriginal();

        using var bitmap = SKBitmap.Decode(codec);
        if (bitmap is null) return new Rejected("The file is not a readable image.");

        var origin = codec.EncodedOrigin;
        var swaps = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        var (width, height) = swaps ? (bitmap.Height, bitmap.Width) : (bitmap.Width, bitmap.Height);
        var scale = Math.Min(1f, (float)policy.MaxImageWidth / width);
        var targetWidth = Math.Max(1, (int)Math.Round(width * scale));
        var targetHeight = Math.Max(1, (int)Math.Round(height * scale));

        using var surface = SKSurface.Create(new SKImageInfo(targetWidth, targetHeight, SKColorType.Rgba8888, SKAlphaType.Premul));
        if (surface is null) return new Rejected("The image could not be processed.");
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);
        // Canvas calls compose right-to-left over the points: the scale is applied last.
        canvas.Scale(scale);
        Orient(canvas, origin, width, height);
        using var image = SKImage.FromBitmap(bitmap);
        canvas.DrawImage(image, 0, 0, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
        canvas.Flush();

        using var snapshot = surface.Snapshot();
        using var encoded = snapshot.Encode(SKEncodedImageFormat.Webp, policy.WebpQuality);
        return encoded is null
            ? new Rejected("The image could not be processed.")
            : new Encoded(encoded.ToArray(), targetWidth, targetHeight);
    }

    /// <summary>Maps the stored pixels onto an upright canvas of <paramref name="width"/> × <paramref name="height"/>.</summary>
    private static void Orient(SKCanvas canvas, SKEncodedOrigin origin, int width, int height)
    {
        switch (origin)
        {
            case SKEncodedOrigin.TopRight: canvas.Translate(width, 0); canvas.Scale(-1, 1); break;
            case SKEncodedOrigin.BottomRight: canvas.Translate(width, height); canvas.RotateDegrees(180); break;
            case SKEncodedOrigin.BottomLeft: canvas.Translate(0, height); canvas.Scale(1, -1); break;
            case SKEncodedOrigin.LeftTop: canvas.Scale(-1, 1); canvas.RotateDegrees(90); break;
            case SKEncodedOrigin.RightTop: canvas.Translate(width, 0); canvas.RotateDegrees(90); break;
            case SKEncodedOrigin.RightBottom: canvas.Translate(width, height); canvas.Scale(1, -1); canvas.RotateDegrees(90); break;
            case SKEncodedOrigin.LeftBottom: canvas.Translate(0, height); canvas.RotateDegrees(-90); break;
        }
    }

    /// <summary><c>shot.png</c> → <c>shot.webp</c>, still within the 255-character column.</summary>
    public static string WebpFileName(string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        if (stem.Length == 0) stem = "image";
        return (stem.Length > 250 ? stem[..250] : stem) + ".webp";
    }
}
