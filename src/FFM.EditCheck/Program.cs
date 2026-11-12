using FFM.App.Services;
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
        PromptChecks();
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
        var opaque = Path.Combine(Sandbox, "flattened.png");
        Image(opaque, 8, 8, 70, transparentPixel: false);
        Throws(() => session.Import(opaque), "flattened edit is rejected when the original requires transparency");
        var result = PortraitCommitService.Apply(session, Store);
        Check(result.ImagePath == pack.image && File.ReadAllBytes(result.BackupPath).SequenceEqual(original) && Path.GetFileName(result.BackupPath) == "original_prev1.png", "exclusive portrait replacement makes exact sibling prev1 backup first");
        Check(File.ReadAllBytes(pack.config).SequenceEqual(config), "exclusive replacement leaves config unchanged");
        var once = File.ReadAllBytes(pack.image);
        var second = Prepare(pack.root, candidate);
        Check(second.EdgeReferenceCopy is not null, "earliest matching backup is snapshotted as the edge-color reference");
        var result2 = PortraitCommitService.Apply(second, Store);
        Check(Path.GetFileName(result2.BackupPath) == "original_prev2.png" && File.ReadAllBytes(result2.BackupPath).SequenceEqual(once), "repeated acceptance increments version and keeps exact previous bytes");
        var restoration = Prepare(pack.root, result.BackupPath);
        restoration.ImportRestore(result.BackupPath);
        PortraitCommitService.Apply(restoration, Store);
        Check(File.ReadAllBytes(pack.image).SequenceEqual(File.ReadAllBytes(result.BackupPath)) && PortraitCommitService.Versions(pack.image).Count == 3, "restoration returns exact backup bytes and creates another backup");
        var stale = Prepare(pack.root, candidate);
        Image(pack.image, 4, 4, 20);
        var changed = File.ReadAllBytes(pack.image);
        Throws(() => PortraitCommitService.Apply(stale, Store), "externally changed portrait blocks stale acceptance");
        Check(File.ReadAllBytes(pack.image).SequenceEqual(changed), "stale rejection preserves the external change");
        var failedAccept = Pack("failed-accept", false);
        var failedSession = Prepare(failedAccept.root, candidate);
        using (var hold = new FileStream(failedAccept.image, FileMode.Open, FileAccess.Read, FileShare.Read))
            Throws(() => PortraitCommitService.Apply(failedSession, Store), "locked target rejects replacement");
        Check(PortraitCommitService.Versions(failedAccept.image).Count == 0 && File.ReadAllBytes(failedAccept.image).SequenceEqual(File.ReadAllBytes(failedSession.OriginalPath)), "failed acceptance removes its unused numbered backup after verified rollback");
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
        lookup.UpdatePortrait(Id, isolated.ImagePath);
        Check(lookup.Find(Id).All(match => match.ImagePath == isolated.ImagePath), "accepted portrait refreshes the selected player's cached mapping without reloading all configs");
        var beforeNext = File.ReadAllBytes(isolated.ImagePath);
        var next = PortraitCommitService.Apply(Prepare(shared.root, candidate), Store);
        Check(next.ImagePath == isolated.ImagePath && Path.GetFileName(next.BackupPath) == Id + "_prev2.png" && File.ReadAllBytes(next.BackupPath).SequenceEqual(beforeNext), "later edits reuse player-specific portrait and increment its backup version");

        var sibling = Pack("sibling-reference", false);
        var otherPack = Path.Combine(Path.GetDirectoryName(sibling.root)!, "another facepack");
        Directory.CreateDirectory(otherPack);
        File.WriteAllText(Path.Combine(otherPack, "config.xml"), "<record><record from=\"../facepack/original\" to=\"graphics/pictures/person/2000026194/portrait\"/></record>");
        var siblingBefore = File.ReadAllBytes(sibling.image);
        var siblingApplied = PortraitCommitService.Apply(Prepare(sibling.root, candidate), Store);
        Check(siblingApplied.IsolatedSharedImage && File.ReadAllBytes(sibling.image).SequenceEqual(siblingBefore), "references from sibling facepacks under graphics also protect shared real-player portraits");

        var nestedRoot = Path.Combine(Sandbox, "nested graphics", "graphics", "outer-pack", "graphics", "inner-pack");
        Directory.CreateDirectory(nestedRoot);
        var nestedImage = Path.Combine(nestedRoot, "original.png");
        Image(nestedImage, 4, 4, 60);
        var nestedConfig = Path.Combine(nestedRoot, "config.xml");
        File.WriteAllText(nestedConfig, $"<record><record from=\"original\" to=\"graphics/pictures/person/{Id}/portrait\"/></record>");
        var outerGraphics = FacepackCatalog.GraphicsRootFor(nestedRoot)!;
        var outerSibling = Path.Combine(outerGraphics, "sibling-pack");
        Directory.CreateDirectory(outerSibling);
        File.WriteAllText(Path.Combine(outerSibling, "config.xml"), "<record><record from=\"../outer-pack/graphics/inner-pack/original\" to=\"graphics/pictures/person/2000026194/portrait\"/></record>");
        var nestedSession = PortraitEditSession.Prepare(Id, nestedRoot, FacepackCatalog.Load(nestedRoot).Find(Id).Single(), "Add light stubble.", Store);
        nestedSession.Import(candidate);
        var nestedApplied = PortraitCommitService.Apply(nestedSession, Store);
        Check(nestedApplied.IsolatedSharedImage && File.Exists(nestedApplied.ImagePath), "shared portraits in nested graphics folders are found from the outer graphics root");

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
        ImageTransferChecks.Run(session, candidate);
        var staleEdit = Path.Combine(Store, "edits", "old-edit");
        Directory.CreateDirectory(staleEdit);
        File.WriteAllText(Path.Combine(staleEdit, "portrait.png"), "old");
        Directory.SetLastWriteTimeUtc(staleEdit, DateTime.UtcNow.AddDays(-8));
        var activeTransaction = Path.Combine(Store, "transactions", "active-old");
        Directory.CreateDirectory(activeTransaction);
        File.WriteAllText(Path.Combine(activeTransaction, "journal.json"), JsonSerializer.Serialize(new PortraitCommitService.Journal { Root = recoveryPack.root, State = "Prepared" }));
        Directory.SetLastWriteTimeUtc(activeTransaction, DateTime.UtcNow.AddDays(-8));
        PortraitCommitService.PruneTemporaryData(Store);
        Check(!Directory.Exists(staleEdit), "startup pruning removes stale edit folders older than retention");
        Check(Directory.Exists(activeTransaction), "pruning retains unresolved transaction journals for recovery");
        var orphanStore = Path.Combine(Sandbox, "orphan-stage-data");
        var orphanJournal = Path.Combine(orphanStore, "transactions", "completed");
        Directory.CreateDirectory(orphanJournal);
        File.WriteAllText(Path.Combine(orphanJournal, "journal.json"), JsonSerializer.Serialize(new PortraitCommitService.Journal { Root = pack.root, State = "RolledBack" }));
        var orphanStage = Path.Combine(pack.root, ".ffm-orphan.tmp");
        File.WriteAllText(orphanStage, "stale stage");
        Check(PortraitCommitService.Recover(orphanStore).Count == 0 && !File.Exists(orphanStage), "recovery removes unreferenced portrait stage files after safe journals resolve");
        var disposable = Prepare(Pack("dispose", false).root, candidate);
        var disposableFolder = disposable.Folder;
        disposable.Dispose();
        Check(!Directory.Exists(disposableFolder), "discarding an edit session deletes its temporary portrait copies");
        Console.WriteLine("Fixtures only: " + Sandbox);
    }
    private static void PromptChecks()
    {
        var current = PortraitPromptBuilder.Build("Keep current hairstyle", "Keep current facial hair", 17, 17);
        Check(current.Contains("No change selected", StringComparison.Ordinal) && !current.Contains("Age the person's appearance", StringComparison.Ordinal), "age +0 keeps the age prompt unchanged");
        var slight = PortraitPromptBuilder.Build("Keep current hairstyle", "Keep current facial hair", 17, 19);
        Check(slight.Contains("barely noticeable", StringComparison.Ordinal) && slight.Contains("subtle natural variation", StringComparison.Ordinal) && slight.Contains("Never add facial hair", StringComparison.Ordinal), "age +2 uses subtle hair variation and never invents facial hair");
        var mature = PortraitPromptBuilder.Build("Keep current hairstyle", "Keep current facial hair", 17, 32);
        Check(mature.Contains("clear but natural age progression", StringComparison.Ordinal) && mature.Contains("mild thinning", StringComparison.Ordinal) && mature.Contains("grey at the temples", StringComparison.Ordinal), "age +15 adds age-scaled natural hair and texture changes");
        var hairstyles = new[] { "Keep current hairstyle", "Buzz cut", "Crew cut", "Textured crop", "Undercut", "Long tied back" };
        var beards = new[] { "Keep current facial hair", "Light stubble", "Heavy stubble", "Short beard", "Full beard", "Goatee", "Circle beard", "Van Dyke", "Balbo" };
        var targets = new int?[] { 17, 19, 22, 27, 32, 37 };
        var combinationsValid = hairstyles.All(hair => beards.All(beard => targets.All(target =>
        {
            var prompt = PortraitPromptBuilder.Build(hair, beard, 17, target);
            var changed = hair != hairstyles[0] || beard != beards[0] || target > 17;
            return prompt.Contains("Preserve the same person's identity", StringComparison.Ordinal) &&
                   prompt.Contains("Return a PNG", StringComparison.Ordinal) &&
                   (!changed || prompt.Contains("Make exactly these requested changes:", StringComparison.Ordinal) && !prompt.Contains("No change selected.", StringComparison.Ordinal)) &&
                   !prompt.Contains("Change only", StringComparison.Ordinal) && !prompt.Contains("Age only", StringComparison.Ordinal);
        })));
        Check(combinationsValid, "all hairstyle, facial-hair and age preset combinations produce a consistent focused prompt");
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
    private static void Image(string path, int width, int height, byte color, bool transparentPixel = true)
    {
        var pixels = new byte[width * height * 4];
        for (var i = 0; i < pixels.Length; i += 4) { pixels[i] = color; pixels[i + 1] = 80; pixels[i + 2] = 150; pixels[i + 3] = transparentPixel && i == 0 ? (byte)0 : (byte)255; }
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
