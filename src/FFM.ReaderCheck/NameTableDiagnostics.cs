using System.Buffers.Binary;
using System.Text;

internal static class NameTableDiagnostics
{
    internal static void Inspect(byte[] db)
    {
        foreach (var term in new[] { "Sam", "Bolton", "José", "Maieco" })
        {
            var needle = Encoding.UTF8.GetBytes(term);
            var cursor = 0;
            var exactCount = 0;
            while (cursor <= db.Length - needle.Length)
            {
                var relative = db.AsSpan(cursor).IndexOf(needle);
                if (relative < 0) break;
                var hit = cursor + relative;
                cursor = hit + needle.Length;
                if (hit < 4 || BinaryPrimitives.ReadUInt32LittleEndian(db.AsSpan(hit - 4, 4)) != needle.Length) continue;
                exactCount++;
                var from = Math.Max(0, hit - 16);
                if (exactCount <= 4) Console.WriteLine($"EXACT COMPONENT {term}: offset={hit}; before={Convert.ToHexString(db.AsSpan(from, hit - from))}; after={Convert.ToHexString(db.AsSpan(hit + needle.Length, Math.Min(16, db.Length - hit - needle.Length)))}");
            }
            Console.WriteLine($"COMPONENT TOTAL {term}: {exactCount}");
        }
        foreach (var id in new uint[] { 2002098250, 2002098449, 2002095290 })
        {
            var needle = new byte[8];
            BinaryPrimitives.WriteUInt32LittleEndian(needle.AsSpan(0, 4), id);
            BinaryPrimitives.WriteUInt32LittleEndian(needle.AsSpan(4, 4), id);
            var cursor = 0;
            while (cursor < db.Length - needle.Length)
            {
                var relative = db.AsSpan(cursor).IndexOf(needle);
                if (relative < 0) break;
                var hit = cursor + relative;
                cursor = hit + needle.Length;
                Console.WriteLine($"DUPLICATED ID {id}@{hit}: {Convert.ToHexString(db.AsSpan(hit - 4, Math.Min(132, db.Length - hit + 4)))}");
            }
        }
    }
}
