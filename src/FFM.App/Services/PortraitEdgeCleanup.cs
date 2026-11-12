namespace FFM.App.Services;

/// <summary>Conservative cutout-edge correction on straight BGRA pixels; no face-wide color filtering.</summary>
public static class PortraitEdgeCleanup
{
    public static byte[] Apply(byte[] original, byte[] edited, int width, int height, bool cleanEdges, out int correctedPixels, byte[]? edgeReference = null)
    {
        if (width <= 0 || height <= 0 || original.Length != checked(width * height * 4) || edited.Length != original.Length) throw new ArgumentException("Invalid portrait pixel buffers.");
        if (edgeReference is not null)
        {
            if (edgeReference.Length != original.Length) throw new ArgumentException("The edge reference must have the same dimensions.");
            for (var i = 3; i < original.Length; i += 4) if (edgeReference[i] != original[i]) throw new ArgumentException("The edge reference must have the same alpha mask.");
        }
        var output = (byte[])edited.Clone();
        var distance = new byte[width * height];
        var editedHasTransparency = false;
        for (var i = 3; i < edited.Length; i += 4) if (edited[i] < 250) { editedHasTransparency = true; break; }
        var allowed = new bool[distance.Length];
        for (var p = 0; p < allowed.Length; p++) allowed[p] = original[p * 4 + 3] > 0;
        var outputAlpha = new byte[distance.Length];
        for (var p = 0; p < distance.Length; p++)
        {
            outputAlpha[p] = editedHasTransparency ? allowed[p] ? edited[p * 4 + 3] : (byte)0 : original[p * 4 + 3];
            distance[p] = outputAlpha[p] == 0 ? (byte)0 : (byte)4;
        }
        if (cleanEdges)
        {
            for (var y = 0; y < height; y++)
                for (var x = 0; x < width; x++)
                {
                    var i = y * width + x;
                    if (x > 0) distance[i] = (byte)Math.Min(distance[i], distance[i - 1] + 1);
                    if (y > 0) distance[i] = (byte)Math.Min(distance[i], distance[i - width] + 1);
                }
            for (var y = height - 1; y >= 0; y--)
                for (var x = width - 1; x >= 0; x--)
                {
                    var i = y * width + x;
                    if (x + 1 < width) distance[i] = (byte)Math.Min(distance[i], distance[i + 1] + 1);
                    if (y + 1 < height) distance[i] = (byte)Math.Min(distance[i], distance[i + width] + 1);
                }
        }
        correctedPixels = 0;
        for (var p = 0; p < distance.Length; p++)
        {
            var i = p * 4;
            var sourceAlpha = original[i + 3];
            var editedAlpha = edited[i + 3];
            var alpha = outputAlpha[p];
            if (alpha == 0)
            {
                output[i] = original[i]; output[i + 1] = original[i + 1]; output[i + 2] = original[i + 2]; output[i + 3] = 0;
                continue;
            }
            // Never reveal undefined generated RGB at a transparent source edge.
            var missingColor = editedHasTransparency && editedAlpha < Math.Min(sourceAlpha, (byte)16);
            var weight = missingColor ? 1d : !cleanEdges ? 0 : distance[p] <= 2 ? 1 : distance[p] == 3 ? 0.5 : 0;
            var changed = false;
            var reference = cleanEdges && distance[p] <= 3 ? edgeReference ?? original : original;
            for (var c = 0; c < 3; c++)
            {
                var value = (byte)Math.Round(reference[i + c] * weight + edited[i + c] * (1 - weight));
                changed |= value != edited[i + c];
                output[i + c] = value;
            }
            output[i + 3] = alpha;
            if (changed) correctedPixels++;
        }
        return output;
    }
}
