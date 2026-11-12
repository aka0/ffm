using System.Buffers.Binary;

namespace FFM.App.Services;

/// <summary>
/// Focused port of fmsave's FM26/FM27 player-record locator. FM27 build 27.0.0 uses
/// game_db schema 4000 and the registered player-record layout shared with FM26 26.3.2.
/// Upstream: https://github.com/rhiever/fmsave (MIT; see THIRD-PARTY-NOTICES.md).
/// </summary>
public sealed class PlayerRecordScanner
{
    private static ReadOnlySpan<byte> Marker => [0x01, 0x00, 0x6c, 0x07];
    private const int MarkerOffset = 102;
    private const int DecodeExtent = 122;
    public const int AllocatedUidMinimum = 2_000_000_000;
    private const uint MissingReference = uint.MaxValue;

    private readonly record struct Candidate(int RecordOffset, uint Pindex, uint Uid, byte ObjectKind);

    public PlayerRecordScanOutcome Scan(byte[] gameDb, IProgress<ScanProgress> progress, CancellationToken cancellationToken = default, DateOnly? gameDate = null)
    {
        var candidates = new List<Candidate>();
        var cursor = 0;
        var nextReport = 4 * 1024 * 1024;
        var marker = Marker;

        while (cursor <= gameDb.Length - marker.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var hitRelative = gameDb.AsSpan(cursor).IndexOf(marker);
            if (hitRelative < 0) break;
            var hit = cursor + hitRelative;
            cursor = hit + 1;
            if (hit >= nextReport)
            {
                progress.Report(new ScanProgress(92 + 3d * hit / gameDb.Length, $"Scanning player records · {hit / (1024d * 1024):N0} / {gameDb.Length / (1024d * 1024):N0} MB"));
                nextReport = hit + 4 * 1024 * 1024;
            }

            var recordOffset = hit - MarkerOffset;
            if (!AcceptCandidate(gameDb, recordOffset, out var candidate)) continue;
            candidates.Add(candidate);
        }

        var duplicatePindexes = candidates.GroupBy(c => c.Pindex).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();
        var duplicateUids = candidates.GroupBy(c => c.Uid).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();
        var ambiguousCandidates = candidates.RemoveAll(c => duplicatePindexes.Contains(c.Pindex) || duplicateUids.Contains(c.Uid));

        candidates.Sort((left, right) => left.RecordOffset.CompareTo(right.RecordOffset));
        var metadataReader = gameDate.HasValue ? new PlayerMetadataReader(gameDb, gameDate.Value) : null;
        var results = new List<NewgenPlayer>();
        var identities = new List<PlayerRecordIdentity>();
        var allIds = new HashSet<string>(StringComparer.Ordinal);
        var withClosingId = 0;
        var newgenIdsByPrefix = 0;
        var undecodableNames = 0;
        uint? minimumId = null;
        uint? maximumId = null;
        for (var i = 0; i < candidates.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var candidate = candidates[i];
            var windowEnd = i + 1 < candidates.Count ? candidates[i + 1].RecordOffset : gameDb.Length;
            var uniqueId = FindClosingUniqueId(gameDb, candidate, windowEnd, out var closingOffset);
            if (uniqueId.HasValue)
            {
                PlayerMetadataReader.Metadata? metadata;
                try { metadata = metadataReader?.Read(candidate.RecordOffset + 100, closingOffset); }
                catch (System.Text.DecoderFallbackException) { metadata = null; undecodableNames++; }
                identities.Add(new PlayerRecordIdentity(uniqueId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture), candidate.ObjectKind,
                    metadata?.FirstName, metadata?.LastName, metadata?.Age, metadata?.BirthDate, metadata?.NationId));
                allIds.Add(uniqueId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
                withClosingId++;
                minimumId = !minimumId.HasValue || uniqueId.Value < minimumId.Value ? uniqueId.Value : minimumId;
                maximumId = !maximumId.HasValue || uniqueId.Value > maximumId.Value ? uniqueId.Value : maximumId;
                // User's FMRTE validation: generated player IDs start with 2002.
                // Only player objects (kind 2) enter the editable roster.
                if (candidate.ObjectKind == 2 && uniqueId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture).StartsWith("2002", StringComparison.Ordinal))
                {
                    newgenIdsByPrefix++;
                    results.Add(new NewgenPlayer(uniqueId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture), null, null, null,
                        metadata?.FirstName, metadata?.LastName, metadata?.Age, metadata?.BirthDate, metadata?.NationId));
                }
            }
            if (i % 512 == 0 || i + 1 == candidates.Count)
            {
                var denominator = Math.Max(candidates.Count, 1);
                progress.Report(new ScanProgress(95 + 4d * (i + 1) / denominator, $"Resolving player IDs · {i + 1:N0} / {candidates.Count:N0} records"));
            }
        }

        return new PlayerRecordScanOutcome(results, candidates.Count, withClosingId, newgenIdsByPrefix, allIds.ToArray(), minimumId, maximumId, identities, undecodableNames, ambiguousCandidates);
    }

    private static bool AcceptCandidate(byte[] data, int recordOffset, out Candidate candidate)
    {
        candidate = default;
        if (recordOffset < 19 || recordOffset + DecodeExtent > data.Length) return false;
        var record = data.AsSpan(recordOffset, DecodeExtent);
        var pindex = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(recordOffset - 19, 4));
        var uid = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(recordOffset - 15, 4));
        var uidCopy = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(recordOffset - 11, 4));
        if (pindex == MissingReference || uid is 0 or MissingReference || uidCopy is 0 or MissingReference) return false;
        if (uid != uidCopy && (uid < AllocatedUidMinimum || data[recordOffset - 7] is not (2 or 3))) return false;

        var currentAbility = BinaryPrimitives.ReadUInt16LittleEndian(record[..2]);
        var potentialAbility = BinaryPrimitives.ReadInt16LittleEndian(record.Slice(2, 2));
        var objectKind = data[recordOffset - 7];
        if (currentAbility > 200 || potentialAbility is < -10 or > 200) return false;
        var reputationBucket = record[8];
        if (reputationBucket > 200) return false;

        var ratings = record.Slice(24, 15);
        foreach (var value in ratings) if (value is < 1 or > 20) return false;
        var attributes = record.Slice(39, 54);
        var maximumAttribute = 0;
        foreach (var value in attributes)
        {
            if (value is < 1 or > 125) return false;
            maximumAttribute = Math.Max(maximumAttribute, value);
        }
        if (maximumAttribute > 100 && (uid != uidCopy || objectKind is not (2 or 3))) return false;

        candidate = new Candidate(recordOffset, pindex, uid, objectKind);
        return true;
    }

    private static uint? FindClosingUniqueId(byte[] data, Candidate player, int windowEnd, out int closingOffset)
    {
        closingOffset = windowEnd;
        if (player.Pindex == MissingReference - 1) return null;
        var needle = new byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(needle, player.Pindex + 1);
        var start = Math.Clamp(player.RecordOffset + DecodeExtent, 0, Math.Max(data.Length - 12, 0));
        if (windowEnd <= start) return null;
        var end = Math.Clamp(windowEnd, start, data.Length);
        var region = data.AsSpan(start, end - start);
        var cursor = 0;
        uint? found = null;
        while (cursor <= region.Length - needle.Length)
        {
            var relative = region[cursor..].IndexOf(needle);
            if (relative < 0) break;
            var hit = start + cursor + relative;
            cursor += relative + 1;
            if (hit + 12 > data.Length) continue;
            var uid = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(hit + 4, 4));
            var uidCopy = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(hit + 8, 4));
            if (uid != uidCopy || uid is 0 or MissingReference) continue;
            if (found.HasValue) return null;
            found = uid;
            closingOffset = hit;
        }
        return found;
    }
}
