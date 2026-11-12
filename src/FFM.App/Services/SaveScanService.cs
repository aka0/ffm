using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using ZstdSharp;

namespace FFM.App.Services;

/// <summary>Read-only FM container inspection. This service never opens the save for writing.</summary>
public sealed class SaveScanService
{
    private static readonly byte[] Magic = [0x02, 0x01, (byte)'f', (byte)'m', (byte)'f', (byte)'.'];
    private const int HeaderSize = 26;
    private const int TrailerHeaderSize = 9;
    private const int TrailerPointerAt = 9;
    private const int MaxTrailerSize = 64 * 1024 * 1024;
    private const int MaxManifestOutput = 64 * 1024 * 1024;
    private const long MaxSectionOutput = 1L * 1024 * 1024 * 1024;
    private const long MaxInfoOutput = 64L * 1024 * 1024;
    private const long MaxCompressedSection = 512L * 1024 * 1024;

    private sealed record DirectoryEntry(string Name, string Extension, long FrameOffset, long CompressedSize, long DecompressedSize);

    public async Task<SaveScanResult> ScanAsync(string path, IProgress<ScanProgress> progress, CancellationToken cancellationToken = default)
    {
        var before = new FileInfo(path);
        var initialLength = before.Length;
        var initialWrite = before.LastWriteTimeUtc;
        progress.Report(new ScanProgress(5, "Opening save read-only…"));
        byte[] header = new byte[HeaderSize];
        byte[] digest;
        ulong trailerPointer;
        IReadOnlyList<DirectoryEntry> entries;

        await using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            if (stream.Length < HeaderSize + TrailerHeaderSize)
                throw new InvalidDataException("This file is too short to be a Football Manager save.");

            var read = 0;
            while (read < HeaderSize)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var count = await stream.ReadAsync(header.AsMemory(read, HeaderSize - read), cancellationToken);
                if (count == 0) throw new EndOfStreamException("The save ended while its header was being read.");
                read += count;
            }
            if (!header.AsSpan(0, Magic.Length).SequenceEqual(Magic))
                throw new InvalidDataException("The selected file does not have the Football Manager save signature.");
            trailerPointer = BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(TrailerPointerAt, 8));
            var trailerOffset64 = checked((long)TrailerPointerAt + (long)trailerPointer);
            if (trailerOffset64 < HeaderSize || trailerOffset64 >= stream.Length - TrailerHeaderSize)
                throw new InvalidDataException("The save directory pointer is invalid. The file may be incomplete or unsupported.");
            var trailerLength = checked((int)(stream.Length - trailerOffset64));
            if (trailerLength > MaxTrailerSize)
                throw new InvalidDataException("The save directory is larger than the supported safety limit.");

            stream.Position = trailerOffset64;
            var trailer = new byte[trailerLength];
            var consumed = 0;
            while (consumed < trailer.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var count = await stream.ReadAsync(trailer.AsMemory(consumed), cancellationToken);
                if (count == 0) throw new EndOfStreamException("The save ended while its directory was being read.");
                consumed += count;
            }
            if (!trailer.AsSpan(0, Magic.Length).SequenceEqual(Magic))
                throw new InvalidDataException("The save directory signature is missing or damaged.");
            digest = SHA256.HashData(Concat(header, trailer));
            progress.Report(new ScanProgress(72, "Reading save directory…"));

            var compressedDirectory = trailer.AsSpan(TrailerHeaderSize).ToArray();
            byte[] directory;
            try
            {
                using var decompressor = new Decompressor();
                directory = DecompressBounded(decompressor, compressedDirectory, MaxManifestOutput, "save directory");
            }
            catch (Exception ex)
            {
                throw new InvalidDataException("The save directory could not be decompressed. This save may use an unsupported format or be incomplete.", ex);
            }
            progress.Report(new ScanProgress(80, "Reading save directory index…"));
            entries = ParseDirectory(directory);
            foreach (var entry in entries)
            {
                var limit = entry.Name == "game_info" && entry.Extension == ".dat" ? MaxInfoOutput : MaxSectionOutput;
                if (entry.FrameOffset < HeaderSize || entry.CompressedSize <= 0 || entry.CompressedSize > MaxCompressedSection || entry.DecompressedSize <= 0 || entry.DecompressedSize > limit || entry.FrameOffset > initialLength || entry.CompressedSize > initialLength - entry.FrameOffset)
                    throw new InvalidDataException($"The {entry.Name} section has invalid offsets or declares {entry.DecompressedSize:N0} output bytes, above the {limit:N0}-byte safety limit.");
                if (entry.DecompressedSize > Math.Max(64L * 1024 * 1024, 64L * entry.CompressedSize))
                    throw new InvalidDataException($"The {entry.Name} section exceeds the decompression safety ratio.");
            }
            cancellationToken.ThrowIfCancellationRequested();
        }

        var gameDb = entries.FirstOrDefault(entry => entry.Name == "game_db" && entry.Extension == ".dat")
            ?? throw new InvalidDataException("The save directory does not contain a game_db section.");
        progress.Report(new ScanProgress(84, "Decompressing game database…"));
        var gameDbDecoded = await ReadAndDecompressEntry(path, gameDb, cancellationToken);
        var gameDbBytes = gameDbDecoded.Bytes;
        var infoEntry = entries.FirstOrDefault(e => e.Name == "game_info" && e.Extension == ".dat")
            ?? throw new InvalidDataException("The save has no game_info section for its in-game date.");
        var infoDecoded = await ReadAndDecompressEntry(path, infoEntry, cancellationToken);
        var gameDate = ReadGameDate(infoDecoded.Bytes);
        cancellationToken.ThrowIfCancellationRequested();
        progress.Report(new ScanProgress(90, $"Scanning player records · {gameDbBytes.Length / (1024d * 1024):N0} MB decoded"));
        var playerScan = await Task.Run(() => new PlayerRecordScanner().Scan(gameDbBytes, progress, cancellationToken, gameDate), cancellationToken);

        var after = new FileInfo(path);
        if (!after.Exists || after.Length != initialLength || after.LastWriteTimeUtc != initialWrite)
            throw new IOException("The save changed while it was being scanned. Close Football Manager or scan a stable copy, then retry.");
        await using (var verify = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, FileOptions.Asynchronous | FileOptions.RandomAccess))
        {
            var verifyHeader = new byte[HeaderSize];
            await ReadExactly(verify, verifyHeader, cancellationToken);
            verify.Position = 9 + (long)trailerPointer;
            var verifyTail = new byte[checked((int)(initialLength - verify.Position))];
            await ReadExactly(verify, verifyTail, cancellationToken);
            if (!SHA256.HashData(Concat(verifyHeader, verifyTail)).SequenceEqual(digest))
                throw new IOException("The save changed while it was being scanned. Close Football Manager or scan a stable copy, then retry.");
        }
        progress.Report(new ScanProgress(99, "Checking save container consistency…"));
        progress.Report(new ScanProgress(100, "Player scan complete"));
        var message = playerScan.ValidatedPlayerRecords == 0
            ? "The FM27 save container was read, but no player records matched the registered schema-4000 layout. The save may use an unsupported build or another FM version. No output was written."
            : $"Loaded {playerScan.NewgenIdsByPrefix:N0} newgens from {playerScan.ValidatedPlayerRecords:N0} player records. In-game date: {gameDate:yyyy-MM-dd}. Names and ages resolved for {playerScan.Players.Count(p => p.Age.HasValue && !string.IsNullOrWhiteSpace(p.Name)):N0} newgens. " +
              (playerScan.AmbiguousCandidates > 0 ? $"Skipped {playerScan.AmbiguousCandidates:N0} ambiguous records. " : "") +
              (playerScan.UndecodableNames > 0 ? $"{playerScan.UndecodableNames:N0} player names contained invalid UTF-8 and were left unresolved. " : "") +
              (gameDbDecoded.DirectorySizeMismatch || infoDecoded.DirectorySizeMismatch ? "A section size entry differed from its compressed frame header; frame output stayed within safety limits. " : "") +
              "Save passed read-only consistency checks.";
        return new SaveScanResult(playerScan.Players, playerScan.AllPlayerIds, message, playerScan.Identities);
    }

    private static DateOnly ReadGameDate(byte[] data)
    {
        uint Word(int at) => at >= 0 && at + 4 <= data.Length ? BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(at, 4)) : throw new InvalidDataException("Incomplete game_info section.");
        var length = Word(8);
        if (length > 64) throw new InvalidDataException("Unsupported game_info database version.");
        var baseAt = 12 + (int)length;
        var count = Word(baseAt + 167);
        if (count > 1024) throw new InvalidDataException("Unsupported game_info filename list.");
        var start = baseAt + 171;
        var cursor = start;
        for (var i = 0; i < count; i++)
        {
            var nameLength = Word(cursor);
            if (nameLength > 32768 || cursor + 4L + nameLength > data.Length) throw new InvalidDataException("Invalid filename in game_info.");
            cursor += 4 + (int)nameLength;
        }
        var raw = Word(baseAt + 174 + cursor - start);
        var day = (int)(raw & 511);
        var year = (int)(raw >> 16);
        if (year is < 1901 or > 2200 || day < 1 || day > (DateTime.IsLeapYear(year) ? 366 : 365)) throw new InvalidDataException("The save's in-game date is unreadable; ages cannot be calculated safely.");
        return new DateOnly(year, 1, 1).AddDays(day - 1);
    }

    private static async Task ReadExactly(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var count = await stream.ReadAsync(buffer[read..], cancellationToken);
            if (count == 0) throw new EndOfStreamException("The save ended unexpectedly during the read-only scan.");
            read += count;
        }
    }

    private static IReadOnlyList<DirectoryEntry> ParseDirectory(byte[] bytes)
    {
        if (bytes.Length < 8) throw new InvalidDataException("The save directory payload is incomplete.");
        var nameLength = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(0, 4));
        if (nameLength == 0 || nameLength > 4096 || 4L + nameLength + 4 > bytes.Length)
            throw new InvalidDataException("The save directory has an invalid save name.");
        var offset = checked((int)(4 + nameLength + 4));
        var entries = new List<DirectoryEntry>();
        var sections = 0;
        while (offset + 4 <= bytes.Length)
        {
            var length = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4));
            if (length == 0 || length > 256) break;
            var extensionLengthOffset = (long)offset + 4 + length;
            var tailOffset = extensionLengthOffset + 8;
            if (tailOffset + 40 > bytes.Length) break;
            if (BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan((int)extensionLengthOffset, 4)) != 4) break;
            var name = System.Text.Encoding.ASCII.GetString(bytes, offset + 4, checked((int)length));
            var nameBytes = bytes.AsSpan(offset + 4, checked((int)length));
            var extensionBytes = bytes.AsSpan((int)extensionLengthOffset + 4, 4);
            if (!IsValidName(nameBytes) || !IsValidExtension(extensionBytes)) break;
            var ext = System.Text.Encoding.ASCII.GetString(extensionBytes);
            if (ext is not (".dat" or ".cmt" or ".apm" or ".scm")) break;
            if (ext is ".dat" or ".cmt") sections++;
            var relativeOffset = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan((int)tailOffset, 8));
            var compressedSize = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan((int)tailOffset + 8, 8));
            var decompressedSize = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan((int)tailOffset + 16, 8));
            if (relativeOffset > long.MaxValue - HeaderSize || compressedSize > long.MaxValue || decompressedSize > long.MaxValue) break;
            entries.Add(new DirectoryEntry(name, ext, HeaderSize + (long)relativeOffset, (long)compressedSize, (long)decompressedSize));
            offset = checked((int)(tailOffset + 40));
        }
        if (sections == 0) throw new InvalidDataException("The save directory does not contain a recognizable data section.");
        if (!entries.Any(entry => entry.Name == "game_db" && entry.Extension == ".dat"))
            throw new InvalidDataException("The save directory is readable, but its game database section is missing.");
        return entries;
    }

    private static bool IsValidName(ReadOnlySpan<byte> value)
    {
        foreach (var character in value)
            if (!((character is >= (byte)'A' and <= (byte)'Z') || (character is >= (byte)'a' and <= (byte)'z') || (character is >= (byte)'0' and <= (byte)'9') || character == (byte)'_')) return false;
        return value.Length > 0;
    }

    private static bool IsValidExtension(ReadOnlySpan<byte> value)
    {
        if (value.Length != 4 || value[0] != (byte)'.') return false;
        for (var i = 1; i < value.Length; i++)
            if (!((value[i] is >= (byte)'A' and <= (byte)'Z') || (value[i] is >= (byte)'a' and <= (byte)'z') || (value[i] is >= (byte)'0' and <= (byte)'9'))) return false;
        return true;
    }

    private static async Task<(byte[] Bytes, bool DirectorySizeMismatch)> ReadAndDecompressEntry(string path, DirectoryEntry entry, CancellationToken cancellationToken)
    {
        if (entry.CompressedSize <= 0 || entry.CompressedSize > MaxCompressedSection || entry.DecompressedSize <= 0 || entry.DecompressedSize > MaxSectionOutput)
            throw new InvalidDataException($"The {entry.Name} section exceeds the supported safety limits.");
        byte[] compressed;
        await using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1024 * 1024, FileOptions.Asynchronous | FileOptions.RandomAccess))
        {
            if (entry.FrameOffset < HeaderSize || entry.FrameOffset + entry.CompressedSize > stream.Length)
                throw new InvalidDataException($"The {entry.Name} frame points outside the save file.");
            compressed = new byte[checked((int)entry.CompressedSize)];
            stream.Position = entry.FrameOffset;
            await ReadExactly(stream, compressed, cancellationToken);
        }
        return await Task.Run(() =>
        {
            using var decompressor = new Decompressor();
            var hardLimit = entry.Name == "game_info" ? MaxInfoOutput : MaxSectionOutput;
            var frameSize = Decompressor.GetDecompressedSize(compressed);
            var outputLimit = frameSize is 0 or ulong.MaxValue ? entry.DecompressedSize : frameSize > (ulong)hardLimit ? throw new InvalidDataException($"The {entry.Name} frame exceeds the supported decompression safety limit.") : (long)frameSize;
            var decompressed = DecompressBounded(decompressor, compressed, outputLimit, entry.Name, hardLimit);
            return (decompressed, decompressed.LongLength != entry.DecompressedSize);
        }, cancellationToken);
    }

    private static byte[] DecompressBounded(Decompressor decompressor, byte[] compressed, long outputLimit, string section, long? absoluteLimit = null)
    {
        var limit = absoluteLimit ?? outputLimit;
        if (outputLimit <= 0 || outputLimit > limit || outputLimit > int.MaxValue) throw new InvalidDataException($"The {section} section exceeds the supported safety limit.");
        var declared = Decompressor.GetDecompressedSize(compressed);
        if (declared != 0 && declared != ulong.MaxValue && declared > (ulong)limit) throw new InvalidDataException($"The {section} frame declares {declared:N0} output bytes, above the {limit:N0}-byte safety limit.");
        var decoded = decompressor.Unwrap(compressed, (int)outputLimit);
        if (decoded.Length == 0 || decoded.Length > outputLimit) throw new InvalidDataException($"The {section} section decoded outside the supported size range.");
        return decoded.ToArray();
    }

    private static byte[] Concat(byte[] left, byte[] right)
    {
        var result = new byte[left.Length + right.Length];
        Buffer.BlockCopy(left, 0, result, 0, left.Length);
        Buffer.BlockCopy(right, 0, result, left.Length, right.Length);
        return result;
    }
}
