using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media.Imaging;

namespace NGMapper.App.Services;

public static class PortraitClipboard
{
    public const int MaximumBytes = 32 * 1024 * 1024;
    private static readonly HttpClient Downloads = new(new HttpClientHandler { UseCookies = false, AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(20) };

    public static string OriginalFilePath(PortraitEditSession session)
    {
        var bytes = File.ReadAllBytes(session.OriginalCopy);
        if (PortraitEditSession.Hash(bytes) != session.OriginalCopyHash) throw new InvalidDataException("The exported original changed. Prepare a fresh edit.");
        return Path.GetFullPath(session.OriginalCopy);
    }

    public static DataObject OriginalFile(PortraitEditSession session)
    {
        var data = new DataObject();
        data.SetData("FFM.OriginalPortrait", session.OriginalCopyHash);
        data.SetData(DataFormats.FileDrop, new[] { OriginalFilePath(session) });
        return data;
    }

    public static async Task<byte[]> ReadImageAsync(IDataObject data, CancellationToken token = default, HttpClient? downloads = null)
    {
        if (data.GetDataPresent("FFM.OriginalPortrait")) throw new InvalidDataException("This clipboard contains the original portrait. Copy the finished image from the browser, then paste it here.");
        if (data.GetDataPresent(DataFormats.FileDrop) && data.GetData(DataFormats.FileDrop) is string[] files)
        {
            if (files.Length != 1) throw new InvalidDataException("Drop or paste one edited portrait at a time.");
            using var input = File.OpenRead(files[0]);
            return await ReadBounded(input, token);
        }
        if (data.GetDataPresent("PNG"))
        {
            var png = data.GetData("PNG");
            if (png is MemoryStream memory) { if (memory.Length > MaximumBytes) throw TooLarge(); return memory.ToArray(); }
            if (png is byte[] bytes) { if (bytes.Length > MaximumBytes) throw TooLarge(); return bytes; }
        }
        if (data.GetDataPresent(DataFormats.Bitmap) && data.GetData(DataFormats.Bitmap) is BitmapSource bitmap)
        {
            if ((long)bitmap.PixelWidth * bitmap.PixelHeight > 16_000_000) throw TooLarge();
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var output = new MemoryStream();
            encoder.Save(output);
            if (output.Length > MaximumBytes) throw TooLarge();
            return output.ToArray();
        }
        string? address = null;
        if (data.GetDataPresent(DataFormats.Html) && data.GetData(DataFormats.Html) is string html && html.Length <= MaximumBytes * 2)
        {
            var match = Regex.Match(html, "<img\\b[^>]*?\\bsrc\\s*=\\s*[\"']([^\"']+)[\"']", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
            if (match.Success) address = WebUtility.HtmlDecode(match.Groups[1].Value);
        }
        if (address is null && data.GetDataPresent(DataFormats.UnicodeText)) address = data.GetData(DataFormats.UnicodeText) as string;
        if (address is null && data.GetDataPresent("text/uri-list")) address = (data.GetData("text/uri-list") as string)?.Split('\n').FirstOrDefault(s => !s.StartsWith('#') && !string.IsNullOrWhiteSpace(s));
        address = address?.Trim();
        if (address?.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase) == true)
        {
            if (address.Length > MaximumBytes * 2) throw TooLarge();
            var comma = address.IndexOf(',');
            if (comma < 0 || !address[..comma].EndsWith(";base64", StringComparison.OrdinalIgnoreCase)) throw NoImage();
            var bytes = Convert.FromBase64String(address[(comma + 1)..]);
            if (bytes.Length > MaximumBytes) throw TooLarge();
            return bytes;
        }
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || uri.IsLoopback || uri.UserInfo.Length != 0) throw NoImage();
        var client = downloads ?? Downloads;
        for (var redirects = 0; redirects <= 3; redirects++)
        {
            using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token);
            if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is Uri location)
            {
                uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
                if (uri.Scheme != Uri.UriSchemeHttps || uri.IsLoopback || uri.UserInfo.Length != 0) throw NoImage();
                continue;
            }
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentType?.MediaType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) != true)
                throw new InvalidDataException("The browser supplied a private or non-image link. Use the browser's Copy image command, or download the PNG and use Import image file.");
            if (response.Content.Headers.ContentLength > MaximumBytes) throw TooLarge();
            using var stream = await response.Content.ReadAsStreamAsync(token);
            return await ReadBounded(stream, token);
        }
        throw NoImage();
    }

    private static async Task<byte[]> ReadBounded(Stream input, CancellationToken token)
    {
        using var output = new MemoryStream();
        var buffer = new byte[65536];
        int count;
        while ((count = await input.ReadAsync(buffer, token)) > 0)
        {
            if (output.Length + count > MaximumBytes) throw TooLarge();
            output.Write(buffer, 0, count);
        }
        return output.ToArray();
    }
    private static InvalidDataException TooLarge() => new("Choose a portrait smaller than 32 MB.");
    private static InvalidDataException NoImage() => new("No usable image was received. In the browser, use Copy image, then Ctrl+V here; alternatively drop a downloaded PNG or use Import image file.");
}
