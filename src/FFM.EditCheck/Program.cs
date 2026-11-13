using FFM.App.Services;
using System.Buffers.Binary;
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
        var oversizedHeader = Path.Combine(Sandbox, "oversized-header.png");
        Image(oversizedHeader, 1, 1, 50);
        var bombBytes = File.ReadAllBytes(oversizedHeader);
        BinaryPrimitives.WriteUInt32BigEndian(bombBytes.AsSpan(16, 4), 50_000);
        BinaryPrimitives.WriteUInt32BigEndian(bombBytes.AsSpan(20, 4), 50_000);
        BinaryPrimitives.WriteUInt32BigEndian(bombBytes.AsSpan(29, 4), PngCrc32(bombBytes.AsSpan(12, 17)));
        File.WriteAllBytes(oversizedHeader, bombBytes);
        Throws(() => session.Import(oversizedHeader), "oversized PNG dimensions are rejected before pixel allocation");
        var opaque = Path.Combine(Sandbox, "flattened.png");
        Image(opaque, 8, 8, 70, transparentPixel: false);
        Throws<PortraitCutoutRequiredException>(() => session.Import(opaque), "flattened edit requests confirmation before replacing the original cutout");
        session.ImportBytes(File.ReadAllBytes(opaque), cleanEdges: false, useOriginalCutout: true);
        var fallback = PortraitEditSession.LoadBitmap(session.PreviewPath!);
        Check(Alpha(fallback).SequenceEqual(Alpha(PortraitEditSession.LoadBitmap(pack.image))) && Pixels(fallback)[4] != Pixels(PortraitEditSession.LoadBitmap(pack.image))[4], "original-cutout fallback retains the alpha shape while using edited colors");
        var sparseCutout = Pack("sparse-cutout", false);
        Image(sparseCutout.image, 4, 4, 50, transparentPixel: true, transparentPixels: 8);
        using (var sparseSession = PortraitEditSession.Prepare(Id, sparseCutout.root, FacepackCatalog.Load(sparseCutout.root).Find(Id).Single(), "Test cutout", Store))
        {
            Throws<PortraitCutoutRequiredException>(() => sparseSession.Import(candidate), "a few stray transparent pixels do not pass the cutout-retention threshold");
            sparseSession.ImportBytes(File.ReadAllBytes(candidate), cleanEdges: false, useOriginalCutout: true);
            Check(Alpha(PortraitEditSession.LoadBitmap(sparseSession.PreviewPath!)).SequenceEqual(Alpha(PortraitEditSession.LoadBitmap(sparseCutout.image))), "sparse-alpha fallback preserves the complete original cutout");
        }
        var result = PortraitCommitService.Apply(session, Store);
        Check(result.ImagePath == pack.image && File.ReadAllBytes(result.BackupPath).SequenceEqual(original) && Path.GetFileName(result.BackupPath) == "original_prev1.png", "exclusive portrait replacement makes exact sibling prev1 backup first");
        Check(File.ReadAllBytes(pack.config).SequenceEqual(config), "exclusive replacement leaves config unchanged");
        var indexBuilds = PortraitCommitService.ReferenceIndexBuildCount;
        var once = File.ReadAllBytes(pack.image);
        var second = Prepare(pack.root, candidate);
        Check(second.EdgeReferenceCopy is not null, "earliest matching backup is snapshotted as the edge-color reference");
        var result2 = PortraitCommitService.Apply(second, Store);
        Check(PortraitCommitService.ReferenceIndexBuildCount == indexBuilds, "accept reuses cached graphics references while config metadata is unchanged");
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
        var indexPack = Pack("index-invalidation", false);
        PortraitCommitService.Apply(Prepare(indexPack.root, candidate), Store);
        var referenceBuildsBeforeChange = PortraitCommitService.ReferenceIndexBuildCount;
        var newReference = Path.Combine(indexPack.root, "other", "config.xml");
        Directory.CreateDirectory(Path.GetDirectoryName(newReference)!);
        File.WriteAllText(newReference, "<record><record from=\"../original.png\" to=\"graphics/pictures/person/2000026194/portrait\"/></record>");
        var afterConfigChange = PortraitCommitService.Apply(Prepare(indexPack.root, candidate), Store);
        Check(afterConfigChange.IsolatedSharedImage && PortraitCommitService.ReferenceIndexBuildCount == referenceBuildsBeforeChange + 1, "new graphics configs invalidate the shared-portrait reference index before acceptance");
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
        var recovered = PortraitCommitService.RecoverDetailed(Store);
        Check(recovered.Errors.Count == 0 && recovered.RolledBackOperations == 1 && File.ReadAllBytes(recoveryPack.image).SequenceEqual(before), "startup recovery restores the interrupted portrait and reports its rollback");
        journal.State = "Prepared";
        File.WriteAllText(Path.Combine(transaction, "journal.json"), JsonSerializer.Serialize(journal));
        Image(recoveryPack.image, 4, 4, 123);
        var external = File.ReadAllBytes(recoveryPack.image);
        var blockedRecovery = PortraitCommitService.RecoverDetailed(Store);
        Check(blockedRecovery.Errors.Count == 1 && blockedRecovery.RolledBackOperations == 0 && File.ReadAllBytes(recoveryPack.image).SequenceEqual(external), "recovery detects external edits and retains them with backups");
        foreach (var faultPoint in new[] { "prepared-journal", "mutation-1-journaled", "mutation-1-staged", "mutation-1-applied", "mutation-2-journaled", "mutation-2-staged", "mutation-2-applied" })
        {
            var faultPack = Pack("fault-" + faultPoint, false);
            var sourceBefore = File.ReadAllBytes(faultPack.image);
            var faultStore = Path.Combine(Sandbox, "fault-store-" + faultPoint);
            using var faultSession = PortraitEditSession.Prepare(Id, faultPack.root, FacepackCatalog.Load(faultPack.root).Find(Id).Single(), "Test fault recovery", faultStore);
            faultSession.Import(candidate);
            PortraitCommitService.FaultInjector = point => { if (point == faultPoint) throw new PortraitCommitService.SimulatedCrashException(point); };
            try { PortraitCommitService.Apply(faultSession, faultStore); throw new Exception("Fault injection was not reached: " + faultPoint); }
            catch (PortraitCommitService.SimulatedCrashException) { }
            finally { PortraitCommitService.FaultInjector = null; }
            var recoveredFault = PortraitCommitService.RecoverDetailed(faultStore);
            Check(recoveredFault.Errors.Count == 0 && recoveredFault.RolledBackOperations == 1 && File.ReadAllBytes(faultPack.image).SequenceEqual(sourceBefore) && PortraitCommitService.Versions(faultPack.image).Count == 0,
                $"journal recovery returns the facepack to its original state after interruption at {faultPoint}");
        }
        var isolatedFaultPack = Pack("fault-isolated-config", true);
        var isolatedFaultSource = File.ReadAllBytes(isolatedFaultPack.image);
        var isolatedFaultConfig = File.ReadAllBytes(isolatedFaultPack.config);
        var isolatedFaultStore = Path.Combine(Sandbox, "fault-isolated-config-store");
        using (var isolatedFaultSession = PortraitEditSession.Prepare(Id, isolatedFaultPack.root, FacepackCatalog.Load(isolatedFaultPack.root).Find(Id).Single(), "Test isolated rollback", isolatedFaultStore))
        {
            isolatedFaultSession.Import(candidate);
            PortraitCommitService.FaultInjector = point => { if (point == "mutation-3-applied") throw new PortraitCommitService.SimulatedCrashException(point); };
            try { PortraitCommitService.Apply(isolatedFaultSession, isolatedFaultStore); throw new Exception("Config fault injection was not reached."); }
            catch (PortraitCommitService.SimulatedCrashException) { }
            finally { PortraitCommitService.FaultInjector = null; }
        }
        var isolatedRecovery = PortraitCommitService.RecoverDetailed(isolatedFaultStore);
        Check(isolatedRecovery.Errors.Count == 0 && isolatedRecovery.RolledBackOperations == 1 && File.ReadAllBytes(isolatedFaultPack.image).SequenceEqual(isolatedFaultSource) && File.ReadAllBytes(isolatedFaultPack.config).SequenceEqual(isolatedFaultConfig) && !File.Exists(Path.Combine(isolatedFaultPack.root, Id + ".png")), "recovery rolls back an interrupted shared-portrait config isolation");
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
        var current = PortraitPromptBuilder.Build(HairstylePreset.KeepCurrent, FacialHairPreset.KeepCurrent, 17, 17);
        Check(current.Contains("No change selected", StringComparison.Ordinal) && !current.Contains("Age the person's appearance", StringComparison.Ordinal), "age +0 keeps the age prompt unchanged");
        var slight = PortraitPromptBuilder.Build(HairstylePreset.KeepCurrent, FacialHairPreset.KeepCurrent, 17, 19);
        Check(slight.Contains("barely noticeable", StringComparison.Ordinal) && slight.Contains("subtle natural variation", StringComparison.Ordinal) && slight.Contains("Never add facial hair", StringComparison.Ordinal), "age +2 uses subtle hair variation and never invents facial hair");
        var mature = PortraitPromptBuilder.Build(HairstylePreset.KeepCurrent, FacialHairPreset.KeepCurrent, 17, 32);
        Check(mature.Contains("clear but natural age progression", StringComparison.Ordinal) && mature.Contains("mild thinning", StringComparison.Ordinal) && mature.Contains("grey at the temples", StringComparison.Ordinal), "age +15 adds age-scaled natural hair and texture changes");
        var hairstyles = Enum.GetValues<HairstylePreset>();
        var beards = Enum.GetValues<FacialHairPreset>();
        var targets = new int?[] { 17, 19, 22, 27, 32, 37 };
        var combinationsValid = hairstyles.All(hair => beards.All(beard => targets.All(target =>
        {
          var prompt = PortraitPromptBuilder.Build(hair, beard, 17, target);
          var changed = hair != HairstylePreset.KeepCurrent || beard != FacialHairPreset.KeepCurrent || target > 17;
            return prompt.Contains("Preserve the same person's identity", StringComparison.Ordinal) &&
                   prompt.Contains("Return a PNG", StringComparison.Ordinal) &&
                   (!changed || prompt.Contains("Make exactly these requested changes:", StringComparison.Ordinal) && !prompt.Contains("No change selected.", StringComparison.Ordinal)) &&
                   !prompt.Contains("Change only", StringComparison.Ordinal) && !prompt.Contains("Age only", StringComparison.Ordinal);
        })));
        Check(combinationsValid, "all tagged hairstyle, facial-hair and age combinations produce a consistent focused prompt");
        Check(PortraitPromptBuilder.Build(HairstylePreset.TexturedCrop, FacialHairPreset.ShortBeard, 20, 20).Contains("matching the hair color", StringComparison.Ordinal), "facial-hair color follows the selected hairstyle color");
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
    private static void Image(string path, int width, int height, byte color, bool transparentPixel = true, int transparentPixels = 1)
    {
        var pixels = new byte[width * height * 4];
        for (var i = 0; i < pixels.Length; i += 4) { pixels[i] = color; pixels[i + 1] = 80; pixels[i + 2] = 150; pixels[i + 3] = transparentPixel && i / 4 < transparentPixels ? (byte)0 : (byte)255; }
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
    private static uint PngCrc32(ReadOnlySpan<byte> bytes)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var value in bytes)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
        }
        return ~crc;
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception("FAIL: " + message); Console.WriteLine("PASS: " + message); }
    private static void Throws(Action action, string message) { try { action(); } catch (Exception ex) when (ex is IOException or InvalidOperationException or InvalidDataException) { Console.WriteLine("PASS: " + message); return; } throw new Exception("FAIL: " + message); }
    private static void Throws<T>(Action action, string message) where T : Exception { try { action(); } catch (T) { Console.WriteLine("PASS: " + message); return; } throw new Exception("FAIL: " + message); }
}
