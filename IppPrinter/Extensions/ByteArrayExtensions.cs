namespace IppPrinter.Extensions;

public static class ByteArrayExtensions
{
    public static uint ReadUInt32BE(this byte[] buffer, int offset)
    {
        return ((uint)buffer[offset] << 24) |
               ((uint)buffer[offset + 1] << 16) |
               ((uint)buffer[offset + 2] << 8) |
               (uint)buffer[offset + 3];
    }

    public static void ConvertLineToRgba(this byte[] srcLine, byte[] destPixels, int row, uint width, uint bitsPerPixel, uint colorSpace)
    {
        int destOffset = row * (int)width * 4;
        int srcBytesPerPixel = (int)(bitsPerPixel + 7) / 8;

        for (int col = 0; col < width; col++)
        {
            int srcOffset = col * srcBytesPerPixel;
            byte r = 0, g = 0, b = 0, a = 255;

            if (bitsPerPixel == 1)
            {
                int byteIdx = col / 8;
                int bitIdx = 7 - (col % 8);
                byte bitVal = (byte)((srcLine[byteIdx] >> bitIdx) & 1);
                byte val = bitVal == 0 ? (byte)255 : (byte)0; // 0 = white, 1 = black
                r = val; g = val; b = val;
            }
            else if (bitsPerPixel == 8)
            {
                byte val = srcLine[srcOffset];
                if (colorSpace == 3) // CUPS_CSPACE_K (Device Black: 0 = white / no ink, 255 = full ink)
                {
                    val = (byte)(255 - val);
                }
                r = val; g = val; b = val;
            }
            else if (bitsPerPixel == 24)
            {
                r = srcLine[srcOffset];
                g = srcLine[srcOffset + 1];
                b = srcLine[srcOffset + 2];
            }
            else if (bitsPerPixel == 32)
            {
                if (colorSpace == 19) // sRGBA
                {
                    r = srcLine[srcOffset];
                    g = srcLine[srcOffset + 1];
                    b = srcLine[srcOffset + 2];
                    a = srcLine[srcOffset + 3];
                }
                else if (colorSpace == 6) // CMYK
                {
                    byte c = srcLine[srcOffset];
                    byte m = srcLine[srcOffset + 1];
                    byte y = srcLine[srcOffset + 2];
                    byte k = srcLine[srcOffset + 3];
                    r = (byte)(255 * (1.0 - c / 255.0) * (1.0 - k / 255.0));
                    g = (byte)(255 * (1.0 - m / 255.0) * (1.0 - k / 255.0));
                    b = (byte)(255 * (1.0 - y / 255.0) * (1.0 - k / 255.0));
                }
                else
                {
                    r = srcLine[srcOffset];
                    g = srcLine[srcOffset + 1];
                    b = srcLine[srcOffset + 2];
                    a = srcLine[srcOffset + 3];
                }
            }

            destPixels[destOffset] = r;
            destPixels[destOffset + 1] = g;
            destPixels[destOffset + 2] = b;
            destPixels[destOffset + 3] = a;
            destOffset += 4;
        }
    }
}
