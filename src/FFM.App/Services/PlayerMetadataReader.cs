using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace FFM.App.Services;

// Native FM27 schema-4000 name/person decoder. Layout reference: rhiever/fmsave (MIT).
internal sealed class PlayerMetadataReader
{
    private readonly byte[] _data;
    private readonly int[][] _pools;
    private readonly DateOnly _clock;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly byte[] ZeroRegion = new byte[7];
    public int PoolsEnd { get; }
    public sealed record Metadata(string? FirstName, string? LastName, string? CommonName, DateOnly BirthDate, int Age, int NationId);

    public PlayerMetadataReader(byte[] data, DateOnly clock)
    {
        _data = data;
        _clock = clock;
        int[][]? found = null;
        var end = 0;
        ReadOnlySpan<byte> anchor = [0x55, 0xb5, 0, 0]; // pool-capacity signature
        var cursor = 0;
        while (cursor < data.Length - 24)
        {
            var relative = data.AsSpan(cursor).IndexOf(anchor);
            if (relative < 0) break;
            var at = cursor + relative;
            cursor = at + 1;
            if (at + 24 > data.Length) break;
            var scaleWord = U32(at + 12);
            if (scaleWord < 256 || scaleWord > 256 * 1024 || scaleWord % 256 != 0) continue;
            var scale = scaleWord / 256;
            if ((U32(at + 8) & 0xffffff00) != 1024 * scale || U32(at + 16) != 2048 * scale || U32(at + 20) != 0) continue;
            var position = at + 24;
            var pools = new int[3][];
            var valid = true;
            for (var pool = 0; pool < 3 && valid; pool++)
            {
                if (position + 4 > data.Length) { valid = false; break; }
                var count = U32(position);
                position += 4;
                if (count > (data.Length - position) / 8) { valid = false; break; }
                pools[pool] = new int[count];
                for (var i = 0; i < count; i++)
                {
                    if (position + 8 > data.Length || U32(position) != i) { valid = false; break; }
                    var length = U32(position + 4);
                    if (length > 1024 || position + 8L + length > data.Length) { valid = false; break; }
                    pools[pool][i] = position + 4;
                    position += 8 + (int)length;
                }
            }
            if (!valid) continue;
            if (found is not null) throw new InvalidDataException("Multiple valid name-pool groups were found.");
            found = pools;
            end = position;
        }
        _pools = found ?? throw new InvalidDataException("The save's three name pools could not be validated.");
        PoolsEnd = end;
    }

    public Metadata? Read(int start, int end)
    {
        Metadata? found = null;
        for (var birth = Math.Max(start + 19, 19); birth + 40 <= end; birth++)
        {
            var packed = U16(birth);
            var year = U16(birth + 2);
            if (packed is < 1 or > 366 || year < 1900 || year > _clock.Year) continue;
            if (!_data.AsSpan(birth + 15, 7).SequenceEqual(ZeroRegion)) continue;
            var personalityValid = true;
            for (var p = birth + 22; p < birth + 30; p++)
                if (_data[p] is < 1 or > 20) { personalityValid = false; break; }
            if (!personalityValid) continue;
            var legalLength = -1;
            if (U32(birth - 4) == 0) legalLength = 0;
            else for (var length = 2; length <= 80 && birth - 4 - length >= start; length++)
                if (U32(birth - 4 - length) == length) { legalLength = length; break; }
            if (legalLength < 0) continue;
            var block = birth - 19 - legalLength;
            if (block < start || _data[block + 4] != 0 || _data[block + 9] != 0 || _data[block + 14] != 0) continue;
            var first = U32(block);
            var last = U32(block + 5);
            var common = U32(block + 10);
            if (!ValidIndex(0, first) || !ValidIndex(1, last) || !ValidIndex(2, common)) continue;
            if (packed > (DateTime.IsLeapYear(year) ? 366 : 365)) continue;
            var date = new DateOnly(year, 1, 1).AddDays(packed - 1);
            var age = _clock.Year - year - (_clock < date.AddYears(_clock.Year - year) ? 1 : 0);
            if (age < 0 || age > 110) continue;
            var metadata = new Metadata(Name(0, first), Name(1, last), Name(2, common), date, age, _data[birth + 14]);
            if (metadata.FirstName is null && metadata.LastName is null && metadata.CommonName is null) continue;
            // Ambiguous person windows must never attach a guessed identity to a portrait.
            if (found is not null) return null;
            found = metadata;
        }
        return found;
    }

    private bool ValidIndex(int pool, uint index) => index == uint.MaxValue || index < _pools[pool].Length;
    private string? Name(int pool, uint index)
    {
        if (index == uint.MaxValue) return null;
        var lengthAt = _pools[pool][index];
        var length = (int)U32(lengthAt);
        return length == 0 ? null : Utf8.GetString(_data, lengthAt + 4, length);
    }
    private uint U32(int at) => BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(at, 4));
    private ushort U16(int at) => BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(at, 2));
}
