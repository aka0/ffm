using FFM.App.Services;
using System.Buffers.Binary;
using System.Reflection;
using System.Text;
using ZstdSharp;
if (args.Length > 0 && args[0] == "--synthetic")
{
    await RunSyntheticChecks();
    return;
}
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

static async Task RunSyntheticChecks()
{
    var folder = Path.Combine(Path.GetTempPath(), "FFM.ReaderCheck-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(folder);
    var scanner = new SaveScanService();
    var validPath = Path.Combine(folder, "valid.fm");
    await File.WriteAllBytesAsync(validPath, CreateSyntheticSave());
    var result = await scanner.ScanAsync(validPath, new Progress<ScanProgress>(_ => { }));
    Check(result.Players.Count == 0 && result.Message.Contains("no player records matched", StringComparison.OrdinalIgnoreCase), "synthetic FM container decompresses and parses the in-game date");
    var badName = new PlayerRecordScanner().Scan(CreatePlayerFixture(invalidUtf8: true), new Progress<ScanProgress>(_ => { }), gameDate: new DateOnly(2027, 12, 29));
    Check(badName.Players.Count == 1 && badName.UndecodableNames == 1 && badName.Players[0].Age is null, "invalid UTF-8 in one player name leaves that player unresolved without aborting scan");
    var duplicate = new PlayerRecordScanner().Scan(CreatePlayerFixture(duplicateCandidates: true), new Progress<ScanProgress>(_ => { }), gameDate: new DateOnly(2027, 12, 29));
    Check(duplicate.ValidatedPlayerRecords == 0 && duplicate.AmbiguousCandidates == 2, "duplicate heuristic candidates are skipped and counted instead of aborting scan");

    var badMagic = CreateSyntheticSave();
    badMagic[0] ^= 0xFF;
    await Reject("bad-magic.fm", badMagic, "invalid save magic is rejected");
    var truncated = CreateSyntheticSave()[..^4];
    await Reject("truncated.fm", truncated, "truncated save directory is rejected");
    await Reject("bomb-metadata.fm", CreateSyntheticSave(declaredGameDb: 1_073_741_825), "decompression bomb metadata is rejected before section allocation");
    await Reject("bad-offset.fm", CreateSyntheticSave(badGameDbOffset: true), "invalid section offsets are rejected before allocation");
    Console.WriteLine("PASS: synthetic save-reader checks complete");

    async Task Reject(string name, byte[] bytes, string message)
    {
        var path = Path.Combine(folder, name);
        await File.WriteAllBytesAsync(path, bytes);
        try { await scanner.ScanAsync(path, new Progress<ScanProgress>(_ => { })); }
        catch (InvalidDataException) { Console.WriteLine("PASS: " + message); return; }
        catch (IOException) { Console.WriteLine("PASS: " + message); return; }
        throw new InvalidDataException("Synthetic reader check failed: " + message);
    }

    static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException("Synthetic reader check failed: " + message);
        Console.WriteLine("PASS: " + message);
    }
}

