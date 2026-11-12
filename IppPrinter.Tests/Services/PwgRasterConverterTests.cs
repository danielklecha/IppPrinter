using IppPrinter.Services;
using SharpIpp.Protocol.Models;
using UglyToad.PdfPig;

namespace IppPrinter.Tests.Services;

public class PwgRasterConverterTests
{
    private readonly PwgRasterConverter _converter = new();

    [Fact]
    public void CanConvert_WhenPwgRaster_ReturnsTrue()
    {
        Assert.True(_converter.CanConvert(DocumentFormat.ImagePwgRaster));
    }

    [Theory]
    [InlineData("application/pdf")]
    [InlineData("application/postscript")]
    [InlineData("image/urf")]
    public void CanConvert_WhenOtherFormats_ReturnsFalse(string format)
    {
        Assert.False(_converter.CanConvert(format));
    }

    [Fact]
    public async Task ConvertAsync_WhenSyncHeaderInvalid_ThrowsInvalidDataException()
    {
        byte[] input = [0x52, 0x61, 0x53, 0x31]; // "RaS1" instead of "RaS2"
        using var output = new MemoryStream();

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            _converter.ConvertAsync(input, output));
    }

    [Fact]
    public async Task ConvertAsync_WhenTooShortForSync_ThrowsInvalidDataException()
    {
        byte[] input = [0x52, 0x61];
        using var output = new MemoryStream();

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            _converter.ConvertAsync(input, output));
    }

    [Theory]
    [InlineData(0u, 10u, 8u, 10u)] // width = 0
    [InlineData(10u, 0u, 8u, 10u)] // height = 0
    [InlineData(10u, 10u, 0u, 10u)] // bitsPerPixel = 0
    [InlineData(10u, 10u, 8u, 0u)] // bytesPerLine = 0
    public async Task ConvertAsync_WhenDimensionsInvalid_ThrowsInvalidDataException(
        uint width, uint height, uint bpp, uint bpl)
    {
        byte[] input = PwgRasterTestHelper.CreatePwgRaster(
            width, height, bpp, bpl, colorSpace: 19, dpi: 300, pageBitmapData: []);
        using var output = new MemoryStream();

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            _converter.ConvertAsync(input, output));
    }

    [Fact]
    public async Task ConvertAsync_RepeatRun_8BitGrayscale_ConvertsSuccessfully()
    {
        // 4x2 page, 8 bpp grayscale (SW = 18), 4 bytes per line
        // Row 1: repeat 0 (1 line), command 3 (repeat next byte 3+1 = 4 times) -> 0x80
        // Row 2: repeat 0 (1 line), command 1 (repeat 2 times -> 0x40), command 1 (repeat 2 times -> 0xC0)
        byte[] bitmapData =
        [
            0x00, 0x03, 0x80,
            0x00, 0x01, 0x40, 0x01, 0xC0
        ];

        byte[] raster = PwgRasterTestHelper.CreatePwgRaster(
            width: 4, height: 2, bitsPerPixel: 8, bytesPerLine: 4, colorSpace: 18, dpi: 300, pageBitmapData: bitmapData);

        using var output = new MemoryStream();
        int pageCount = await _converter.ConvertAsync(raster, output);

        Assert.Equal(1, pageCount);
        output.Position = 0;
        using var pdf = PdfDocument.Open(output);
        Assert.Equal(1, pdf.NumberOfPages);
    }

    [Fact]
    public async Task ConvertAsync_VerbatimRun_24BitRgb_ConvertsSuccessfully()
    {
        // 2x1 page, 24 bpp sRGB (19), 6 bytes per line
        // Row 1: lineRepeat = 0, command = 255 (257 - 255 = 2 pixels verbatim)
        // Pixel 1: Red (0xFF, 0x00, 0x00), Pixel 2: Blue (0x00, 0x00, 0xFF)
        byte[] bitmapData =
        [
            0x00,
            0xFF, 0xFF, 0x00, 0x00, 0x00, 0x00, 0xFF
        ];

        byte[] raster = PwgRasterTestHelper.CreatePwgRaster(
            width: 2, height: 1, bitsPerPixel: 24, bytesPerLine: 6, colorSpace: 19, dpi: 300, pageBitmapData: bitmapData);

        using var output = new MemoryStream();
        int pageCount = await _converter.ConvertAsync(raster, output);

        Assert.Equal(1, pageCount);
        output.Position = 0;
        using var pdf = PdfDocument.Open(output);
        Assert.Equal(1, pdf.NumberOfPages);
    }

    [Fact]
    public async Task ConvertAsync_Command128_FillsRemainderOfLine()
    {
        // 6x1 page, 8 bpp grayscale (CUPS_CSPACE_W = 0), 6 bytes per line
        // Row 1: lineRepeat = 0, command = 1 (repeat 2 times pixel 0x10), command = 128 (fill remainder 4 bytes with white 0xFF)
        byte[] bitmapData =
        [
            0x00,
            0x01, 0x10,
            0x80 // command 128
        ];

        byte[] raster = PwgRasterTestHelper.CreatePwgRaster(
            width: 6, height: 1, bitsPerPixel: 8, bytesPerLine: 6, colorSpace: 0, dpi: 300, pageBitmapData: bitmapData);

        using var output = new MemoryStream();
        int pageCount = await _converter.ConvertAsync(raster, output);

        Assert.Equal(1, pageCount);
        output.Position = 0;
        using var pdf = PdfDocument.Open(output);
        Assert.Equal(1, pdf.NumberOfPages);
    }

    [Fact]
    public async Task ConvertAsync_Command128_1BitMonochrome_FillsWhiteWithZero()
    {
        // 8x1 page, 1 bpp (CUPS_CSPACE_W = 0), 1 byte per line
        // Row 1: lineRepeat = 0, command = 128 (fills line with 0x00, which in 1-bit is white)
        byte[] bitmapData =
        [
            0x00,
            0x80
        ];

        byte[] raster = PwgRasterTestHelper.CreatePwgRaster(
            width: 8, height: 1, bitsPerPixel: 1, bytesPerLine: 1, colorSpace: 0, dpi: 300, pageBitmapData: bitmapData);

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
        // 2x2 page, 8 bpp (SW = 18), 2 bytes per line
        // Row 1 repeated 1 time (lineRepeat = 1 means 2 rows total)
        // Command 1 (repeat pixel 0xAA 2 times)
        byte[] bitmapData =
        [
            0x01, // lineRepeat = 1 (repeats 1+1=2 rows)
            0x01, 0xAA
        ];

        byte[] raster = PwgRasterTestHelper.CreatePwgRaster(
            width: 2, height: 2, bitsPerPixel: 8, bytesPerLine: 2, colorSpace: 18, dpi: 300, pageBitmapData: bitmapData);

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
        // Row 1: lineRepeat = 0, command = 253 (declares 257 - 253 = 4 verbatim pixels),
        // but row only has 2 bytes capacity.
        // The stream provides 4 payload bytes: 0x11, 0x22, 0x33, 0x44.
        // Row 2: lineRepeat = 0, command = 1 (repeat pixel 0x99 2 times).
        // If offset did not consume all 4 bytes, Row 2 would start reading lineRepeat from 0x33, desyncing!
        byte[] bitmapData =
        [
            0x00,
            0xFD, 0x11, 0x22, 0x33, 0x44, // 4-pixel verbatim run
            0x00,
            0x01, 0x99 // 2nd row
        ];

        byte[] raster = PwgRasterTestHelper.CreatePwgRaster(
            width: 2, height: 2, bitsPerPixel: 8, bytesPerLine: 2, colorSpace: 18, dpi: 300, pageBitmapData: bitmapData);

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
        byte[] page1Bitmap = [0x00, 0x01, 0x10]; // 2x1 page, repeat 0x10 2 times
        byte[] page2Bitmap = [0x00, 0x01, 0x20]; // 2x1 page, repeat 0x20 2 times

        byte[] raster = PwgRasterTestHelper.CreateMultiPagePwgRaster(
        [
            (width: 2, height: 1, bitsPerPixel: 8, bytesPerLine: 2, colorSpace: 18, dpi: 300, page1Bitmap),
            (width: 2, height: 1, bitsPerPixel: 8, bytesPerLine: 2, colorSpace: 18, dpi: 300, page2Bitmap)
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
        // Header declares 10x10 page, but bitmap data is truncated in middle of row 1
        byte[] truncatedBitmap = [0x00, 0x05, 0x12]; // Repeat 6 times, but line is 10 bytes and only 1 pixel provided

        byte[] raster = PwgRasterTestHelper.CreatePwgRaster(
            width: 10, height: 10, bitsPerPixel: 8, bytesPerLine: 10, colorSpace: 18, dpi: 300, pageBitmapData: truncatedBitmap);

        using var output = new MemoryStream();
        // Should not throw unhandled IndexOutOfRange / Overflow exceptions
        int pageCount = await _converter.ConvertAsync(raster, output);

        Assert.Equal(1, pageCount);
    }
}

