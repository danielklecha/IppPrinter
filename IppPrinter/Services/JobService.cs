using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using IppPrinter.Extensions;
using IppPrinter.Models;
using SharpIpp.Models.Requests;
using SharpIpp.Protocol.Models;
using System.Diagnostics;
using System.IO.Abstractions;
using System.Text;
using System.Text.RegularExpressions;
using UglyToad.PdfPig;

namespace IppPrinter.Services;

public class JobService(
    PrinterService printerService,
    IWebHostEnvironment env,
    IFileSystem fileSystem,
    IOptions<PrinterOptions> printerOptions,
    IEnumerable<IDocumentConverter> converters,
    ILogger<JobService> logger)
{
    private readonly FileExtensionContentTypeProvider _contentTypeProvider = new();

    public async Task ProcessJobAsync(int jobId, CancellationToken cancellationToken)
    {
        var job = await printerService.StartJobProcessingAsync(jobId);
        if (job == null)
            return;
        try
        {
            var jobAttributes = printerService.GetEffectiveJobTemplateAttributes(job);

            var defaultMedia = printerOptions.Value.Media != null && printerOptions.Value.Media.Length > 0 ? printerOptions.Value.Media[0].Value : null;
            var media = jobAttributes?.Media is Media m ? m.Value : defaultMedia;

            int totalPages = 0;
            for (var i = 0; i < job.Requests.Count; i++)
            {
                var prefix = $"{job.Id}.{i}";
                switch (job.Requests[i])
                {
                    case PrintJobRequest printJobRequest:
                        totalPages += await SaveAsync(prefix, printJobRequest, media, cancellationToken);
                        break;
                    case SendDocumentRequest sendJobRequest:
                        totalPages += await SaveAsync(prefix, sendJobRequest, media, cancellationToken);
                        break;
                }
            }
            int? completedImpressions = job.Impressions.HasValue && job.Impressions.Value > 0
                ? job.Impressions.Value
                : (totalPages > 0 ? totalPages : 1);
            await printerService.AddCompletedJobAsync(job.Id, completedImpressions);
        }
        catch (Exception ex)
        {
            await printerService.AddAbortedJobAsync(job.Id, ex);
            throw;
        }
    }

    private Task<int> SaveAsync(string prefix, PrintJobRequest request, string? media, CancellationToken cancellationToken)
    {
        return ProcessAndSaveDocumentAsync(
            prefix,
            request.Document,
            request.OperationAttributes?.DocumentFormat,
            request.OperationAttributes?.DocumentName,
            media,
            cancellationToken);
    }

    private Task<int> SaveAsync(string prefix, SendDocumentRequest request, string? media, CancellationToken cancellationToken)
    {
        return ProcessAndSaveDocumentAsync(
            prefix,
            request.Document,
            request.OperationAttributes?.DocumentFormat,
            request.OperationAttributes?.DocumentName,
            media,
            cancellationToken);
    }

    private async Task<int> ProcessAndSaveDocumentAsync(
        string prefix,
        Stream? document,
        string? documentFormat,
        string? documentName,
        string? media,
        CancellationToken cancellationToken)
    {
        if (document == null)
            return 0;

        if (document.Position > 0)
            document.Seek(0, SeekOrigin.Begin);

        var format = documentFormat;
        var stream = document;
        var finalFormat = format;

        if (string.IsNullOrEmpty(format) || format == "application/octet-stream")
        {
            var sniffed = SniffFormat(stream);
            if (sniffed != null)
            {
                format = sniffed;
                finalFormat = sniffed;
            }
        }

        int pageCount = 1;
        var converter = format != null ? converters.FirstOrDefault(c => c.CanConvert(format)) : null;
        if (converter != null)
        {
            var pdfStream = new MemoryStream();
            try
            {
                pageCount = await converter.ConvertToPdfAsync(stream, pdfStream, media, cancellationToken);
                pdfStream.Seek(0, SeekOrigin.Begin);
                stream = pdfStream;
                finalFormat = "application/pdf";
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to convert document stream from format '{Format}' to PDF.", format);
                pdfStream.Dispose();
                throw;
            }
        }

        var fileName = GetFileName(prefix, documentName, finalFormat);
        var savedPath = await SaveAsync(stream, fileName, cancellationToken);

        if (converter == null && string.Equals(finalFormat, "application/pdf", StringComparison.OrdinalIgnoreCase))
        {
            pageCount = CountPdfPages(stream, savedPath);
        }

        if (stream != document)
        {
            await stream.DisposeAsync();
        }
        return pageCount;
    }

    private int CountPdfPages(Stream stream, string savedPath)
    {
        if (stream.CanSeek)
        {
            try
            {
                var originalPos = stream.Position;
                stream.Seek(0, SeekOrigin.Begin);
                using var document = PdfDocument.Open(stream, new ParsingOptions { UseLenientParsing = true, ClipPaths = false });
                var count = document.NumberOfPages;
                stream.Seek(originalPos, SeekOrigin.Begin);
                if (count > 0)
                    return count;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to parse PDF page count from stream with PdfPig");
            }
        }

        if (fileSystem.File.Exists(savedPath))
        {
            try
            {
                using var fileStream = fileSystem.FileStream.New(savedPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var document = PdfDocument.Open(fileStream, new ParsingOptions { UseLenientParsing = true, ClipPaths = false });
                var count = document.NumberOfPages;
                if (count > 0)
                    return count;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to parse PDF page count from saved file '{SavedPath}' with PdfPig", savedPath);
            }
        }

        return 1;
    }

    private static string? SniffFormat(Stream stream)
    {
        if (!stream.CanSeek)
            return null;

        var originalPosition = stream.Position;
        byte[] buffer = new byte[12];
        int read = 0;
        try
        {
            read = stream.Read(buffer, 0, buffer.Length);
        }
        catch
        {
            return null;
        }
        finally
        {
            stream.Seek(originalPosition, SeekOrigin.Begin);
        }

        if (read < 4)
            return null;

        // 1. PDF: %PDF
        if (buffer[0] == 0x25 && buffer[1] == 0x50 && buffer[2] == 0x44 && buffer[3] == 0x46)
        {
            return "application/pdf";
        }

        // 2. PWG Raster: RaS2
        if (buffer[0] == (byte)'R' && buffer[1] == (byte)'a' && buffer[2] == (byte)'S' && buffer[3] == (byte)'2')
        {
            return "image/pwg-raster";
        }

        // 3. Apple URF: UNIRAST\0
        if (read >= 8 &&
            buffer[0] == (byte)'U' && buffer[1] == (byte)'N' && buffer[2] == (byte)'I' && buffer[3] == (byte)'R' &&
            buffer[4] == (byte)'A' && buffer[5] == (byte)'S' && buffer[6] == (byte)'T' && buffer[7] == 0x00)
        {
            return "image/urf";
        }

        // 4. JPEG: 0xFFD8FF
        if (buffer[0] == 0xFF && buffer[1] == 0xD8 && buffer[2] == 0xFF)
        {
            return "image/jpeg";
        }

        // 5. PNG: 0x89 50 4E 47 0D 0A 1A 0A
        if (read >= 8 &&
            buffer[0] == 0x89 && buffer[1] == 0x50 && buffer[2] == 0x4E && buffer[3] == 0x47 &&
            buffer[4] == 0x0D && buffer[5] == 0x0A && buffer[6] == 0x1A && buffer[7] == 0x0A)
        {
            return "image/png";
        }

        // 6. GIF: GIF8
        if (buffer[0] == (byte)'G' && buffer[1] == (byte)'I' && buffer[2] == (byte)'F' && buffer[3] == (byte)'8')
        {
            return "image/gif";
        }

        // 7. BMP: BM
        if (buffer[0] == (byte)'B' && buffer[1] == (byte)'M')
        {
            return "image/bmp";
        }

        // 8. WebP: RIFFxxxxWEBP
        if (read >= 12 &&
            buffer[0] == (byte)'R' && buffer[1] == (byte)'I' && buffer[2] == (byte)'F' && buffer[3] == (byte)'F' &&
            buffer[8] == (byte)'W' && buffer[9] == (byte)'E' && buffer[10] == (byte)'B' && buffer[11] == (byte)'P')
        {
            return "image/webp";
        }

        // 9. TIFF: II* (0x49 0x49 0x2A 0x00) or MM* (0x4D 0x4D 0x00 0x2A)
        if (read >= 4 &&
            ((buffer[0] == 0x49 && buffer[1] == 0x49 && buffer[2] == 0x2A && buffer[3] == 0x00) ||
             (buffer[0] == 0x4D && buffer[1] == 0x4D && buffer[2] == 0x00 && buffer[3] == 0x2A)))
        {
            return "image/tiff";
        }

        // 10. Plain Text: Check if the buffer consists of printable ASCII/UTF-8
        if (read >= 4 && IsPrintableText(buffer.AsSpan(0, read)))
        {
            return "text/plain";
        }

        return null;
    }

    private static bool IsPrintableText(ReadOnlySpan<byte> bytes)
    {
        foreach (var b in bytes)
        {
            if (b < 32 && b != 9 && b != 10 && b != 13)
                return false;
        }
        return true;
    }


    private string GetFileName(string prefix, string? documentName, string? documentFormat)
    {
        var extension = documentFormat is null
            ? null
            : _contentTypeProvider.Mappings.Where(x => string.Equals(x.Value, documentFormat, StringComparison.OrdinalIgnoreCase)).Select(x => x.Key).FirstOrDefault();
        return $"{prefix}_{documentName ?? "no-name"}{extension ?? ".unknown"}";
    }

    public string GetJobsPath()
    {
        var jobsPath = printerOptions.Value.JobsPath;
        if (!string.IsNullOrWhiteSpace(jobsPath))
        {
            jobsPath = Environment.ExpandEnvironmentVariables(jobsPath);
            if (!fileSystem.Path.IsPathRooted(jobsPath))
            {
                jobsPath = fileSystem.Path.Combine(env.ContentRootPath, jobsPath);
            }
        }
        else
        {
            jobsPath = fileSystem.Path.Combine(env.ContentRootPath, "jobs");
        }
        return jobsPath;
    }

    public void VerifyJobsPathAccess()
    {
        var jobsPath = GetJobsPath();
        try
        {
            fileSystem.Directory.CreateDirectory(jobsPath);
            var tempFile = fileSystem.Path.Combine(jobsPath, $".write_test_{Guid.NewGuid():N}");
            fileSystem.File.WriteAllText(tempFile, "temp");
            fileSystem.File.Delete(tempFile);
            logger.LogInformation("Successfully verified write access to JobsPath: {JobsPath}", jobsPath);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to verify write access to JobsPath: {JobsPath}. Please check permissions.", jobsPath);
        }
    }

    private async Task<string> SaveAsync(Stream stream, string fileName, CancellationToken cancellationToken)
    {
        var jobsPath = GetJobsPath();
        var path = fileSystem.Path.Combine(jobsPath, fileName);
        fileSystem.Directory.CreateDirectory(jobsPath);
        using (var fileStream = fileSystem.FileStream.New(path, FileMode.OpenOrCreate))
        {
            await stream.CopyToAsync(fileStream, cancellationToken);
        }

        ExecutePostProcess(path);
        return path;
    }

    private void ExecutePostProcess(string filePath)
    {
        var options = printerOptions.Value;
        var processName = options.PostProcessName;
        if (string.IsNullOrWhiteSpace(processName))
        {
            return;
        }

        try
        {
            string arguments = string.IsNullOrWhiteSpace(options.PostProcessArguments)
                ? $"\"{filePath}\""
                : FormatFilePathPlaceholders(options.PostProcessArguments, filePath);

            logger.LogDebug("Executing PostProcess '{ProcessName}' for file '{FilePath}' with arguments: {Arguments}", processName, filePath, arguments);

            var startInfo = new ProcessStartInfo
            {
                FileName = processName,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(startInfo);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to execute PostProcess '{ProcessName}' for file '{FilePath}'", processName, filePath);
        }
    }

    private static string FormatFilePathPlaceholders(string input, string filePath)
    {
        if (input.Contains("{fullName}", StringComparison.OrdinalIgnoreCase))
        {
            return input.Replace("{fullName}", filePath, StringComparison.OrdinalIgnoreCase);
        }
        return $"{input} \"{filePath}\"";
    }
}