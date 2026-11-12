using FFM.App.Services;

// Isolated facepack fixtures: never write to the user's graphics folder.
var fixture = Path.Combine(Path.GetTempPath(), "FFM.FacepackCheck-" + Guid.NewGuid().ToString("N"));
var root = Path.Combine(fixture, "graphics", "arbitrary root");
var a = Path.Combine(root, "xyz", "nested folder", "José");
var b = Path.Combine(root, "another branch");
Directory.CreateDirectory(a);
Directory.CreateDirectory(b);
File.WriteAllBytes(Path.Combine(a, "portrait one.png"), [1]);
File.WriteAllBytes(Path.Combine(root, "shared.jpg"), [1]);
File.WriteAllText(Path.Combine(a, "config.xml"), """
<record><list id="maps">
 <record from="portrait one" to="graphics/pictures/person/r-2002098250/portrait"/>
 <record from="portrait one.png" to="graphics/pictures/person/2002098250/portrait"/>
 <record from="portrait one" to="graphics/pictures/person/2002098449/icon"/>
</list></record>
""");
File.WriteAllText(Path.Combine(b, "CONFIG.XML"), """
<record><list id="maps">
 <record from="../shared.jpg" to="graphics/pictures/person/r2002098449/portrait"/>
 <record from="missing" to="graphics/pictures/person/2002095290/portrait"/>
 <record from="../shared.jpg" to="graphics/pictures/person/2002069230/portrait"/>
</list></record>
""");
File.WriteAllText(Path.Combine(root, "config.xml"), """
<record><list id="maps">
 <record from="xyz/nested folder/José/portrait one" to="graphics/pictures/person/2002069230/portrait"/>
</list></record>
""");
var unsafeFolder = Path.Combine(root, "unsafe references");
Directory.CreateDirectory(unsafeFolder);
var outside = Path.Combine(fixture, "outside.png");
File.WriteAllBytes(outside, [1]);
var rooted = System.Security.SecurityElement.Escape(outside)!;
File.WriteAllText(Path.Combine(unsafeFolder, "config.xml"), $"<record><record from=\"../../outside.png\" to=\"graphics/pictures/person/2002098250/portrait\"/><record from=\"{rooted}\" to=\"graphics/pictures/person/2002098449/portrait\"/></record>");
var doctypeFolder = Path.Combine(root, "doctype");
Directory.CreateDirectory(doctypeFolder);
File.WriteAllText(Path.Combine(doctypeFolder, "config.xml"), "<!DOCTYPE root [<!ENTITY external \"unsafe\">]><root><record from=\"&external;\" to=\"graphics/pictures/person/2002098250/portrait\"/></root>");
var symlinkCreated = false;
try
{
    File.CreateSymbolicLink(Path.Combine(root, "linked.png"), outside);
    File.WriteAllText(Path.Combine(root, "linked-config.xml"), "<record><record from=\"linked.png\" to=\"graphics/pictures/person/2002095290/portrait\"/></record>");
    symlinkCreated = true;
}
catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException) { }
var malformed = Path.Combine(root, "broken");
Directory.CreateDirectory(malformed);
File.WriteAllText(Path.Combine(malformed, "config.xml"), "<record><record from=\"../shared.jpg\" to=\"graphics/pictures/person/999/portrait\"/><broken>");
var catalog = FacepackCatalog.Load(root);
void Check(bool passes, string label) { if (!passes) throw new Exception(label); Console.WriteLine("PASS: " + label); }
Check(catalog.ConfigCount == (symlinkCreated ? 7 : 6), "recursive discovery ignores folder names and config filename case");
Check(catalog.Find("2002098250").Single().ImagePath == Path.Combine(a, "portrait one.png"), "nested config uses its own folder and aliases deduplicate");
Check(catalog.Find("2002098449").Single().ImagePath == Path.Combine(root, "shared.jpg"), "relative parent path, explicit extension and portrait-only mappings");
Check(catalog.Find("2002095290").Single().ImagePath is null, "missing image is reported without substitution");
Check(catalog.Find("2002069230").Count == 2, "conflicting configs remain ambiguous instead of depending on traversal order");
Check(catalog.Issues.Count == (symlinkCreated ? 5 : 4) && catalog.Find("999").Count == 0, "malformed, DTD, rooted and escaping config references are rejected");
if (symlinkCreated) Check(catalog.Find("2002095290").Single().ImagePath is null, "mapped symlinks are not exposed as editable portraits");
Check(FacepackCatalog.IsInsideGraphics(root) && FacepackCatalog.IsInsideGraphics(Path.GetDirectoryName(root)!) && !FacepackCatalog.IsInsideGraphics(fixture), "graphics root and arbitrary descendants are accepted");
var filtered = FacepackCatalog.Load(root, new HashSet<string> { "2002098250" });
Check(filtered.MappedPlayerCount == 1, "catalog filters to loaded newgen IDs");
using var cancelled = new CancellationTokenSource();
cancelled.Cancel();
try { FacepackCatalog.Load(root, cancellationToken: cancelled.Token); throw new Exception("Cancellation not observed"); }
catch (OperationCanceledException) { Console.WriteLine("PASS: loading supports cancellation"); }
if (args.Length > 0)
{
    var real = FacepackCatalog.Load(args[0], new HashSet<string> { "2002098250", "2002098449", "2002095290", "2002069230" });
    Console.WriteLine($"READ-ONLY REAL PACK: {real.ConfigCount} configs; {real.MappedPlayerCount} mapped sample IDs; {real.Issues.Count} issues");
    foreach (var id in new[] { "2002098250", "2002098449", "2002095290", "2002069230" })
        foreach (var match in real.Find(id)) Console.WriteLine($"{id}: {match.ImagePath ?? "IMAGE MISSING"} [{match.ConfigPath}]");
}
Console.WriteLine("Fixture files: " + fixture);
