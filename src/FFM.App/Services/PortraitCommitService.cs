using System.IO;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace FFM.App.Services;

public sealed record PortraitCommitResult(string ImagePath, string BackupPath, bool IsolatedSharedImage);
public sealed record PortraitVersion(int Number, string Path);

public static class PortraitCommitService
{
    public sealed record Mutation(string Target, string? BeforeHash, string AfterHash, string? BeforeFile, string Stage);
    public sealed class Journal
    {
        public string Root { get; set; } = "";
        public string State { get; set; } = "Prepared";
        public List<Mutation> Mutations { get; set; } = [];
    }

    public static PortraitCommitResult Apply(PortraitEditSession session, string dataRoot)
    {
        if (!session.PlayerId.StartsWith("2002", StringComparison.Ordinal) || !uint.TryParse(session.PlayerId, out _)) throw new InvalidOperationException("Real player portraits cannot be changed.");
        if (session.PreviewPath is null || session.PreviewHash is null) throw new InvalidOperationException("Import and review an edited portrait first.");
        var root = Path.GetFullPath(session.FacepackRoot);
        if (!FacepackCatalog.IsInsideGraphics(root)) throw new InvalidOperationException("The selected facepack must be under graphics.");
        RequireInside(root, session.OriginalPath);
        RequireInside(root, session.ConfigPath);
        CheckHash(session.OriginalPath, session.OriginalHash, "The original portrait changed. Prepare a new edit.");
        CheckHash(session.ConfigPath, session.ConfigHash, "The facepack config changed. Reload sources and prepare a new edit.");
        CheckHash(session.PreviewPath, session.PreviewHash, "The preview changed. Import it again before accepting.");
        var originals = File.ReadAllBytes(session.OriginalPath);
        var edited = File.ReadAllBytes(session.PreviewPath);
        var configs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var shared = false;
        var graphics = FacepackCatalog.GraphicsRootFor(root) is { } graphicsRoot
            ? new DirectoryInfo(graphicsRoot)
            : throw new IOException("The graphics directory could not be found.");
        // Inspect all mappings, including real players and icons, before deciding whether
        // the source image can be replaced. The UI's newgen-only index cannot establish this.
        foreach (var config in FacepackCatalog.FindConfigs(graphics.FullName))
        {
            if ((File.GetAttributes(config) & FileAttributes.ReparsePoint) != 0) continue;
            using var reader = XmlReader.Create(config, XmlSettings());
            var beforeHash = PortraitEditSession.Hash(File.ReadAllBytes(config));
            var selected = false;
            while (reader.Read())
            {
                if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "record") continue;
                var from = reader.GetAttribute("from");
                if (string.IsNullOrWhiteSpace(from)) continue;
                var id = FacepackCatalog.PlayerId(reader.GetAttribute("to"));
                var matches = PointsTo(config, from, session.OriginalPath);
                if (id == session.PlayerId)
                {
                    RequireInside(root, config);
                    if (!matches) throw new IOException("This player has another portrait mapping. Reload sources and resolve the conflict before accepting.");
                    selected = true;
                }
                else if (matches) shared = true;
            }
            if (selected) configs.Add(config, beforeHash);
        }
        if (!configs.ContainsKey(session.ConfigPath)) throw new IOException("The selected player's mapping is no longer present.");
        var isolate = shared || !Path.GetExtension(session.OriginalPath).Equals(".png", StringComparison.OrdinalIgnoreCase);
        var target = session.OriginalPath;
        if (isolate)
        {
            target = Path.Combine(Path.GetDirectoryName(target)!, session.PlayerId + ".png");
            if (File.Exists(target)) target = Path.Combine(Path.GetDirectoryName(target)!, session.PlayerId + "_ffm_" + Guid.NewGuid().ToString("N")[..8] + ".png");
        }
        var folder = Path.Combine(dataRoot, "transactions", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var journal = new Journal { Root = root };
        string? backup = null;
        try
        {
            WriteJournal(folder, journal);
            var targetBefore = isolate ? null : originals;
            AddMutation(journal, target, targetBefore, edited, folder);
            WriteJournal(folder, journal);
            if (isolate)
            {
                foreach (var (config, hash) in configs)
                {
                    var bytes = File.ReadAllBytes(config);
                    if (PortraitEditSession.Hash(bytes) != hash) throw new IOException("A facepack config changed while the edit was being prepared.");
                    using var input = new MemoryStream(bytes, false);
                    using var reader = XmlReader.Create(input, XmlSettings());
                    var doc = XDocument.Load(reader, LoadOptions.PreserveWhitespace);
                    foreach (var record in doc.Descendants("record"))
                    {
                        var from = (string?)record.Attribute("from");
                        if (FacepackCatalog.PlayerId((string?)record.Attribute("to")) != session.PlayerId || from is null || !PointsTo(config, from, session.OriginalPath)) continue;
                        record.SetAttributeValue("from", Path.GetRelativePath(Path.GetDirectoryName(config)!, target).Replace('\\', '/'));
                    }
                    using var output = new MemoryStream();
                    doc.Save(output, SaveOptions.DisableFormatting);
                    AddMutation(journal, config, bytes, output.ToArray(), folder);
                    WriteJournal(folder, journal);
                }
            }
            // Back up exact original bytes, beside the active portrait, before any replacement.
            backup = ReserveBackup(target, Path.GetExtension(session.OriginalPath), originals);
            WriteJournal(folder, journal);
            CheckHash(session.OriginalPath, session.OriginalHash, "The original portrait changed before acceptance.");
            foreach (var (config, hash) in configs) CheckHash(config, hash, "A facepack config changed before acceptance.");
            foreach (var mutation in journal.Mutations)
            {
                if (mutation.BeforeHash is null)
                {
                    if (File.Exists(mutation.Target)) throw new IOException("The new portrait filename was created by another process. Retry the edit.");
                    File.Move(mutation.Stage, mutation.Target, false);
                }
                else
                {
                    CheckHash(mutation.Target, mutation.BeforeHash, "A file changed during acceptance.");
                    File.Replace(mutation.Stage, mutation.Target, null);
                }
                CheckHash(mutation.Target, mutation.AfterHash, "The written portrait or config could not be verified.");
            }
            journal.State = "Committed";
            WriteJournal(folder, journal);
            return new PortraitCommitResult(target, backup, shared);
        }
        catch (Exception operationError)
        {
            try
            {
                RollBack(journal);
                journal.State = "RolledBack";
                WriteJournal(folder, journal);
                if (backup is not null && File.Exists(backup) && PortraitEditSession.Hash(File.ReadAllBytes(backup)) == PortraitEditSession.Hash(originals)) File.Delete(backup);
            }
            catch (Exception recoveryError)
            {
                throw new AggregateException("Portrait acceptance failed and automatic rollback needs attention. The numbered backup and transaction files were retained.", operationError, recoveryError);
            }
            throw;
        }
        finally
        {
            foreach (var mutation in journal.Mutations) if (File.Exists(mutation.Stage)) File.Delete(mutation.Stage);
        }
    }

