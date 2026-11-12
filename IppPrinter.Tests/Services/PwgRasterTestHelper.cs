using System.Buffers.Binary;
using System.Text;

namespace IppPrinter.Tests.Services;

public static class PwgRasterTestHelper
{
    public static byte[] CreatePwgRaster(
        uint width,
        uint height,
        uint bitsPerPixel,
        uint bytesPerLine,
        uint colorSpace,
        uint dpi,
        byte[] pageBitmapData)
    {
        return CreateMultiPagePwgRaster([(width, height, bitsPerPixel, bytesPerLine, colorSpace, dpi, pageBitmapData)]);
    }

    public static byte[] CreateMultiPagePwgRaster(
        IEnumerable<(uint width, uint height, uint bitsPerPixel, uint bytesPerLine, uint colorSpace, uint dpi, byte[] bitmapData)> pages)
    {
        using var ms = new MemoryStream();
        // Synchronization header
        ms.Write(Encoding.ASCII.GetBytes("RaS2"));

        foreach (var (width, height, bitsPerPixel, bytesPerLine, colorSpace, dpi, bitmapData) in pages)
        {
            byte[] header = new byte[1796];

            // Offset 276: HWResolutionX (Big Endian)
            BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(276, 4), dpi);
            // Offset 280: HWResolutionY (Big Endian)
            BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(280, 4), dpi);
            // Offset 372: Page width (Big Endian)
            BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(372, 4), width);
            // Offset 376: Page height (Big Endian)
            BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(376, 4), height);
            // Offset 388: BitsPerPixel (Big Endian)
            BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(388, 4), bitsPerPixel);
            // Offset 392: BytesPerLine (Big Endian)
            BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(392, 4), bytesPerLine);
            // Offset 400: ColorSpace (Big Endian)
            BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(400, 4), colorSpace);

            ms.Write(header);
            ms.Write(bitmapData);
        }

        return ms.ToArray();
    }
}

