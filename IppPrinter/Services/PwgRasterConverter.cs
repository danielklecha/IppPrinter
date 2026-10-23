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

            if (width == 0 || height == 0 || bitsPerPixel == 0)
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

                // Decompress pixel data using PackBits (positive = verbatim copy, negative = repeat)
                int bytesWritten = 0;
                while (bytesWritten < bytesPerLineExpected)
                {
                    if (offset >= input.Length)
                        break;

                    sbyte command = (sbyte)input[offset++];
                    if (command >= 0)
                    {
                        // Verbatim copy (command + 1) pixels
                        int count = (command + 1) * (int)bytesPerPixel;
                        int bytesToRead = Math.Min(count, bytesPerLineExpected - bytesWritten);
                        
                        if (offset + bytesToRead > input.Length)
                            break;
                            
                        Array.Copy(input, offset, decompressedLine, bytesWritten, bytesToRead);
                        offset += bytesToRead;
                        bytesWritten += bytesToRead;
                    }
                    else if (command > -128)
                    {
                        // Repeat next single pixel (-command + 1) times
                        int repeatCount = -command + 1;
                        int pixelSize = (int)bytesPerPixel;
                        if (offset + pixelSize > input.Length)
                            break;
                            
                        // Read pixel value
                        ReadOnlySpan<byte> pixelValue = new ReadOnlySpan<byte>(input, offset, pixelSize);
                        offset += pixelSize;
                        
                        for (int i = 0; i < repeatCount && bytesWritten < bytesPerLineExpected; i++)
                        {
                            int bytesToCopy = Math.Min(pixelSize, bytesPerLineExpected - bytesWritten);
                            pixelValue.Slice(0, bytesToCopy).CopyTo(new Span<byte>(decompressedLine, bytesWritten, bytesToCopy));
                            bytesWritten += bytesToCopy;
                        }
                    }
                    else
                    {
                        // command == -128 is a no-op / ignored in PWG Raster / CUPS Raster
                    }
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
}