    public static IReadOnlyList<PortraitVersion> Versions(string image)
    {
        var stem = Path.GetFileNameWithoutExtension(image);
        return Directory.EnumerateFiles(Path.GetDirectoryName(image)!, stem + "_prev*.*")
            .Select(path => new PortraitVersion(VersionNumber(path, stem), path)).Where(v => v.Number > 0).OrderByDescending(v => v.Number).ToArray();
    }

    public static IReadOnlyList<string> Recover(string dataRoot)
    {
        var errors = new List<string>();
        var graphicsRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var root = Path.Combine(dataRoot, "transactions");
        if (!Directory.Exists(root)) return errors;
        foreach (var path in Directory.EnumerateFiles(root, "journal.json", SearchOption.AllDirectories))
        {
            try
            {
                var journal = JsonSerializer.Deserialize<Journal>(File.ReadAllText(path)) ?? throw new InvalidDataException("Invalid portrait transaction journal.");
                var graphicsRoot = FacepackCatalog.GraphicsRootFor(journal.Root);
                if (graphicsRoot is not null) graphicsRoots.Add(graphicsRoot);
                if (journal.State is "Committed" or "RolledBack") continue;
                if (!FacepackCatalog.IsInsideGraphics(journal.Root)) throw new InvalidDataException("Invalid transaction graphics location.");
                foreach (var mutation in journal.Mutations)
                {
                    RequireInside(journal.Root, mutation.Target);
                    if (mutation.BeforeFile is not null) RequireInside(Path.GetDirectoryName(path)!, mutation.BeforeFile);
                    if (!new[] { ".xml", ".png" }.Contains(Path.GetExtension(mutation.Target), StringComparer.OrdinalIgnoreCase)) throw new InvalidDataException("Invalid transaction file type.");
                }
                RollBack(journal);
                journal.State = "RolledBack";
                WriteJournal(Path.GetDirectoryName(path)!, journal);
                foreach (var mutation in journal.Mutations) TryDelete(mutation.Stage);
            }
            catch (Exception ex) { errors.Add($"{path}: {ex.Message}"); }
        }
        if (errors.Count == 0)
            foreach (var graphicsRoot in graphicsRoots) RemoveOrphanStages(graphicsRoot);
        return errors;
    }

    public static void PruneTemporaryData(string dataRoot, TimeSpan? retention = null)
    {
        var cutoff = DateTime.UtcNow - (retention ?? TimeSpan.FromDays(7));
        PruneFolders(Path.Combine(dataRoot, "edits"), cutoff, _ => true);
        var transactions = Path.Combine(dataRoot, "transactions");
        PruneFolders(transactions, cutoff, folder =>
        {
            var journalPath = Path.Combine(folder, "journal.json");
            if (!File.Exists(journalPath)) return true;
            try
            {
                var journal = JsonSerializer.Deserialize<Journal>(File.ReadAllText(journalPath));
                return journal?.State is "Committed" or "RolledBack";
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return false; }
        });
    }

