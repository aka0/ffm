using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media.Imaging;

namespace FFM.App.Services;

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

    public static async Task<byte[]> ReadImageAsync(IDataObject data, CancellationToken token = default, HttpClient? downloads = null, Func<string, CancellationToken, Task<IPAddress[]>>? resolveHost = null)
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
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || uri.UserInfo.Length != 0) throw NoImage();
        var client = downloads ?? Downloads;
        for (var redirects = 0; redirects <= 3; redirects++)
        {
            await EnsurePublicHostAsync(uri, token, resolveHost);
            using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token);
            if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is Uri location)
            {
                uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
                if (uri.Scheme != Uri.UriSchemeHttps || uri.UserInfo.Length != 0) throw NoImage();
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

    private static async Task EnsurePublicHostAsync(Uri uri, CancellationToken token, Func<string, CancellationToken, Task<IPAddress[]>>? resolver)
    {
        var host = uri.DnsSafeHost.TrimEnd('.');
        if (host.Length == 0 || host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || !host.Contains('.') || host.EndsWith(".local", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)) throw NoImage();
        IPAddress[] addresses;
        if (IPAddress.TryParse(host, out var literal)) addresses = [literal];
        else
        {
            try { addresses = await (resolver?.Invoke(host, token) ?? Dns.GetHostAddressesAsync(host, token)); }
            catch (Exception ex) when (ex is SocketException or ArgumentException) { throw NoImage(); }
        }
        if (addresses.Length == 0 || addresses.Any(IsNonPublicAddress)) throw NoImage();
    }

    private static bool IsNonPublicAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any) || address.Equals(IPAddress.IPv6Loopback)) return true;
        var b = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
            return b[0] == 0 || b[0] == 10 || b[0] == 127 || b[0] >= 224 || b[0] == 169 && b[1] == 254 || b[0] == 172 && b[1] is >= 16 and <= 31 || b[0] == 192 && b[1] == 168 || b[0] == 100 && b[1] is >= 64 and <= 127;
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
            return address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || (b[0] & 0xFE) == 0xFC || (b[0] == 0xFF) || b.All(x => x == 0);
        return true;
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
    private static InvalidDataException NoImage() => new("No usable public image link was received. In the browser, use Copy image, then Ctrl+V here; alternatively drop a downloaded PNG or use Import image file.");
}
