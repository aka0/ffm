using NGMapper.App.Services;
using System.Buffers.Binary;
using System.Reflection;
using System.Text;
using ZstdSharp;
var path = args.Length > 0 ? args[0] : throw new ArgumentException("Pass a save path.");
Console.OutputEncoding = Encoding.UTF8;
var result = await new SaveScanService().ScanAsync(path, new Progress<ScanProgress>(_ => { }));
Console.WriteLine(result.Message);
foreach (var id in new[] { "2002095290", "2000026194", "2000011972", "2002098250", "2002098449", "2002069230" })
{
    var record = result.Identities.FirstOrDefault(p => p.Id == id);
    Console.WriteLine($"METADATA {id}: {record?.FirstName} | {record?.LastName} | age {record?.Age} | DOB {record?.BirthDate:yyyy-MM-dd} | nation index {record?.NationId}");
    Console.WriteLine($"FIXTURE {id}: {(record is null ? "NOT FOUND" : $"object kind {record.ObjectKind}; classified {(record.Id.StartsWith("2002", StringComparison.Ordinal) ? "newgen by ID prefix" : "not newgen by ID prefix")}")}");
}
Console.WriteLine($"Unique IDs with 2002 prefix: {result.AllPlayerIds.Count(id => id.StartsWith("2002", StringComparison.Ordinal)):N0}");
Console.WriteLine($"Unique IDs with 2000 prefix: {result.AllPlayerIds.Count(id => id.StartsWith("2000", StringComparison.Ordinal)):N0}");
Console.WriteLine($"2002 prefix by object kind: {string.Join(", ", result.Identities.Where(x => x.Id.StartsWith("2002", StringComparison.Ordinal)).GroupBy(x => x.ObjectKind).OrderBy(g => g.Key).Select(g => $"{g.Key}={g.Count():N0}"))}");
if (args.Contains("--validate"))
{
    var expected = new[] {
        ("2002095290", "Shakille", "Drakes", 16, true),
        ("2000026194", "Lucas", "Høgsberg", 21, false),
        ("2000011972", "Sávio", "Moreira de Oliveira", 23, false),
        ("2002098250", "Sam", "Bolton", 16, true),
        ("2002098449", "José", "Maieco", 16, true),
        ("2002069230", "David", "Ferreira", 17, true)
    };
    foreach (var (id, first, last, age, newgen) in expected)
    {
        var actual = result.Identities.Single(p => p.Id == id);
        if (actual.FirstName != first || actual.LastName != last || actual.Age != age || result.Players.Any(p => p.Id == id) != newgen)
            throw new InvalidDataException($"Fixture mismatch for {id}: {actual}.");
    }
    if (result.Players.Count != 35292 || result.Players.Select(p => p.Id).Distinct().Count() != result.Players.Count)
        throw new InvalidDataException("The newgen count or uniqueness does not match the supplied FMRTE benchmark.");
    Console.WriteLine("PASS: all six player identities, ages, real-player exclusions and 35,292 unique newgens.");
    return;
}
if (args.Contains("--roster")) return;

// Diagnostic: search raw save bytes and the decoded game_db section for the names.
var raw = await File.ReadAllBytesAsync(path);
var trailerPointer = BinaryPrimitives.ReadUInt64LittleEndian(raw.AsSpan(9, 8));
var trailerOffset = checked((int)(9 + trailerPointer));
var compressedDirectory = raw.AsSpan(trailerOffset + 9).ToArray();
byte[] decodedDirectory;
using (var decompressor = new Decompressor()) decodedDirectory = decompressor.Unwrap(compressedDirectory).ToArray();
var parseDirectory = typeof(SaveScanService).GetMethod("ParseDirectory", BindingFlags.NonPublic | BindingFlags.Static)!;
var entries = ((System.Collections.IEnumerable)parseDirectory.Invoke(null, [decodedDirectory])!).Cast<object>().ToArray();
var readDb = typeof(SaveScanService).GetMethod("ReadAndDecompressEntry", BindingFlags.NonPublic | BindingFlags.Static)!;
var searched = 0;
foreach (var entry in entries.Where(e => (string?)e.GetType().GetProperty("Extension")!.GetValue(e) == ".dat"))
{
    var sectionName = (string)entry.GetType().GetProperty("Name")!.GetValue(entry)!;
    if (args.Contains("--name-components") && sectionName != "game_db") continue;
    var task = (Task<byte[]>)readDb.Invoke(null, [path, entry, CancellationToken.None])!;
    var section = await task;
    searched++;
    if (sectionName == "game_db")
    {
        if (args.Contains("--name-components"))
        {
            NameTableDiagnostics.Inspect(section);
            continue;
        }
        foreach (var idText in new[] { "2002098250", "2002098449", "2002069230" })
        {
            var id = uint.Parse(idText, System.Globalization.CultureInfo.InvariantCulture);
            var le = new byte[4];
            var be = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(le, id);
            BinaryPrimitives.WriteUInt32BigEndian(be, id);
            foreach (var (encoding, needle) in new[] { ("LE", le), ("BE", be) })
            {
                var at = section.AsSpan().IndexOf(needle);
                Console.WriteLine($"IDBYTES {idText} {encoding}: {(at < 0 ? "absent" : $"offset {at:N0}")}");
                if (at >= 0)
                {
                    var from = Math.Max(0, at - 80);
                    var count = Math.Min(section.Length - from, 180);
                    var context = Encoding.UTF8.GetString(section, from, count).Select(c => char.IsControl(c) ? '.' : c).ToArray();
                    Console.WriteLine($"  context@{at:N0}: {new string(context)}");
                }
            }
        }
    }
    foreach (var term in new[] { "Sam", "Bolton", "José", "Maieco", "Jose", "Ferreira" })
    {
        var encodings = new[] { Encoding.UTF8, Encoding.Unicode, Encoding.ASCII };
        var matchEncoding = encodings.FirstOrDefault(enc => section.AsSpan().IndexOf(enc.GetBytes(term)) >= 0);
        if (matchEncoding is not null)
        {
            Console.WriteLine($"TEXT HIT: section={sectionName}; term={term}; encoding={matchEncoding.WebName}; decoded bytes={section.Length:N0}");
            if (sectionName == "game_db")
            {
                var needle = matchEncoding.GetBytes(term);
                var offset = 0;
                for (var shown = 0; shown < 3; shown++)
                {
                    var relative = section.AsSpan(offset).IndexOf(needle);
                    if (relative < 0) break;
                    var hit = offset + relative;
                    var from = Math.Max(0, hit - 48);
                    var count = Math.Min(section.Length - from, needle.Length + 96);
                    var context = Encoding.UTF8.GetString(section, from, count).Select(c => char.IsControl(c) ? '.' : c).ToArray();
                    Console.WriteLine($"  context@{hit:N0}: {new string(context)}");
                    offset = hit + needle.Length;
                }
            }
        }
    }
}
Console.WriteLine($"Searched {searched} decompressed .dat sections for name fragments.");