    private static void PruneFolders(string root, DateTime cutoff, Func<string, bool> mayDelete)
    {
        if (!Directory.Exists(root)) return;
        foreach (var folder in Directory.EnumerateDirectories(root))
        {
            try
            {
                var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                var fullFolder = Path.GetFullPath(folder);
                if (!fullFolder.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase) || Directory.GetLastWriteTimeUtc(folder) >= cutoff || !mayDelete(folder)) continue;
                Directory.Delete(folder, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        }
    }

    private static void RemoveOrphanStages(string graphicsRoot)
    {
        var pending = new Stack<string>();
        pending.Push(graphicsRoot);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            IEnumerable<string> children;
            try { children = Directory.EnumerateFileSystemEntries(directory).ToArray(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            foreach (var item in children)
            {
                try
                {
                    var attributes = File.GetAttributes(item);
                    if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                    if ((attributes & FileAttributes.Directory) != 0) pending.Push(item);
                    else if (Path.GetFileName(item).StartsWith(".ffm-", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(item).Contains(".ffm-restore-", StringComparison.OrdinalIgnoreCase)) TryDelete(item);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static void AddMutation(Journal journal, string target, byte[]? before, byte[] after, string folder)
    {
        var beforeFile = before is null ? null : Path.Combine(folder, journal.Mutations.Count + ".before");
        if (beforeFile is not null) DurableWrite(beforeFile, before!);
        var stage = Path.Combine(Path.GetDirectoryName(target)!, ".ffm-" + Guid.NewGuid().ToString("N") + ".tmp");
        DurableWrite(stage, after);
        journal.Mutations.Add(new Mutation(target, before is null ? null : PortraitEditSession.Hash(before), PortraitEditSession.Hash(after), beforeFile, stage));
    }
    private static void RollBack(Journal journal)
    {
        foreach (var m in journal.Mutations.AsEnumerable().Reverse())
        {
            if (!File.Exists(m.Target))
            {
                if (m.BeforeHash is null) continue;
                // A missing pre-existing file requires manual recovery; never recreate over an external change.
                throw new IOException($"A transaction file disappeared: {m.Target}. Its backup is retained.");
            }
            var hash = PortraitEditSession.Hash(File.ReadAllBytes(m.Target));
            if (hash == m.BeforeHash) continue;
            if (hash != m.AfterHash) throw new IOException($"External changes prevent automatic recovery of {m.Target}. Backups are retained.");
            if (m.BeforeFile is null) File.Delete(m.Target);
            else
            {
                var bytes = File.ReadAllBytes(m.BeforeFile);
                if (PortraitEditSession.Hash(bytes) != m.BeforeHash) throw new IOException("A transaction backup failed verification.");
                var stage = m.Target + ".ffm-restore-" + Guid.NewGuid().ToString("N");
                DurableWrite(stage, bytes);
                File.Replace(stage, m.Target, null);
            }
        }
    }
    private static string ReserveBackup(string target, string originalExtension, byte[] bytes)
    {
        var stem = Path.GetFileNameWithoutExtension(target);
        var version = Versions(target).Select(v => v.Number).DefaultIfEmpty(0).Max() + 1;
        var path = Path.Combine(Path.GetDirectoryName(target)!, $"{stem}_prev{version}{originalExtension}");
        DurableWrite(path, bytes);
        CheckHash(path, PortraitEditSession.Hash(bytes), "The previous portrait backup failed verification.");
        return path;
    }
    private static int VersionNumber(string path, string stem) => int.TryParse(Path.GetFileNameWithoutExtension(path)[(stem.Length + 5)..], out var number) ? number : -1;
    private static void WriteJournal(string folder, Journal journal)
    {
        var stage = Path.Combine(folder, "journal-" + Guid.NewGuid().ToString("N") + ".tmp");
        DurableWrite(stage, System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(journal)));
        File.Move(stage, Path.Combine(folder, "journal.json"), true);
    }
    private static void DurableWrite(string path, byte[] bytes)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.Write(bytes);
        stream.Flush(true);
    }
    private static void CheckHash(string path, string hash, string message)
    {
        if (!File.Exists(path) || PortraitEditSession.Hash(File.ReadAllBytes(path)) != hash) throw new IOException(message);
    }
    private static bool PointsTo(string config, string from, string image)
    {
        var path = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(config)!, from.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar)));
        return path.Equals(image, StringComparison.OrdinalIgnoreCase) || (path + Path.GetExtension(image)).Equals(image, StringComparison.OrdinalIgnoreCase);
    }
    private static void RequireInside(string root, string path)
    {
        if (!Path.GetFullPath(path).StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("A portrait path lies outside the selected facepack.");
        if (FacepackCatalog.HasReparsePointBetween(root, path)) throw new IOException("This portrait path passes through a junction or symbolic link. Choose a regular facepack folder and image.");
    }
    private static XmlReaderSettings XmlSettings() => new() { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 256L * 1024 * 1024 };
}
