using System.Globalization;
using System.IO;
using System.Xml;

namespace NGMapper.App.Services;

public sealed record FacepackPortrait(string PlayerId, string ConfigPath, string SourcePath, string? ImagePath);

/// <summary>Discovers FM portrait mappings without assuming any facepack folder structure.</summary>
public sealed class FacepackCatalog
{
    private readonly Dictionary<string, List<FacepackPortrait>> _portraits = new(StringComparer.Ordinal);
    private static readonly string[] Extensions = [".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff"];
    public int ConfigCount { get; private set; }
    public IReadOnlyList<string> Issues => _issues;
    private readonly List<string> _issues = [];
    public int MappedPlayerCount => _portraits.Count;

    public static bool IsInsideGraphics(string path)
    {
        for (DirectoryInfo? dir = new(Path.GetFullPath(path)); dir is not null; dir = dir.Parent)
            if (string.Equals(dir.Name, "graphics", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    public static IEnumerable<string> FindConfigs(string root) => Directory.EnumerateFiles(root, "config.xml", new EnumerationOptions
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.ReparsePoint,
        MatchCasing = MatchCasing.CaseInsensitive
    });

    public IReadOnlyList<FacepackPortrait> Find(string id) => _portraits.TryGetValue(id, out var matches) ? matches : [];

    public static FacepackCatalog Load(string root, IReadOnlySet<string>? wantedIds = null, IProgress<ScanProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        root = Path.GetFullPath(root);
        var rootPrefix = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var result = new FacepackCatalog();
        progress?.Report(new ScanProgress(0, "Finding config.xml files in facepack subfolders…", true));
        var configs = new List<string>();
        foreach (var config in FindConfigs(root))
        {
            cancellationToken.ThrowIfCancellationRequested();
            configs.Add(config);
        }
        result.ConfigCount = configs.Count;
        for (var i = 0; i < configs.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var config = configs[i];
            var pending = new List<FacepackPortrait>();
            try
            {
                using var reader = XmlReader.Create(config, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 256L * 1024 * 1024, IgnoreComments = true });
                while (reader.Read())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "record") continue;
                    var from = reader.GetAttribute("from");
                    var target = reader.GetAttribute("to");
                    var id = PlayerId(target);
                    if (string.IsNullOrWhiteSpace(from) || id is null || (wantedIds is not null && !wantedIds.Contains(id))) continue;
                    var source = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(config)!, from.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar)));
                    if (!source.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
                    {
                        result._issues.Add($"Image reference outside selected facepack: {config}");
                        continue;
                    }
                    var image = ResolveImage(source);
                    pending.Add(new FacepackPortrait(id, config, source, image));
                }
                // Commit only after the whole XML document validates.
                foreach (var portrait in pending)
                {
                    if (!result._portraits.TryGetValue(portrait.PlayerId, out var matches)) result._portraits[portrait.PlayerId] = matches = [];
                    if (!matches.Any(m => string.Equals(m.ImagePath ?? m.SourcePath, portrait.ImagePath ?? portrait.SourcePath, StringComparison.OrdinalIgnoreCase))) matches.Add(portrait);
                }
            }
            catch (Exception ex) when (ex is XmlException or IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                result._issues.Add($"{config}: {ex.Message}");
            }
            progress?.Report(new ScanProgress(100d * (i + 1) / configs.Count, $"Reading facepack configs · {i + 1:N0} / {configs.Count:N0}"));
        }
        progress?.Report(new ScanProgress(100, $"Facepack indexed · {result.MappedPlayerCount:N0} players across {configs.Count:N0} configs"));
        return result;
    }

    internal static string? PlayerId(string? target)
    {
        if (string.IsNullOrWhiteSpace(target)) return null;
        var parts = target.Replace('\\', '/').Trim('/').Split('/');
        if (parts.Length != 5 || !parts[0].Equals("graphics", StringComparison.OrdinalIgnoreCase) || !parts[1].Equals("pictures", StringComparison.OrdinalIgnoreCase) || !parts[2].Equals("person", StringComparison.OrdinalIgnoreCase) || !parts[4].Equals("portrait", StringComparison.OrdinalIgnoreCase)) return null;
        var id = parts[3];
        if (id.StartsWith("r-", StringComparison.OrdinalIgnoreCase)) id = id[2..];
        else if (id.StartsWith("r", StringComparison.OrdinalIgnoreCase)) id = id[1..];
        return uint.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var numeric) ? numeric.ToString(CultureInfo.InvariantCulture) : null;
    }

    private static string? ResolveImage(string source)
    {
        if (Path.HasExtension(source) && Extensions.Contains(Path.GetExtension(source), StringComparer.OrdinalIgnoreCase) && File.Exists(source)) return source;
        foreach (var extension in Extensions)
            if (File.Exists(source + extension)) return source + extension;
        return null;
    }
}
