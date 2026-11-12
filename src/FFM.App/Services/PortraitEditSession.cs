using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace FFM.App.Services;

public sealed class PortraitEditSession : IDisposable
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
    public string? EdgeReferenceCopy { get; init; }
    public string? EdgeReferenceHash { get; init; }
    public required string Prompt { get; init; }
    public string? PreviewPath { get; private set; }
    public string? PreviewHash { get; private set; }
    public int CorrectedEdgePixels { get; private set; }
    public bool EdgeCleanupEnabled { get; private set; }
    private byte[]? _importedBytes;
    public void Reprocess(bool cleanEdges)
    {
        if (_importedBytes is null) throw new InvalidOperationException("Import an edited image first.");
        ImportBytes(_importedBytes, cleanEdges);
    }

    public void DiscardPreview()
    {
        if (PreviewPath is not null)
        {
            try { if (File.Exists(PreviewPath)) File.Delete(PreviewPath); } catch (IOException) { }
        }
        PreviewPath = null;
        PreviewHash = null;
        CorrectedEdgePixels = 0;
        _importedBytes = null;
    }

    public void Dispose()
    {
        try { if (Directory.Exists(Folder)) Directory.Delete(Folder, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

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
        string? edgeCopy = null;
        // A previously accepted halo may already be in the current portrait. Prefer the
        // earliest readable sibling backup with exactly the same mask for boundary colors.
        var originalPixels = Pixels(image);
        foreach (var version in PortraitCommitService.Versions(mapping.ImagePath).OrderBy(v => v.Number))
        {
            try
            {
                var reference = Decode(ReadImageBytes(version.Path));
                if (reference.PixelWidth != image.PixelWidth || reference.PixelHeight != image.PixelHeight) continue;
                var referencePixels = Pixels(reference);
                var sameAlpha = true;
                for (var i = 3; i < originalPixels.Length; i += 4) if (originalPixels[i] != referencePixels[i]) { sameAlpha = false; break; }
                if (!sameAlpha) continue;
                edgeCopy = Path.Combine(folder, "edge-reference.png");
                SavePng(reference, edgeCopy);
                break;
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException or FileFormatException) { }
        }
        File.WriteAllText(Path.Combine(folder, "prompt.txt"), prompt, new UTF8Encoding(false));
        return new PortraitEditSession { PlayerId = id, FacepackRoot = Path.GetFullPath(root), OriginalPath = Path.GetFullPath(mapping.ImagePath), ConfigPath = mapping.ConfigPath,
            OriginalHash = Hash(bytes), ConfigHash = Hash(File.ReadAllBytes(mapping.ConfigPath)), Folder = folder, OriginalCopy = original, OriginalCopyHash = Hash(File.ReadAllBytes(original)),
            EdgeReferenceCopy = edgeCopy, EdgeReferenceHash = edgeCopy is null ? null : Hash(File.ReadAllBytes(edgeCopy)), Prompt = prompt };
    }

    public void Import(string path, bool cleanEdges = true) => ImportBytes(ReadImageBytes(path), cleanEdges);

    public void ImportRestore(string path)
    {
        var bytes = ReadImageBytes(path);
        _ = Decode(bytes);
        DiscardPreview();
        var preview = Path.Combine(Folder, "restore-" + Guid.NewGuid().ToString("N") + Path.GetExtension(path));
        File.WriteAllBytes(preview, bytes);
        PreviewPath = preview;
        PreviewHash = Hash(bytes);
        CorrectedEdgePixels = 0;
        EdgeCleanupEnabled = false;
        _importedBytes = bytes;
    }

    public void ImportBytes(byte[] bytes, bool cleanEdges = true)
    {
        if (bytes.Length > 32 * 1024 * 1024) throw new InvalidDataException("Choose a portrait image smaller than 32 MB.");
        var snapshot = File.ReadAllBytes(OriginalCopy);
        if (Hash(snapshot) != OriginalCopyHash) throw new InvalidDataException("The exported original was changed. Prepare a fresh edit and save the provider's result as a separate file.");
        var original = Decode(snapshot);
        var edited = Decode(bytes);
        var originalRatio = (double)original.PixelWidth / original.PixelHeight;
        var editedRatio = (double)edited.PixelWidth / edited.PixelHeight;
        if (Math.Abs(originalRatio / editedRatio - 1) > 0.02) throw new InvalidDataException("The edited image has a different aspect ratio. Ask the provider to keep the original crop and aspect ratio, then import it again.");
        var visual = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.Fant);
        using (var drawing = visual.RenderOpen()) drawing.DrawImage(edited, new Rect(0, 0, original.PixelWidth, original.PixelHeight));
        var render = new RenderTargetBitmap(original.PixelWidth, original.PixelHeight, 96, 96, PixelFormats.Pbgra32);
        render.Render(visual);
        var before = Pixels(original);
        var rendered = Pixels(render);
        var originalHasTransparency = HasTransparency(before);
        var editedHasTransparency = HasTransparency(rendered);
        if (originalHasTransparency && !editedHasTransparency)
            throw new InvalidDataException("This result has an opaque background, but the original portrait uses transparency. Ask the image editor for a transparent PNG, then import it again. FFM has not enabled acceptance.");
        byte[]? edgeReference = null;
        if (cleanEdges && EdgeReferenceCopy is not null)
        {
            var reference = File.ReadAllBytes(EdgeReferenceCopy);
            if (Hash(reference) != EdgeReferenceHash) throw new InvalidDataException("The edge reference changed. Prepare a fresh edit.");
            edgeReference = Pixels(Decode(reference));
        }
        var after = PortraitEdgeCleanup.Apply(before, rendered, original.PixelWidth, original.PixelHeight, cleanEdges, out var corrected, edgeReference);
        var normalized = BitmapSource.Create(original.PixelWidth, original.PixelHeight, 96, 96, PixelFormats.Bgra32, null, after, original.PixelWidth * 4);
        normalized.Freeze();
        DiscardPreview();
        var preview = Path.Combine(Folder, "preview-" + Guid.NewGuid().ToString("N") + ".png");
        SavePng(normalized, preview);
        PreviewPath = preview;
        PreviewHash = Hash(File.ReadAllBytes(preview));
        CorrectedEdgePixels = corrected;
        EdgeCleanupEnabled = cleanEdges;
        _importedBytes = bytes;
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
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
        if (decoder.Frames.Count != 1) throw new InvalidDataException("Choose a single, still portrait image.");
        var bitmap = decoder.Frames[0];
        if (bitmap.PixelWidth <= 0 || bitmap.PixelHeight <= 0 || (long)bitmap.PixelWidth * bitmap.PixelHeight > 16_000_000) throw new InvalidDataException("The portrait dimensions are too large.");
        var converted = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
        var pixels = new byte[checked(bitmap.PixelWidth * bitmap.PixelHeight * 4)];
        converted.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
        var loaded = BitmapSource.Create(bitmap.PixelWidth, bitmap.PixelHeight, 96, 96, PixelFormats.Bgra32, null, pixels, bitmap.PixelWidth * 4);
        loaded.Freeze();
        return loaded;
    }
    private static byte[] Pixels(BitmapSource bitmap)
    {
        var converted = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
        var pixels = new byte[checked(bitmap.PixelWidth * bitmap.PixelHeight * 4)];
        converted.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
        return pixels;
    }
    private static bool HasTransparency(byte[] pixels)
    {
        for (var i = 3; i < pixels.Length; i += 4) if (pixels[i] < 250) return true;
        return false;
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
