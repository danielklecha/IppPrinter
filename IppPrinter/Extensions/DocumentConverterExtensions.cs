using SkiaSharp;
using System;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using IppPrinter.Services;

namespace IppPrinter.Extensions;

public static class DocumentConverterExtensions
{
    private static readonly Regex MediaRegex = new(@"_([0-9.]+)[xX]([0-9.]+)(in|mm)$", RegexOptions.Compiled);

    public static async Task<int> ConvertToPdfAsync(
        this IDocumentConverter converter,
        Stream input,
        Stream output,
        string? mediaName = null,
        CancellationToken cancellationToken = default)
    {
        // 1. Buffer the entire input stream to memory asynchronously to prevent synchronous network I/O block
        using var memoryInput = new MemoryStream();
        await input.CopyToAsync(memoryInput, cancellationToken);
        byte[] inputBytes = memoryInput.ToArray();

        // 2. Buffer output PDF to memory
        using var memoryOutput = new MemoryStream();

        // 3. Delegate to the specific converter
        int pageCount = await converter.ConvertAsync(inputBytes, memoryOutput, mediaName, cancellationToken);

        // 4. Asynchronously write the generated PDF back to the output stream
        memoryOutput.Position = 0;
        await memoryOutput.CopyToAsync(output, cancellationToken);

        return pageCount;
    }

    public static void WritePageToPdf(this SKDocument pdfDocument, byte[] rgbaPixels, int width, int height, int dpiX, int dpiY)
    {
        int dpi = dpiX > 0 ? dpiX : 300;
        float scale = 72f / dpi;
        float pageWidthPoints = width * scale;
        float pageHeightPoints = height * scale;

        using var bitmap = new SKBitmap(width, height);
        System.Runtime.InteropServices.Marshal.Copy(rgbaPixels, 0, bitmap.GetPixels(), rgbaPixels.Length);

        using (var canvas = pdfDocument.BeginPage(pageWidthPoints, pageHeightPoints))
        {
            canvas.Scale(scale, scale);
            canvas.DrawBitmap(bitmap, 0, 0);
        }
        pdfDocument.EndPage();
    }

    public static (float widthPoints, float heightPoints) ParseMediaSize(this string mediaName, float defaultWidth = 595.276f, float defaultHeight = 841.890f)
    {
        var match = MediaRegex.Match(mediaName);
        if (!match.Success)
            return (defaultWidth, defaultHeight);

        if (!float.TryParse(match.Groups[1].Value, CultureInfo.InvariantCulture, out float w) ||
            !float.TryParse(match.Groups[2].Value, CultureInfo.InvariantCulture, out float h))
        {
            return (defaultWidth, defaultHeight);
        }

        string unit = match.Groups[3].Value;
        if (unit == "mm")
        {
            float widthPoints = w * 72f / 25.4f;
            float heightPoints = h * 72f / 25.4f;
            return (widthPoints, heightPoints);
        }
        else if (unit == "in")
        {
            float widthPoints = w * 72f;
            float heightPoints = h * 72f;
            return (widthPoints, heightPoints);
        }

        return (defaultWidth, defaultHeight);
    }
}
