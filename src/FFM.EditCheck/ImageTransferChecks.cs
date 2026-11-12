using NGMapper.App.Services;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Windows;

internal static class ImageTransferChecks
{
    internal static void Run(PortraitEditSession session, string imageFile)
    {
        var bytes = File.ReadAllBytes(imageFile);
        var upload = PortraitClipboard.OriginalFilePath(session);
        Check(Path.IsPathFullyQualified(upload) && File.Exists(upload) && upload == session.OriginalCopy && Path.GetExtension(upload) == ".png", "upload path is the exact existing prepared PNG, ready for the Add file dialog");
        var outgoing = PortraitClipboard.OriginalFile(session);
        Check(outgoing.GetData(DataFormats.FileDrop) is string[] files && files.Single() == upload && !outgoing.GetDataPresent(DataFormats.Html) && !outgoing.GetDataPresent(DataFormats.UnicodeText), "original drag payload is a real file without misleading combined image/text formats");
        Throws(() => Read(outgoing), "FFM original clipboard cannot be mistaken for a finished edit");
        var png = new DataObject();
        png.SetData("PNG", new MemoryStream(bytes));
        png.SetData(DataFormats.Bitmap, PortraitEditSession.LoadBitmap(session.OriginalCopy));
        Check(Read(png).SequenceEqual(bytes), "browser PNG clipboard data takes priority over converted bitmap");
        var bitmap = new DataObject(DataFormats.Bitmap, PortraitEditSession.LoadBitmap(imageFile));
        Check(Read(bitmap).Length > 0, "bitmap-only clipboard can be imported as PNG");
        Check(Read(new DataObject(DataFormats.FileDrop, new[] { imageFile })).SequenceEqual(bytes), "downloaded image drag/drop imports exact file bytes");
        Throws(() => Read(new DataObject(DataFormats.FileDrop, new[] { imageFile, imageFile })), "multiple dropped files are rejected");
        var inline = new DataObject(DataFormats.Html, "<img src=\"data:image/png;base64," + Convert.ToBase64String(bytes) + "\">");
        Check(Read(inline).SequenceEqual(bytes), "browser inline image data can be pasted without a download step");
        var remote = new DataObject(DataFormats.UnicodeText, "https://images.example.test/finished.png");
        var handler = new Handler(request =>
        {
            Check(request.Headers.Authorization is null && !request.Headers.Contains("Cookie"), "image link fetch does not forward browser credentials");
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
            response.Content.Headers.ContentType = new("image/png");
            return response;
        });
        using var http = new HttpClient(handler);
        Check(Read(remote, http).SequenceEqual(bytes), "public HTTPS image link imports through bounded image download");
        using var privateHttp = new HttpClient(new Handler(_ => new(HttpStatusCode.Unauthorized)));
        Throws(() => Read(remote, privateHttp), "private browser links fail with copy-image/file guidance");
        using var redirectHttp = new HttpClient(new Handler(_ => new(HttpStatusCode.Redirect) { Headers = { Location = new Uri("http://example.test/image.png") } }));
        Throws(() => Read(remote, redirectHttp), "HTTPS image redirects cannot downgrade to HTTP");
        using var largeHttp = new HttpClient(new Handler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([]) };
            response.Content.Headers.ContentType = new("image/png");
            response.Content.Headers.ContentLength = PortraitClipboard.MaximumBytes + 1L;
            return response;
        }));
        Throws(() => Read(remote, largeHttp), "oversized linked images are rejected before reading content");
        Throws(() => Read(new DataObject(DataFormats.UnicodeText, "blob:https://chatgpt.com/private-image")), "browser-private blob URLs do not cause hidden authentication attempts");
        Throws(() => Read(new DataObject(DataFormats.UnicodeText, "ordinary editing prompt")), "ordinary clipboard text is not treated as an image");
        EdgeChecks();
    }
    private static void EdgeChecks()
    {
        const int size = 12;
        var original = new byte[size * size * 4];
        for (var y = 2; y <= 9; y++) for (var x = 2; x <= 9; x++) Set(original, x, y, 90, 110, 130, 255);
        var edited = (byte[])original.Clone();
        Set(edited, 2, 5, 0, 0, 0, 255);
        Set(edited, 3, 5, 0, 0, 255, 255);
        Set(edited, 6, 6, 0, 0, 255, 255); // legitimate red interior detail
        Set(edited, 6, 5, 0, 0, 0, 0); // undefined transparent RGB must not become black
        Set(edited, 0, 0, 0, 0, 255, 255); // colored noise outside original cutout
        var input = (byte[])edited.Clone();
        var cleaned = PortraitEdgeCleanup.Apply(original, edited, size, size, true, out var count);
        Check(EqualPixel(cleaned, original, 2, 5) && EqualPixel(cleaned, original, 3, 5) && count >= 3, "narrow edge cleanup removes synthetic black and red boundary artifacts");
        Check(EqualPixel(cleaned, edited, 6, 6), "legitimate red interior pixels remain untouched");
        Check(EqualPixel(cleaned, original, 6, 5), "transparent generated pixels cannot expose black RGB after alpha restoration");
        Check(EqualPixel(cleaned, original, 0, 0) && Enumerable.Range(0, size * size).All(i => cleaned[i * 4 + 3] == original[i * 4 + 3]), "cleanup preserves the exact original alpha and transparent background");
        Check(edited.SequenceEqual(input), "cleanup does not mutate the imported pixel buffer");
        var off = PortraitEdgeCleanup.Apply(original, edited, size, size, false, out _);
        Check(EqualPixel(off, edited, 2, 5) && EqualPixel(off, original, 6, 5), "cleanup can be disabled while invalid transparent-color protection stays active");
        var alreadyDamaged = (byte[])original.Clone();
        Set(alreadyDamaged, 2, 5, 0, 0, 0, 255);
        var historical = PortraitEdgeCleanup.Apply(alreadyDamaged, edited, size, size, true, out _, original);
        Check(EqualPixel(historical, original, 2, 5), "matching earlier backup can repair a halo already accepted into the current source");
        static void Set(byte[] data, int x, int y, byte b, byte g, byte r, byte a) { var at = (y * size + x) * 4; data[at] = b; data[at + 1] = g; data[at + 2] = r; data[at + 3] = a; }
        static bool EqualPixel(byte[] a, byte[] b, int x, int y) => a.AsSpan((y * size + x) * 4, 4).SequenceEqual(b.AsSpan((y * size + x) * 4, 4));
    }
    private static byte[] Read(IDataObject data, HttpClient? client = null) => PortraitClipboard.ReadImageAsync(data, downloads: client).GetAwaiter().GetResult();
    private static void Check(bool condition, string message) { if (!condition) throw new Exception("FAIL: " + message); Console.WriteLine("PASS: " + message); }
    private static void Throws(Action action, string message)
    {
        try { action(); }
        catch (InvalidDataException) { Console.WriteLine("PASS: " + message); return; }
        throw new Exception("FAIL: " + message);
    }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(response(request));
    }
}
