using IppPrinter.Extensions;
using SharpIpp.Protocol.Models;
using SkiaSharp;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace IppPrinter.Services;

public class UrfRasterConverter : IDocumentConverter
{
    public bool CanConvert(DocumentFormat documentFormat) =>
        string.Equals(documentFormat, DocumentFormat.ImageUrf, StringComparison.OrdinalIgnoreCase);

    public Task<int> ConvertAsync(byte[] input, Stream output, string? mediaName = null, CancellationToken cancellationToken = default)
    {
        int pageCount = Convert(input, output);
        return Task.FromResult(pageCount);
    }

    private int Convert(byte[] input, Stream output)
    {
        int offset = 0;

        // 1. Read URF file header (12 bytes)
        if (input.Length - offset < 8)
        {
            throw new InvalidDataException("Invalid Apple URF synchronization header.");
        }
        string signature = System.Text.Encoding.ASCII.GetString(input, offset, 7);
        if (signature != "UNIRAST" || input[offset + 7] != 0x00)
        {
            throw new InvalidDataException("Invalid Apple URF synchronization header. Expected 'UNIRAST\\0'.");
        }
        offset += 8;
        
        if (input.Length - offset < 4)
        {
            throw new InvalidDataException("Truncated Apple URF header.");
        }
        uint headerPageCount = input.ReadUInt32BE(offset);
        offset += 4;

        using var pdfDocument = SKDocument.CreatePdf(output);
        int pagesWritten = 0;

        // 2. Loop through pages
        for (int p = 0; p < headerPageCount || (headerPageCount == 0 && offset < input.Length); p++)
        {
            if (offset >= input.Length)
                break;

            // Read 32-byte page header
            if (input.Length - offset < 32)
                throw new InvalidDataException("Truncated Apple URF page header.");

            byte bitsPerPixel = input[offset];
            byte colorSpace = input[offset + 1]; // 0 = sGray, 1 = sRGB

            uint width = input.ReadUInt32BE(offset + 12);
            uint height = input.ReadUInt32BE(offset + 16);
            uint dpi = input.ReadUInt32BE(offset + 20);
            
            offset += 32;

            if (width == 0 || height == 0 || bitsPerPixel == 0)
                throw new InvalidDataException("Invalid page dimensions in URF header.");

            uint bytesPerPixel = (uint)((bitsPerPixel + 7) / 8);
            uint bytesPerLine = width * bytesPerPixel;
            byte[] rgbaPixels = new byte[width * height * 4];

            byte[] decompressedLine = new byte[bytesPerLine];

            int row = 0;
            while (row < height)
            {
                if (offset >= input.Length)
                    break;

                // Each line starts with a repeat count byte
                byte lineRepeat = input[offset++];

                // Decompress using inverted PackBits (positive = repeat, negative = verbatim)
                int bytesWritten = 0;
                while (bytesWritten < bytesPerLine)
                {
                    if (offset >= input.Length)
                        break;

                    sbyte command = (sbyte)input[offset++];
                    if (command >= 0)
                    {
                        // Repeat next single pixel (command + 1) times
                        int repeatCount = command + 1;
                        int pixelSize = (int)bytesPerPixel;
                        if (offset + pixelSize > input.Length)
                            break;
                            
                        ReadOnlySpan<byte> pixelValue = new ReadOnlySpan<byte>(input, offset, pixelSize);
                        offset += pixelSize;
                        
                        for (int i = 0; i < repeatCount && bytesWritten < bytesPerLine; i++)
                        {
                            int bytesToCopy = Math.Min(pixelSize, (int)bytesPerLine - bytesWritten);
                            pixelValue.Slice(0, bytesToCopy).CopyTo(new Span<byte>(decompressedLine, bytesWritten, bytesToCopy));
                            bytesWritten += bytesToCopy;
                        }
                    }
                    else if (command > -128)
                    {
                        // Verbatim copy (-command + 1) pixels
                        int count = (-command + 1) * (int)bytesPerPixel;
                        int bytesToRead = Math.Min(count, (int)bytesPerLine - bytesWritten);
                        if (offset + bytesToRead > input.Length)
                            break;
                            
                        Array.Copy(input, offset, decompressedLine, bytesWritten, bytesToRead);
                        offset += bytesToRead;
                        bytesWritten += bytesToRead;
                    }
                    else // command == -128 (0x80)
                    {
                        // Fill the rest of the line with the next single pixel value
                        int pixelSize = (int)bytesPerPixel;
                        if (offset + pixelSize > input.Length)
                            break;
                            
                        ReadOnlySpan<byte> pixelValue = new ReadOnlySpan<byte>(input, offset, pixelSize);
                        offset += pixelSize;
                        
                        while (bytesWritten < bytesPerLine)
                        {
                            int bytesToCopy = Math.Min(pixelSize, (int)bytesPerLine - bytesWritten);
                            pixelValue.Slice(0, bytesToCopy).CopyTo(new Span<byte>(decompressedLine, bytesWritten, bytesToCopy));
                            bytesWritten += bytesToCopy;
                        }
                    }
                }

                // Copy decompressedLine to rgbaPixels row, repeating it lineRepeat + 1 times
                int repetitions = lineRepeat + 1;
                for (int r = 0; r < repetitions && row < height; r++)
                {
                    // URF Colorspace mapping: 0 -> Grayscale (1), 1 -> sRGB (18)
                    uint urfColorSpace = colorSpace == 0 ? 1u : 18u;
                    decompressedLine.ConvertLineToRgba(rgbaPixels, row, width, bitsPerPixel, urfColorSpace);
                    row++;
                }
            }

            pdfDocument.WritePageToPdf(rgbaPixels, (int)width, (int)height, (int)dpi, (int)dpi);
            pagesWritten++;
        }

        pdfDocument.Close();
        return pagesWritten;
    }
}
