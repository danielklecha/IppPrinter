using IppPrinter.Extensions;
using SharpIpp.Protocol.Models;
using SkiaSharp;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace IppPrinter.Services;

public class PwgRasterConverter : IDocumentConverter
{
    public bool CanConvert(DocumentFormat documentFormat) =>
        string.Equals(documentFormat, DocumentFormat.ImagePwgRaster, StringComparison.OrdinalIgnoreCase);

    public Task<int> ConvertAsync(byte[] input, Stream output, string? mediaName = null, CancellationToken cancellationToken = default)
    {
        int pageCount = Convert(input, output);
        return Task.FromResult(pageCount);
    }

    private int Convert(byte[] input, Stream output)
    {
        int offset = 0;
        
        // 1. Read PWG synchronization header (4 bytes: "RaS2")
        if (input.Length - offset < 4)
        {
            throw new InvalidDataException("Invalid PWG Raster synchronization header.");
        }
        string sync = System.Text.Encoding.ASCII.GetString(input, offset, 4);
        offset += 4;
        if (sync != "RaS2")
        {
            throw new InvalidDataException("Invalid PWG Raster synchronization header. Expected 'RaS2'.");
        }

        using var pdfDocument = SKDocument.CreatePdf(output);
        int pageCount = 0;

        // 2. Loop through pages
        while (offset < input.Length)
        {
            // Each page starts with a 1796-byte header
            if (input.Length - offset < 1796)
                break;

            // Parse metadata (Big-Endian)
            uint xResolution = input.ReadUInt32BE(offset + 276);
            uint yResolution = input.ReadUInt32BE(offset + 280);
            uint width = input.ReadUInt32BE(offset + 372);
            uint height = input.ReadUInt32BE(offset + 376);
            uint bitsPerPixel = input.ReadUInt32BE(offset + 388);
            uint bytesPerLine = input.ReadUInt32BE(offset + 392);
            uint colorSpace = input.ReadUInt32BE(offset + 400);
            
            offset += 1796;

            if (width == 0 || height == 0 || bitsPerPixel == 0 || bytesPerLine == 0)
                throw new InvalidDataException("Invalid page dimensions in PWG header.");

            uint bytesPerPixel = (bitsPerPixel + 7) / 8;
            byte[] rgbaPixels = new byte[width * height * 4];

            int bytesPerLineExpected = (int)bytesPerLine;
            byte[] decompressedLine = new byte[bytesPerLineExpected];

            int row = 0;
            while (row < height)
            {
                if (offset >= input.Length)
                    break;

                // Each line starts with a repeat count byte (0 to 255)
                byte lineRepeat = input[offset++];

                // Decompress pixel data (PWG 5102.4 section 4.4): 0-127 repeats the next pixel n + 1
                // times and 129-255 copies 257 - n pixels verbatim. 128 is not defined by the spec; as in
                // CUPS, it fills the rest of the line with white for white-based colour spaces, else zero.
                int bytesWritten = 0;
                while (bytesWritten < bytesPerLineExpected)
                {
                    if (offset >= input.Length)
                        break;

                    byte command = input[offset++];
                    if (command == 128)
                    {
                        byte fill = GetWhiteFill(colorSpace, bitsPerPixel);
                        decompressedLine.AsSpan(bytesWritten, bytesPerLineExpected - bytesWritten).Fill(fill);
                        bytesWritten = bytesPerLineExpected;
                    }
                    else if (command > 128)
                    {
                        // Verbatim copy (257 - command) pixels
                        int count = (257 - command) * (int)bytesPerPixel;
                        int bytesToRead = Math.Min(count, bytesPerLineExpected - bytesWritten);

                        if (offset >= input.Length)
                            break;

                        int available = input.Length - offset;
                        int actualRead = Math.Min(bytesToRead, available);
                        Array.Copy(input, offset, decompressedLine, bytesWritten, actualRead);

                        // Advance offset across the full run payload in the stream, even if the run overshot the line width
                        int bytesToConsume = Math.Min(count, available);
                        offset += bytesToConsume;
                        bytesWritten += actualRead;

                        if (actualRead < bytesToRead)
                            break;
                    }
                    else
                    {
                        // Repeat next single pixel (command + 1) times
                        int repeatCount = command + 1;
                        int pixelSize = (int)bytesPerPixel;
                        if (offset + pixelSize > input.Length)
                            break;

                        ReadOnlySpan<byte> pixelValue = input.AsSpan(offset, pixelSize);
                        offset += pixelSize;

                        for (int i = 0; i < repeatCount && bytesWritten < bytesPerLineExpected; i++)
                        {
                            int bytesToCopy = Math.Min(pixelSize, bytesPerLineExpected - bytesWritten);
                            pixelValue.Slice(0, bytesToCopy).CopyTo(decompressedLine.AsSpan(bytesWritten, bytesToCopy));
                            bytesWritten += bytesToCopy;
                        }
                    }
                }

                // If stream was truncated mid-line, fill remainder with white
                if (bytesWritten < bytesPerLineExpected)
                {
                    decompressedLine.AsSpan(bytesWritten, bytesPerLineExpected - bytesWritten).Fill(GetWhiteFill(colorSpace, bitsPerPixel));
                }

                // Copy decompressedLine to rgbaPixels row, repeating it lineRepeat + 1 times
                int repetitions = lineRepeat + 1;
                for (int r = 0; r < repetitions && row < height; r++)
                {
                    decompressedLine.ConvertLineToRgba(rgbaPixels, row, width, bitsPerPixel, colorSpace);
                    row++;
                }
            }

            pdfDocument.WritePageToPdf(rgbaPixels, (int)width, (int)height, (int)xResolution, (int)yResolution);
            pageCount++;
        }

        pdfDocument.Close();
        return pageCount;
    }

    private static byte GetWhiteFill(uint colorSpace, uint bitsPerPixel)
    {
        if (bitsPerPixel == 1)
            return 0x00; // In 1-bit PWG/CUPS raster, 0 = white (ink off), 1 = black (ink on)
        return IsWhiteBased(colorSpace) ? (byte)0xFF : (byte)0x00;
    }

    // CUPS_CSPACE_W, RGB, RGBW, SW, SRGB and ADOBERGB: the colour spaces where 0xFF is white.
    private static bool IsWhiteBased(uint colorSpace) =>
        colorSpace is 0 or 1 or 17 or 18 or 19 or 20;
}
