using IppPrinter.Extensions;
using SkiaSharp;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace IppPrinter.Services;

public class TextConverter : IDocumentConverter
{
    public bool CanConvert(string mimeType) =>
        string.Equals(mimeType, "text/plain", StringComparison.OrdinalIgnoreCase);

    public Task<int> ConvertAsync(
        byte[] input,
        Stream output,
        string? mediaName = null,
        CancellationToken cancellationToken = default)
    {
        var text = System.Text.Encoding.UTF8.GetString(input);
        var lines = text.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);

        // Determine target page size in points (default: A4 = 595.276 x 841.890 points)
        float defaultPageWidth = 595.276f;
        float defaultPageHeight = 841.890f;
        var (pageWidth, pageHeight) = (mediaName ?? "iso_a4_210x297mm").ParseMediaSize(defaultPageWidth, defaultPageHeight);

        // Define margins (0.5 inch / 36 points)
        float margin = 36f;
        float printWidth = pageWidth - (margin * 2);

        // Use Courier (monospaced) for standard text file representation
        using var typeface = SKTypeface.FromFamilyName("Courier New") ?? SKTypeface.FromFamilyName("monospace") ?? SKTypeface.Default;
        float fontSize = 10f;

        using var font = new SKFont(typeface, fontSize);
        using var paint = new SKPaint
        {
            Color = SKColors.Black,
            IsAntialias = true
        };

        float fontSpacing = font.Spacing;
        using var pdfDocument = SKDocument.CreatePdf(output);

        float x = margin;
        float y = margin + fontSpacing;
        SKCanvas? canvas = null;
        int pageCount = 0;

        void StartNewPage()
        {
            if (canvas != null)
            {
                pdfDocument.EndPage();
            }
            canvas = pdfDocument.BeginPage(pageWidth, pageHeight);
            pageCount++;
            y = margin + fontSpacing;
        }

        StartNewPage();

        foreach (var rawLine in lines)
        {
            if (cancellationToken.IsCancellationRequested)
                break;

            var wrappedLines = WrapText(rawLine, font, paint, printWidth);
            foreach (var line in wrappedLines)
            {
                if (y > pageHeight - margin)
                {
                    StartNewPage();
                }

                canvas!.DrawText(line, x, y, font, paint);
                y += fontSpacing;
            }
        }

        if (canvas != null)
        {
            pdfDocument.EndPage();
        }

        pdfDocument.Close();
        return Task.FromResult(pageCount);
    }

    private static List<string> WrapText(string text, SKFont font, SKPaint paint, float maxWidth)
    {
        var result = new List<string>();
        if (string.IsNullOrEmpty(text))
        {
            result.Add(string.Empty);
            return result;
        }

        int start = 0;
        while (start < text.Length)
        {
            int count = font.BreakText(text.Substring(start), maxWidth, paint);
            if (count == 0)
            {
                count = 1; // force advance at least one character to prevent infinite loop
            }
            result.Add(text.Substring(start, count));
            start += count;
        }

        return result;
    }
}
