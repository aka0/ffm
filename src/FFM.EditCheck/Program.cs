using NGMapper.App.Services;
using System.IO;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;

internal static class Program
{
    private const string Id = "2002098250";
    private static readonly string Sandbox = Path.Combine(Path.GetTempPath(), "FFM.EditCheck-" + Guid.NewGuid().ToString("N"));
    private static string Store => Path.Combine(Sandbox, "app-data");
    [STAThread]
    private static void Main()
    {
        Directory.CreateDirectory(Sandbox);
        var pack = Pack("unique", false);
        var original = File.ReadAllBytes(pack.image);
        var config = File.ReadAllBytes(pack.config);
        var candidate = Path.Combine(Sandbox, "download.png");
        Image(candidate, 8, 8, 190);
        var session = Prepare(pack.root, candidate);
        Check(File.ReadAllBytes(pack.image).SequenceEqual(original) && File.ReadAllBytes(pack.config).SequenceEqual(config), "handoff and preview leave facepack bytes unchanged");
        var imported = PortraitEditSession.LoadBitmap(session.PreviewPath!);
        Check(imported.PixelWidth == 4 && imported.PixelHeight == 4 && Alpha(imported).SequenceEqual(Alpha(PortraitEditSession.LoadBitmap(pack.image))), "import normalizes original dimensions and exact cutout alpha");
        var wrong = Path.Combine(Sandbox, "wrong.png");
        Image(wrong, 8, 4, 70);
        Throws(() => session.Import(wrong), "different crop/aspect ratio is rejected");
        var result = PortraitCommitService.Apply(session, Store);
        Check(result.ImagePath == pack.image && File.ReadAllBytes(result.BackupPath).SequenceEqual(original) && Path.GetFileName(result.BackupPath) == "original_prev1.png", "exclusive portrait replacement makes exact sibling prev1 backup first");
        Check(File.ReadAllBytes(pack.config).SequenceEqual(config), "exclusive replacement leaves config unchanged");
        var once = File.ReadAllBytes(pack.image);
        var second = Prepare(pack.root, candidate);
        var result2 = PortraitCommitService.Apply(second, Store);
        Check(Path.GetFileName(result2.BackupPath) == "original_prev2.png" && File.ReadAllBytes(result2.BackupPath).SequenceEqual(once), "repeated acceptance increments version and keeps exact previous bytes");
        var restoration = Prepare(pack.root, result.BackupPath);
        PortraitCommitService.Apply(restoration, Store);
        Check(Pixels(PortraitEditSession.LoadBitmap(pack.image)).SequenceEqual(Pixels(PortraitEditSession.LoadBitmap(result.BackupPath))) && PortraitCommitService.Versions(pack.image).Count == 3, "restoration returns original pixels and creates another backup");
        var stale = Prepare(pack.root, candidate);
        Image(pack.image, 4, 4, 20);
        var changed = File.ReadAllBytes(pack.image);
        Throws(() => PortraitCommitService.Apply(stale, Store), "externally changed portrait blocks stale acceptance");
        Check(File.ReadAllBytes(pack.image).SequenceEqual(changed), "stale rejection preserves the external change");
        var replacedExport = Prepare(pack.root, candidate);
        File.WriteAllBytes(replacedExport.OriginalCopy, File.ReadAllBytes(candidate));
        Throws(() => replacedExport.Import(candidate), "overwriting exported original cannot alter the normalization baseline");
        var staleConfig = Prepare(pack.root, candidate);
        File.AppendAllText(pack.config, "\n");
        Throws(() => PortraitCommitService.Apply(staleConfig, Store), "externally changed config blocks stale acceptance");

        var shared = Pack("shared", true);
        var sharedOriginal = File.ReadAllBytes(shared.image);
        var nested = Path.Combine(shared.root, "arbitrary", "subfolder");
        Directory.CreateDirectory(nested);
        var extraConfig = Path.Combine(nested, "config.xml");
        File.WriteAllText(extraConfig, $"<record><record from=\"../../original\" to=\"graphics/pictures/person/r-{Id}/portrait\"/></record>");
        var isolated = PortraitCommitService.Apply(Prepare(shared.root, candidate), Store);
        Check(isolated.IsolatedSharedImage && isolated.ImagePath != shared.image && File.ReadAllBytes(shared.image).SequenceEqual(sharedOriginal), "shared source including a real player is never overwritten");
        Check(File.ReadAllBytes(isolated.BackupPath).SequenceEqual(sharedOriginal) && Path.GetFileName(isolated.BackupPath) == Id + "_prev1.png", "isolated player keeps an exact numbered sibling backup");
        var doc = XDocument.Load(shared.config);
        Check(doc.Descendants("record").Single(e => ((string?)e.Attribute("to"))?.Contains("2000026194") == true).Attribute("from")!.Value == "original", "real player's config mapping remains unchanged");
        var lookup = FacepackCatalog.Load(shared.root);
        Check(lookup.Find(Id).Count == 1 && lookup.Find(Id).Single().ImagePath == isolated.ImagePath && XDocument.Load(extraConfig).Descendants("record").Single(e => e.Attribute("from") is not null).Attribute("from")!.Value.Contains(Id), "all selected-player aliases across scattered configs follow isolated portrait");
        var beforeNext = File.ReadAllBytes(isolated.ImagePath);
        var next = PortraitCommitService.Apply(Prepare(shared.root, candidate), Store);
        Check(next.ImagePath == isolated.ImagePath && Path.GetFileName(next.BackupPath) == Id + "_prev2.png" && File.ReadAllBytes(next.BackupPath).SequenceEqual(beforeNext), "later edits reuse player-specific portrait and increment its backup version");

        var locked = Pack("locked", true);
        var lockedSession = Prepare(locked.root, candidate);
        var lockedImage = File.ReadAllBytes(locked.image);
        var lockedConfig = File.ReadAllBytes(locked.config);
        using (var hold = new FileStream(locked.config, FileMode.Open, FileAccess.Read, FileShare.Read))
            Throws(() => PortraitCommitService.Apply(lockedSession, Store), "locked config fails acceptance instead of leaving a partial edit");
        Check(File.ReadAllBytes(locked.config).SequenceEqual(lockedConfig) && File.ReadAllBytes(locked.image).SequenceEqual(lockedImage) && !File.Exists(Path.Combine(locked.root, Id + ".png")), "failed shared-image transaction rolls back created portrait and retains config/source");
        var realMapping = new FacepackPortrait("2000026194", locked.config, locked.image, locked.image);
        Throws(() => PortraitEditSession.Prepare("2000026194", locked.root, realMapping, "test", Store), "real player ID is rejected by the edit service");

        var recoveryPack = Pack("recovery", false);
        var before = File.ReadAllBytes(recoveryPack.image);
        var after = File.ReadAllBytes(candidate);
        var transaction = Path.Combine(Store, "transactions", "simulated-interruption");
        Directory.CreateDirectory(transaction);
        var beforeFile = Path.Combine(transaction, "0.before");
        File.WriteAllBytes(beforeFile, before);
        File.WriteAllBytes(recoveryPack.image, after);
        var journal = new PortraitCommitService.Journal { Root = recoveryPack.root, Mutations = [new(recoveryPack.image, PortraitEditSession.Hash(before), PortraitEditSession.Hash(after), beforeFile, recoveryPack.image + ".stage")] };
        File.WriteAllText(Path.Combine(transaction, "journal.json"), JsonSerializer.Serialize(journal));
        Check(PortraitCommitService.Recover(Store).Count == 0 && File.ReadAllBytes(recoveryPack.image).SequenceEqual(before), "startup recovery restores interrupted transaction from verified preimage");
        journal.State = "Prepared";
        File.WriteAllText(Path.Combine(transaction, "journal.json"), JsonSerializer.Serialize(journal));
        Image(recoveryPack.image, 4, 4, 123);
        var external = File.ReadAllBytes(recoveryPack.image);
        Check(PortraitCommitService.Recover(Store).Count == 1 && File.ReadAllBytes(recoveryPack.image).SequenceEqual(external), "recovery detects external edits and retains them with backups");
        Console.WriteLine("Fixtures only: " + Sandbox);
    }
    private static (string root, string image, string config) Pack(string name, bool shared)
    {
        var root = Path.Combine(Sandbox, name, "graphics", "facepack");
        Directory.CreateDirectory(root);
        var image = Path.Combine(root, "original.png");
        Image(image, 4, 4, 50);
        var config = Path.Combine(root, "config.xml");
        File.WriteAllText(config, $"<record><list id=\"maps\"><record from=\"original\" to=\"graphics/pictures/person/{Id}/portrait\"/>" + (shared ? "<record from=\"original\" to=\"graphics/pictures/person/2000026194/portrait\"/>" : "") + "</list></record>");
        return (root, image, config);
    }
    private static PortraitEditSession Prepare(string root, string candidate)
    {
        var session = PortraitEditSession.Prepare(Id, root, FacepackCatalog.Load(root).Find(Id).Single(), "Add light stubble; preserve identity.", Store);
        session.Import(candidate);
        return session;
    }
    private static void Image(string path, int width, int height, byte color)
    {
        var pixels = new byte[width * height * 4];
        for (var i = 0; i < pixels.Length; i += 4) { pixels[i] = color; pixels[i + 1] = 80; pixels[i + 2] = 150; pixels[i + 3] = i == 0 ? (byte)0 : (byte)255; }
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
    private static byte[] Pixels(BitmapSource image)
    {
        var bitmap = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        var bytes = new byte[image.PixelWidth * image.PixelHeight * 4];
        bitmap.CopyPixels(bytes, image.PixelWidth * 4, 0);
        return bytes;
    }
    private static byte[] Alpha(BitmapSource image) => Pixels(image).Where((b, i) => i % 4 == 3).ToArray();
    private static void Check(bool condition, string message) { if (!condition) throw new Exception("FAIL: " + message); Console.WriteLine("PASS: " + message); }
    private static void Throws(Action action, string message) { try { action(); } catch (Exception ex) when (ex is IOException or InvalidOperationException or InvalidDataException) { Console.WriteLine("PASS: " + message); return; } throw new Exception("FAIL: " + message); }
}
