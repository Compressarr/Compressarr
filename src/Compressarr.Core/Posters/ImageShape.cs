namespace Compressarr.Core.Posters;

/// <summary>Reads the size of a downloaded JPEG or PNG without decoding it, so a picture that is not a poster can be refused.</summary>
internal static class ImageShape
{
    /// <summary>A poster is taller than it is wide (OMDb's pictures are 380x562); a landscape picture is a still, a banner or the wrong title.</summary>
    public static bool IsPortrait(byte[] bytes) => TryGetSize(bytes, out var width, out var height) && height > width && width >= 60 && height >= 90;

    public static bool TryGetSize(byte[] bytes, out int width, out int height)
    {
        width = height = 0;

        // PNG: signature, then the IHDR chunk with the size at fixed positions.
        if (bytes.Length > 24 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47)
        {
            width = (bytes[16] << 24) | (bytes[17] << 16) | (bytes[18] << 8) | bytes[19];
            height = (bytes[20] << 24) | (bytes[21] << 16) | (bytes[22] << 8) | bytes[23];
            return width > 0 && height > 0;
        }

        // JPEG: walk the segments to the first "start of frame".
        if (bytes.Length < 4 || bytes[0] != 0xFF || bytes[1] != 0xD8) return false;

        var i = 2;
        while (i + 9 < bytes.Length)
        {
            if (bytes[i] != 0xFF)
            {
                i++;
                continue;
            }

            var marker = bytes[i + 1];
            if (marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC)
            {
                height = (bytes[i + 5] << 8) | bytes[i + 6];
                width = (bytes[i + 7] << 8) | bytes[i + 8];
                return width > 0 && height > 0;
            }

            if (marker == 0xFF || marker == 0x00 || marker == 0x01 || marker is >= 0xD0 and <= 0xD9)
            {
                i += marker == 0xFF ? 1 : 2;    // padding, or a marker with no length
                continue;
            }

            i += 2 + ((bytes[i + 2] << 8) | bytes[i + 3]);
        }

        return false;
    }
}