static byte[] CreateSyntheticSave(long? declaredGameDb = null, bool badGameDbOffset = false)
{
    var magic = new byte[] { 0x02, 0x01, (byte)'f', (byte)'m', (byte)'f', (byte)'.' };
    var gameDb = new byte[36];
    gameDb[0] = 0x55; gameDb[1] = 0xB5;
    BinaryPrimitives.WriteUInt32LittleEndian(gameDb.AsSpan(8, 4), 1024);
    BinaryPrimitives.WriteUInt32LittleEndian(gameDb.AsSpan(12, 4), 256);
    BinaryPrimitives.WriteUInt32LittleEndian(gameDb.AsSpan(16, 4), 2048);
    BinaryPrimitives.WriteUInt32LittleEndian(gameDb.AsSpan(24, 4), 0);
    BinaryPrimitives.WriteUInt32LittleEndian(gameDb.AsSpan(28, 4), 0);
    BinaryPrimitives.WriteUInt32LittleEndian(gameDb.AsSpan(32, 4), 0);
    var gameInfo = new byte[190];
    BinaryPrimitives.WriteUInt32LittleEndian(gameInfo.AsSpan(8, 4), 0);
    BinaryPrimitives.WriteUInt32LittleEndian(gameInfo.AsSpan(179, 4), 0);
    BinaryPrimitives.WriteUInt32LittleEndian(gameInfo.AsSpan(186, 4), (2027u << 16) | 365u);
    using var compressor = new Compressor();
    var compressedDb = compressor.Wrap(gameDb).ToArray();
    var compressedInfo = compressor.Wrap(gameInfo).ToArray();
    var directory = new byte[0];
    using (var stream = new MemoryStream())
    using (var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true))
    {
        var name = Encoding.ASCII.GetBytes("Test");
        writer.Write((uint)name.Length); writer.Write(name); writer.Write(0u);
        WriteEntry(writer, "game_db", 0, compressedDb.Length, declaredGameDb ?? gameDb.Length);
        WriteEntry(writer, "game_info", compressedDb.Length, compressedInfo.Length, gameInfo.Length);
        directory = stream.ToArray();
    }
    var compressedDirectory = compressor.Wrap(directory).ToArray();
    var trailerOffset = 26 + compressedDb.Length + compressedInfo.Length;
    var save = new byte[trailerOffset + 9 + compressedDirectory.Length];
    magic.CopyTo(save, 0);
    BinaryPrimitives.WriteUInt64LittleEndian(save.AsSpan(9, 8), (ulong)(trailerOffset - 9));
    compressedDb.CopyTo(save, 26);
    compressedInfo.CopyTo(save, 26 + compressedDb.Length);
    magic.CopyTo(save, trailerOffset);
    compressedDirectory.CopyTo(save, trailerOffset + 9);
    if (badGameDbOffset)
    {
        // First directory entry relative offset is after the save name and the entry's name/extension prefix.
        var offsetAt = 4 + Encoding.ASCII.GetByteCount("Test") + 4 + 4 + Encoding.ASCII.GetByteCount("game_db") + 8;
        BinaryPrimitives.WriteUInt64LittleEndian(directory.AsSpan(offsetAt, 8), ulong.MaxValue);
        using var replacementCompressor = new Compressor();
        var replacement = replacementCompressor.Wrap(directory).ToArray();
        return [.. save[..(trailerOffset + 9)], .. replacement];
    }
    return save;

    static void WriteEntry(BinaryWriter writer, string name, long relativeOffset, long compressedLength, long decompressedLength)
    {
        var nameBytes = Encoding.ASCII.GetBytes(name);
        writer.Write((uint)nameBytes.Length); writer.Write(nameBytes);
        writer.Write(4u); writer.Write(Encoding.ASCII.GetBytes(".dat"));
        writer.Write((ulong)relativeOffset); writer.Write((ulong)compressedLength); writer.Write((ulong)decompressedLength);
        writer.Write(new byte[16]);
    }
}

static byte[] CreatePlayerFixture(bool invalidUtf8 = false, bool duplicateCandidates = false)
{
    var data = new byte[700];
    data[0] = 0x55; data[1] = 0xB5;
    BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(8, 4), 1024);
    BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(12, 4), 256);
    BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(16, 4), 2048);
    var cursor = 24;
    WritePool(invalidUtf8 ? [0xFF] : Encoding.UTF8.GetBytes("Sam"));
    WritePool(Encoding.UTF8.GetBytes("Bolton"));
    WritePool([]);
    AddCandidate(100);
    if (duplicateCandidates) AddCandidate(350);
    if (!duplicateCandidates)
    {
        var birth = 230;
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(211, 4), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(216, 4), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(221, 4), uint.MaxValue);
        data[215] = data[220] = data[225] = 0;
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(birth - 4, 4), 0);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(birth, 2), 100);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(birth + 2, 2), 2010);
        for (var index = birth + 22; index < birth + 30; index++) data[index] = 10;
        var closing = 350;
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(closing, 4), 101);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(closing + 4, 4), 2_002_098_250);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(closing + 8, 4), 2_002_098_250);
    }
    return data;

    void WritePool(byte[] name)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(cursor, 4), 1); cursor += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(cursor, 4), 0); cursor += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(cursor, 4), (uint)name.Length); cursor += 4;
        name.CopyTo(data, cursor); cursor += name.Length;
    }

    void AddCandidate(int record)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(record - 19, 4), 100);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(record - 15, 4), 500);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(record - 11, 4), 500);
        data[record - 7] = 2;
        for (var i = 24; i < 39; i++) data[record + i] = 10;
        for (var i = 39; i < 93; i++) data[record + i] = 50;
        data[record + 102] = 0x01; data[record + 103] = 0; data[record + 104] = 0x6C; data[record + 105] = 0x07;
    }
}


