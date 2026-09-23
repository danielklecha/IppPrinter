using IppPrinter.Extensions;
using SkiaSharp;
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace IppPrinter.Services;

public class ImageConverter : IDocumentConverter
{
    private static readonly string[] SupportedMimeTypes =
    {
        "image/jpeg",
        "image/png",
        "image/gif",
        "image/webp",
        "image/bmp",
        "image/tiff"
    };

    public bool CanConvert(string mimeType) =>
        SupportedMimeTypes.Contains(mimeType, StringComparer.OrdinalIgnoreCase);

    public Task<int> ConvertAsync(byte[] input, Stream output, string? mediaName = null, CancellationToken cancellationToken = default)
    {
        Convert(input, output, mediaName);
        return Task.FromResult(1);
    }

    private void Convert(byte[] input, Stream output, string? mediaName)
    {
        using var bitmap = SKBitmap.Decode(input);
        if (bitmap == null)
        {
            throw new InvalidDataException("Failed to decode image using SkiaSharp.");
        }

        // Determine target page size in points (default: A4 = 595.276 x 841.890 points)
        float defaultPageWidth = 595.276f;
        float defaultPageHeight = 841.890f;
        var (pageWidth, pageHeight) = (mediaName ?? "iso_a4_210x297mm").ParseMediaSize(defaultPageWidth, defaultPageHeight);

        // Define margins (0.5 inch / 36 points)
        float margin = 36f;
        float maxPrintWidth = pageWidth - (margin * 2);
        float maxPrintHeight = pageHeight - (margin * 2);

        if (maxPrintWidth <= 0 || maxPrintHeight <= 0)
        {
            margin = 0;
            maxPrintWidth = pageWidth;
            maxPrintHeight = pageHeight;
        }

        // Calculate scaling factor to fit page while preserving aspect ratio
        float scaleX = maxPrintWidth / bitmap.Width;
        float scaleY = maxPrintHeight / bitmap.Height;
        float scale = Math.Min(scaleX, scaleY);

        float destWidth = bitmap.Width * scale;
        float destHeight = bitmap.Height * scale;

        // Center image
        float x = margin + (maxPrintWidth - destWidth) / 2f;
        float y = margin + (maxPrintHeight - destHeight) / 2f;

        using var pdfDocument = SKDocument.CreatePdf(output);
        using (var canvas = pdfDocument.BeginPage(pageWidth, pageHeight))
        {
            using var paint = new SKPaint
            {
                IsAntialias = true
            };

            var destRect = new SKRect(x, y, x + destWidth, y + destHeight);
            canvas.DrawBitmap(bitmap, destRect, new SKSamplingOptions(SKCubicResampler.Mitchell), paint);
        }
        pdfDocument.EndPage();
        pdfDocument.Close();
    }
}
