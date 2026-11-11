using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace NGMapper.App.Services;

public sealed class PortraitEditSession
{
    public required string PlayerId { get; init; }
    public required string FacepackRoot { get; init; }
    public required string OriginalPath { get; init; }
    public required string ConfigPath { get; init; }
    public required string OriginalHash { get; init; }
    public required string ConfigHash { get; init; }
    public required string Folder { get; init; }
    public required string OriginalCopy { get; init; }
    public required string OriginalCopyHash { get; init; }
    public required string Prompt { get; init; }
    public string? PreviewPath { get; private set; }
    public string? PreviewHash { get; private set; }

    public static PortraitEditSession Prepare(string id, string root, FacepackPortrait mapping, string prompt, string dataRoot)
    {
        if (!id.StartsWith("2002", StringComparison.Ordinal) || mapping.PlayerId != id || !uint.TryParse(id, out _)) throw new InvalidOperationException("Only a confirmed newgen can enter the editing workflow.");
        if (mapping.ImagePath is null) throw new FileNotFoundException("The mapped portrait is missing.");
        var bytes = ReadImageBytes(mapping.ImagePath);
        var image = Decode(bytes);
        var folder = Path.Combine(dataRoot, "edits", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var original = Path.Combine(folder, "portrait.png");
        SavePng(image, original);
        var fullPrompt = "Edit the attached original portrait, rather than creating a new person. Apply only the requested changes. Preserve identity, facial structure, pose, expression, clothing, framing, lighting, background and image aspect ratio. Keep the original transparent cutout where present. Return only the edited portrait, with no text, watermark, border or collage.\n\n" + prompt;
        File.WriteAllText(Path.Combine(folder, "prompt.txt"), fullPrompt, new UTF8Encoding(false));
        return new PortraitEditSession { PlayerId = id, FacepackRoot = Path.GetFullPath(root), OriginalPath = Path.GetFullPath(mapping.ImagePath), ConfigPath = mapping.ConfigPath,
            OriginalHash = Hash(bytes), ConfigHash = Hash(File.ReadAllBytes(mapping.ConfigPath)), Folder = folder, OriginalCopy = original, OriginalCopyHash = Hash(File.ReadAllBytes(original)), Prompt = fullPrompt };
    }

    public void Import(string path)
    {
        var snapshot = File.ReadAllBytes(OriginalCopy);
        if (Hash(snapshot) != OriginalCopyHash) throw new InvalidDataException("The exported original was changed. Prepare a fresh edit and save the provider's result as a separate file.");
        var original = Decode(snapshot);
        var edited = Decode(ReadImageBytes(path));
        var originalRatio = (double)original.PixelWidth / original.PixelHeight;
        var editedRatio = (double)edited.PixelWidth / edited.PixelHeight;
        if (Math.Abs(originalRatio / editedRatio - 1) > 0.02) throw new InvalidDataException("The edited image has a different aspect ratio. Ask the provider to keep the original crop and aspect ratio, then import it again.");
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen()) drawing.DrawImage(edited, new Rect(0, 0, original.PixelWidth, original.PixelHeight));
        var render = new RenderTargetBitmap(original.PixelWidth, original.PixelHeight, 96, 96, PixelFormats.Pbgra32);
        render.Render(visual);
        var before = Pixels(original);
        var after = Pixels(render);
        // Preserve the game's cutout transparency, including transparent background pixels.
        for (var i = 0; i < before.Length; i += 4)
        {
            after[i + 3] = before[i + 3];
            if (before[i + 3] == 0) Array.Copy(before, i, after, i, 4);
        }
        var normalized = BitmapSource.Create(original.PixelWidth, original.PixelHeight, 96, 96, PixelFormats.Bgra32, null, after, original.PixelWidth * 4);
        normalized.Freeze();
        var preview = Path.Combine(Folder, "preview-" + Guid.NewGuid().ToString("N") + ".png");
        SavePng(normalized, preview);
        PreviewPath = preview;
        PreviewHash = Hash(File.ReadAllBytes(preview));
    }

    public static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    public static BitmapSource LoadBitmap(string path) => Decode(ReadImageBytes(path));
    private static byte[] ReadImageBytes(string path)
    {
        if (new FileInfo(path).Length > 32 * 1024 * 1024) throw new InvalidDataException("Choose a portrait image smaller than 32 MB.");
        return File.ReadAllBytes(path);
    }
    private static BitmapSource Decode(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, false);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        if (decoder.Frames.Count != 1) throw new InvalidDataException("Choose a single, still portrait image.");
        var bitmap = decoder.Frames[0];
        if (bitmap.PixelWidth <= 0 || bitmap.PixelHeight <= 0 || (long)bitmap.PixelWidth * bitmap.PixelHeight > 16_000_000) throw new InvalidDataException("The portrait dimensions are too large.");
        bitmap.Freeze();
        return bitmap;
    }
    private static byte[] Pixels(BitmapSource bitmap)
    {
        var converted = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
        var pixels = new byte[checked(bitmap.PixelWidth * bitmap.PixelHeight * 4)];
        converted.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
        return pixels;
    }
    private static void SavePng(BitmapSource bitmap, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        encoder.Save(stream);
        stream.Flush(true);
    }
}
