using System.Buffers.Binary;
using System.Text;

namespace IppPrinter.Tests.Services;

public static class UrfRasterTestHelper
{
    public static byte[] CreateUrfRaster(
        uint width,
        uint height,
        byte bitsPerPixel,
        byte colorSpace,
        uint dpi,
        byte[] pageBitmapData)
    {
        return CreateMultiPageUrfRaster([(width, height, bitsPerPixel, colorSpace, dpi, pageBitmapData)]);
    }

    public static byte[] CreateMultiPageUrfRaster(
        IEnumerable<(uint width, uint height, byte bitsPerPixel, byte colorSpace, uint dpi, byte[] bitmapData)> pages)
    {
        var pageList = pages.ToList();
        using var ms = new MemoryStream();

        // 1. File header (12 bytes): "UNIRAST\0" + page count (Big Endian)
        ms.Write(Encoding.ASCII.GetBytes("UNIRAST\0"));
        byte[] pageCountBytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(pageCountBytes, (uint)pageList.Count);
        ms.Write(pageCountBytes);

        // 2. Pages
        foreach (var (width, height, bitsPerPixel, colorSpace, dpi, bitmapData) in pageList)
        {
            byte[] header = new byte[32];
            header[0] = bitsPerPixel;
            header[1] = colorSpace;

            // Offset 12: width
            BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(12, 4), width);
            // Offset 16: height
            BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(16, 4), height);
            // Offset 20: dpi
            BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(20, 4), dpi);

            ms.Write(header);
            ms.Write(bitmapData);
        }

        return ms.ToArray();
    }
}

