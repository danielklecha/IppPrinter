using IppPrinter.Services;
using SharpIpp.Protocol.Models;
using UglyToad.PdfPig;

namespace IppPrinter.Tests.Services;

public class UrfRasterConverterTests
{
    private readonly UrfRasterConverter _converter = new();

    [Fact]
    public void CanConvert_WhenUrfRaster_ReturnsTrue()
    {
        Assert.True(_converter.CanConvert(DocumentFormat.ImageUrf));
    }

    [Theory]
    [InlineData("application/pdf")]
    [InlineData("application/postscript")]
    [InlineData("image/pwg-raster")]
    public void CanConvert_WhenOtherFormats_ReturnsFalse(string format)
    {
        Assert.False(_converter.CanConvert(format));
    }

    [Fact]
    public async Task ConvertAsync_WhenSyncHeaderInvalid_ThrowsInvalidDataException()
    {
        byte[] input = "BADRAST\0\0\0\0\x01"u8.ToArray();
        using var output = new MemoryStream();

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            _converter.ConvertAsync(input, output));
    }

    [Fact]
    public async Task ConvertAsync_WhenTooShortForSync_ThrowsInvalidDataException()
    {
        byte[] input = [0x55, 0x4E, 0x49];
        using var output = new MemoryStream();

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            _converter.ConvertAsync(input, output));
    }

    [Fact]
    public async Task ConvertAsync_WhenTruncatedHeader_ThrowsInvalidDataException()
    {
        byte[] input = "UNIRAST\0"u8.ToArray(); // 8 bytes, missing page count (4 bytes)
        using var output = new MemoryStream();

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            _converter.ConvertAsync(input, output));
    }

    [Theory]
    [InlineData(0u, 10u, 8, 0)] // width = 0
    [InlineData(10u, 0u, 8, 0)] // height = 0
    [InlineData(10u, 10u, 0, 0)] // bitsPerPixel = 0
    public async Task ConvertAsync_WhenDimensionsInvalid_ThrowsInvalidDataException(
        uint width, uint height, byte bpp, byte colorSpace)
    {
        byte[] input = UrfRasterTestHelper.CreateUrfRaster(
            width, height, bpp, colorSpace, dpi: 300, pageBitmapData: []);
        using var output = new MemoryStream();

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            _converter.ConvertAsync(input, output));
    }

    [Fact]
    public async Task ConvertAsync_RepeatRun_8BitGrayscale_ConvertsSuccessfully()
    {
        // 4x2 page, 8 bpp grayscale (colorSpace = 0), 4 bytes per line
        // Row 1: lineRepeat = 0, command = 3 (repeat next byte 3+1 = 4 times -> 0x80)
        // Row 2: lineRepeat = 0, command = 1 (repeat 2 times -> 0x40), command = 1 (repeat 2 times -> 0xC0)
        byte[] bitmapData =
        [
            0x00, 0x03, 0x80,
            0x00, 0x01, 0x40, 0x01, 0xC0
        ];

        byte[] raster = UrfRasterTestHelper.CreateUrfRaster(
            width: 4, height: 2, bitsPerPixel: 8, colorSpace: 0, dpi: 72, pageBitmapData: bitmapData);

        using var output = new MemoryStream();
        int pageCount = await _converter.ConvertAsync(raster, output);

        Assert.Equal(1, pageCount);
        output.Position = 0;
        using var pdf = PdfDocument.Open(output);
        Assert.Equal(1, pdf.NumberOfPages);
    }

    [Fact]
    public async Task ConvertAsync_VerbatimRun_24BitRgb_PreservesRedAndBlueChannels()
    {
        // 2x1 page, 24 bpp sRGB (colorSpace = 1), 6 bytes per line
        // Row 1: lineRepeat = 0, command = -1 (sbyte)0xFF -> (-(-1) + 1) = 2 pixels verbatim
        // Pixel 1: Red (0xFF, 0x00, 0x00), Pixel 2: Blue (0x00, 0x00, 0xFF)
        byte[] bitmapData =
        [
            0x00,
            0xFF, 0xFF, 0x00, 0x00, 0x00, 0x00, 0xFF
        ];

        byte[] raster = UrfRasterTestHelper.CreateUrfRaster(
            width: 2, height: 1, bitsPerPixel: 24, colorSpace: 1, dpi: 72, pageBitmapData: bitmapData);

        using var output = new MemoryStream();
        int pageCount = await _converter.ConvertAsync(raster, output);

        Assert.Equal(1, pageCount);
        output.Position = 0;
        using var pdf = PdfDocument.Open(output);
        Assert.Equal(1, pdf.NumberOfPages);

        var page = pdf.GetPage(1);
        var images = page.GetImages().ToList();
        Assert.Single(images);
        var image = images[0];
        Assert.True(image.TryGetBytesAsMemory(out var memory));
        var span = memory.Span;

        // Verify pixel 1 is Red (255, 0, 0) and pixel 2 is Blue (0, 0, 255) - not swapped!
        Assert.Equal(255, span[0]); // R
        Assert.Equal(0, span[1]);   // G
        Assert.Equal(0, span[2]);   // B
        Assert.Equal(0, span[3]);   // R
        Assert.Equal(0, span[4]);   // G
        Assert.Equal(255, span[5]); // B
    }

    [Fact]
    public async Task ConvertAsync_CommandMinus128_FillsRemainderOfLineWithPixel()
    {
        // 4x1 page, 8 bpp grayscale (colorSpace = 0), 4 bytes per line
        // Row 1: lineRepeat = 0, command = 0x80 (-128: fill rest of line with pixel 0x77)
        byte[] bitmapData =
        [
            0x00,
            0x80, 0x77
        ];

        byte[] raster = UrfRasterTestHelper.CreateUrfRaster(
            width: 4, height: 1, bitsPerPixel: 8, colorSpace: 0, dpi: 72, pageBitmapData: bitmapData);

        using var output = new MemoryStream();
        int pageCount = await _converter.ConvertAsync(raster, output);

        Assert.Equal(1, pageCount);
        output.Position = 0;
        using var pdf = PdfDocument.Open(output);
        Assert.Equal(1, pdf.NumberOfPages);
    }

    [Fact]
    public async Task ConvertAsync_LineRepeat_RepeatsRowCorrectly()
    {
        // 2x2 page, 8 bpp, 2 bytes per line
        // Row 1 repeated 1 time (lineRepeat = 1 means 2 rows total)
        // Command 1 (repeat pixel 0xAA 2 times)
        byte[] bitmapData =
        [
            0x01, // lineRepeat = 1 (repeats 1+1=2 rows)
            0x01, 0xAA
        ];

        byte[] raster = UrfRasterTestHelper.CreateUrfRaster(
            width: 2, height: 2, bitsPerPixel: 8, colorSpace: 0, dpi: 72, pageBitmapData: bitmapData);

        using var output = new MemoryStream();
        int pageCount = await _converter.ConvertAsync(raster, output);

        Assert.Equal(1, pageCount);
        output.Position = 0;
        using var pdf = PdfDocument.Open(output);
        Assert.Equal(1, pdf.NumberOfPages);
    }

    [Fact]
    public async Task ConvertAsync_VerbatimRunOvershoot_AdvancesOffsetPastFullPayload()
    {
        // 2x2 page, 8 bpp, 2 bytes per line
        // Row 1: lineRepeat = 0, command = 0xFD (-3 as sbyte -> -(-3) + 1 = 4 verbatim pixels)
        // but row only has 2 bytes capacity.
        // Stream provides 4 payload bytes: 0x11, 0x22, 0x33, 0x44.
        // Row 2: lineRepeat = 0, command = 1 (repeat pixel 0x99 2 times).
        // If offset did not advance across the 4 bytes, row 2 would read lineRepeat from payload byte!
        byte[] bitmapData =
        [
            0x00,
            0xFD, 0x11, 0x22, 0x33, 0x44,
            0x00,
            0x01, 0x99
        ];

        byte[] raster = UrfRasterTestHelper.CreateUrfRaster(
            width: 2, height: 2, bitsPerPixel: 8, colorSpace: 0, dpi: 72, pageBitmapData: bitmapData);

        using var output = new MemoryStream();
        int pageCount = await _converter.ConvertAsync(raster, output);

        Assert.Equal(1, pageCount);
        output.Position = 0;
        using var pdf = PdfDocument.Open(output);
        Assert.Equal(1, pdf.NumberOfPages);
    }

    [Fact]
    public async Task ConvertAsync_MultiPageRaster_ConvertsAllPages()
    {
        byte[] page1Bitmap = [0x00, 0x01, 0x10]; // 2x1 page
        byte[] page2Bitmap = [0x00, 0x01, 0x20]; // 2x1 page

        byte[] raster = UrfRasterTestHelper.CreateMultiPageUrfRaster(
        [
            (width: 2, height: 1, bitsPerPixel: 8, colorSpace: 0, dpi: 72, page1Bitmap),
            (width: 2, height: 1, bitsPerPixel: 8, colorSpace: 0, dpi: 72, page2Bitmap)
        ]);

        using var output = new MemoryStream();
        int pageCount = await _converter.ConvertAsync(raster, output);

        Assert.Equal(2, pageCount);
        output.Position = 0;
        using var pdf = PdfDocument.Open(output);
        Assert.Equal(2, pdf.NumberOfPages);
    }

    [Fact]
    public async Task ConvertAsync_TruncatedStream_ExitsGracefullyWithoutCrashing()
    {
        byte[] truncatedBitmap = [0x00, 0x05, 0x12];

        byte[] raster = UrfRasterTestHelper.CreateUrfRaster(
            width: 10, height: 10, bitsPerPixel: 8, colorSpace: 0, dpi: 72, pageBitmapData: truncatedBitmap);

        using var output = new MemoryStream();
        int pageCount = await _converter.ConvertAsync(raster, output);

        Assert.Equal(1, pageCount);
    }
}

