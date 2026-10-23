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
    private readonly FileExtensionContentTypeProvider _contentTypeProvider = InitializeContentTypeProvider();

    private static FileExtensionContentTypeProvider InitializeContentTypeProvider()
    {
        var provider = new FileExtensionContentTypeProvider();
        provider.Mappings[".pwg"] = DocumentFormat.ImagePwgRaster;
        provider.Mappings[".urf"] = DocumentFormat.ImageUrf;
        provider.Mappings[".webp"] = "image/webp";
        provider.Mappings[".tiff"] = "image/tiff";
        provider.Mappings[".tif"] = "image/tiff";
        provider.Mappings[".ps"] = "application/postscript";
        return provider;
    }

    public async Task ProcessJobAsync(int jobId, CancellationToken cancellationToken)
    {
        var job = await printerService.StartJobProcessingAsync(jobId);
        if (job == null)
            return;
        try
        {
            var jobAttributes = printerService.GetEffectiveJobTemplateAttributes(job);

            Media defaultMedia = (printerOptions.Value.Media != null && printerOptions.Value.Media.Length > 0)
                ? printerOptions.Value.Media[0]
                : (Media)"iso_a4_210x297mm";
            Media media = jobAttributes?.Media?.GetValueOrDefault(defaultMedia) ?? defaultMedia;

            string? jobName = job.Requests.Select(x => x switch
            {
                CreateJobRequest createJobRequest => (string?)createJobRequest.OperationAttributes?.JobName,
                PrintJobRequest printJobRequest => (string?)printJobRequest.OperationAttributes?.JobName,
                _ => null,
            }).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));

            int totalPages = 0;
            int documentIndex = 0;
            for (var i = 0; i < job.Requests.Count; i++)
            {
                switch (job.Requests[i])
                {
                    case PrintJobRequest printJobRequest:
                    {
                        var prefix = $"{job.Id}.{documentIndex++}";
                        totalPages += await SaveAsync(prefix, job.Id, printJobRequest, media, jobName, cancellationToken);
                        break;
                    }
                    case SendDocumentRequest sendJobRequest:
                    {
                        var prefix = $"{job.Id}.{documentIndex++}";
                        totalPages += await SaveAsync(prefix, job.Id, sendJobRequest, media, jobName, cancellationToken);
                        break;
                    }
                }
            }

            int copies = jobAttributes?.Copies?.GetValueOrDefault(1) ?? 1;
            if (copies < 1)
                copies = 1;

            int computedImpressions = (totalPages > 0 ? totalPages : 1) * copies;
            int? completedImpressions = job.Impressions.HasValue && job.Impressions.Value > 0
                ? job.Impressions.Value
                : computedImpressions;
            await printerService.AddCompletedJobAsync(job.Id, completedImpressions);
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("Processing of Job {JobId} was canceled.", job.Id);
            throw;
        }
        catch (Exception ex)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                logger.LogInformation("Processing of Job {JobId} was canceled during shutdown.", job.Id);
                throw;
            }

            await printerService.AddAbortedJobAsync(job.Id, ex);
            throw;
        }
    }

    private Task<int> SaveAsync(string prefix, int jobId, PrintJobRequest request, IppValue<Media> media, string? jobName, CancellationToken cancellationToken)
    {
        var candidateName = ResolveEffectiveDocumentName(request.OperationAttributes?.DocumentName, jobName, jobId);
        return ProcessAndSaveDocumentAsync(
            prefix,
            request.Document,
            request.OperationAttributes?.DocumentFormat,
            candidateName,
            media,
            cancellationToken);
    }

    private Task<int> SaveAsync(string prefix, int jobId, SendDocumentRequest request, IppValue<Media> media, string? jobName, CancellationToken cancellationToken)
    {
        var candidateName = ResolveEffectiveDocumentName(request.OperationAttributes?.DocumentName, jobName, jobId);
        return ProcessAndSaveDocumentAsync(
            prefix,
            request.Document,
            request.OperationAttributes?.DocumentFormat,
            candidateName,
            media,
            cancellationToken);
    }

    private static string ResolveEffectiveDocumentName(string? documentName, string? jobName, int jobId)
    {
        if (!string.IsNullOrWhiteSpace(documentName))
            return documentName;
        if (!string.IsNullOrWhiteSpace(jobName))
            return jobName;
        return $"job-{jobId}";
    }

    private async Task<int> ProcessAndSaveDocumentAsync(
        string prefix,
        Stream? document,
        DocumentFormat? documentFormat,
        string? candidateName,
        Media? media,
        CancellationToken cancellationToken)
    {
        if (document == null)
            return 0;

        if (document.CanSeek && document.Position > 0)
            document.Seek(0, SeekOrigin.Begin);

        var stream = document;
        var finalFormat = documentFormat;

        if (!documentFormat.HasValue || documentFormat.Value == DocumentFormat.ApplicationOctetStream)
        {
            var sniffed = SniffDocumentFormat(stream);
            if (sniffed != null)
            {
                documentFormat = sniffed;
                finalFormat = sniffed;
            }
            else
            {
                var defaultFormat = printerOptions.Value.DocumentFormat;
                if (!string.IsNullOrWhiteSpace(defaultFormat))
                {
                    documentFormat = defaultFormat;
                    finalFormat = defaultFormat;
                }
            }
        }

        int pageCount = 1;
        var converter = documentFormat != null ? converters.FirstOrDefault(c => c.CanConvert(documentFormat.Value)) : null;
        if (converter != null)
        {
            var pdfStream = new MemoryStream();
            try
            {
                pageCount = await converter.ConvertToPdfAsync(stream, pdfStream, media, cancellationToken);
                pdfStream.Seek(0, SeekOrigin.Begin);
                stream = pdfStream;
                finalFormat = DocumentFormat.ApplicationPdf;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to convert document stream from format '{Format}' to PDF.", documentFormat);
                await pdfStream.DisposeAsync();
                throw;
            }
        }

        try
        {
            var fileName = GetFileName(prefix, candidateName, finalFormat);
            var savedPath = await SaveAsync(stream, fileName, cancellationToken);

            if (converter == null && string.Equals(finalFormat, DocumentFormat.ApplicationPdf, StringComparison.OrdinalIgnoreCase))
            {
                pageCount = CountPdfPages(savedPath);
            }

            return pageCount;
        }
        finally
        {
            if (stream != document)
            {
                await stream.DisposeAsync();
            }
        }
    }

    private int CountPdfPages(string savedPath)
    {
        if (fileSystem.File.Exists(savedPath))
        {
            try
            {
                using var fileStream = fileSystem.FileStream.New(savedPath, FileMode.Open, FileAccess.Read, FileShare.Read);
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

    private static DocumentFormat? SniffDocumentFormat(Stream stream)
    {
        if (!stream.CanSeek)
            return null;

        var originalPosition = stream.Position;
        byte[] buffer = new byte[512];
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
            return DocumentFormat.ApplicationPdf;
        }

        // 2. PostScript: %!PS
        if (buffer[0] == 0x25 && buffer[1] == 0x21 && buffer[2] == 0x50 && buffer[3] == 0x53)
        {
            return new DocumentFormat("application/postscript");
        }

        // 3. PWG Raster: RaS2
        if (buffer[0] == (byte)'R' && buffer[1] == (byte)'a' && buffer[2] == (byte)'S' && buffer[3] == (byte)'2')
        {
            return DocumentFormat.ImagePwgRaster;
        }

        // 4. Apple URF: UNIRAST\0
        if (read >= 8 &&
            buffer[0] == (byte)'U' && buffer[1] == (byte)'N' && buffer[2] == (byte)'I' && buffer[3] == (byte)'R' &&
            buffer[4] == (byte)'A' && buffer[5] == (byte)'S' && buffer[6] == (byte)'T' && buffer[7] == 0x00)
        {
            return DocumentFormat.ImageUrf;
        }

        // 5. JPEG: 0xFFD8FF
        if (buffer[0] == 0xFF && buffer[1] == 0xD8 && buffer[2] == 0xFF)
        {
            return DocumentFormat.ImageJpeg;
        }

        // 6. PNG: 0x89 50 4E 47 0D 0A 1A 0A
        if (read >= 8 &&
            buffer[0] == 0x89 && buffer[1] == 0x50 && buffer[2] == 0x4E && buffer[3] == 0x47 &&
            buffer[4] == 0x0D && buffer[5] == 0x0A && buffer[6] == 0x1A && buffer[7] == 0x0A)
        {
            return new DocumentFormat("image/png");
        }

        // 7. GIF: GIF8
        if (buffer[0] == (byte)'G' && buffer[1] == (byte)'I' && buffer[2] == (byte)'F' && buffer[3] == (byte)'8')
        {
            return new DocumentFormat("image/gif");
        }

        // 8. BMP: BM
        if (buffer[0] == (byte)'B' && buffer[1] == (byte)'M')
        {
            return new DocumentFormat("image/bmp");
        }

        // 9. WebP: RIFFxxxxWEBP
        if (read >= 12 &&
            buffer[0] == (byte)'R' && buffer[1] == (byte)'I' && buffer[2] == (byte)'F' && buffer[3] == (byte)'F' &&
            buffer[8] == (byte)'W' && buffer[9] == (byte)'E' && buffer[10] == (byte)'B' && buffer[11] == (byte)'P')
        {
            return new DocumentFormat("image/webp");
        }

        // 10. TIFF: II* (0x49 0x49 0x2A 0x00) or MM* (0x4D 0x4D 0x00 0x2A)
        if (read >= 4 &&
            ((buffer[0] == 0x49 && buffer[1] == 0x49 && buffer[2] == 0x2A && buffer[3] == 0x00) ||
             (buffer[0] == 0x4D && buffer[1] == 0x4D && buffer[2] == 0x00 && buffer[3] == 0x2A)))
        {
            return new DocumentFormat("image/tiff");
        }

        // 11. Plain Text: Check for BOM or printable text
        if (IsPlainText(buffer.AsSpan(0, read)))
        {
            return new DocumentFormat("text/plain");
        }

        return null;
    }

    private static bool IsPlainText(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 4)
            return false;

        // UTF-8 BOM
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return true;

        // UTF-16 LE BOM
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return true;

        // UTF-16 BE BOM
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return true;

        // RTF: {\rtf
        if (bytes.Length >= 5 && bytes[0] == (byte)'{' && bytes[1] == (byte)'\\' && bytes[2] == (byte)'r' && bytes[3] == (byte)'t' && bytes[4] == (byte)'f')
            return false;

        // PCL PJL: \x1b%-12345X
        if (bytes[0] == 0x1B)
            return false;

        foreach (var b in bytes)
        {
            if (b == 0)
                return false;
            if (b < 32 && b != 9 && b != 10 && b != 13)
                return false;
        }

        return true;
    }


    private string GetFileName(string prefix, string? candidateName, string? documentFormat)
    {
        var rawName = candidateName ?? "document";

        // Strip directory path components to avoid path traversal
        var safeName = fileSystem.Path.GetFileName(rawName);
        if (string.IsNullOrWhiteSpace(safeName))
        {
            safeName = "document";
        }

        // Sanitize invalid file name characters
        var invalidChars = fileSystem.Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(safeName.Length);
        foreach (var ch in safeName)
        {
            sb.Append(invalidChars.Contains(ch) ? '_' : ch);
        }
        safeName = sb.ToString().Trim();
        if (string.IsNullOrWhiteSpace(safeName))
        {
            safeName = "document";
        }

        // Limit name length to prevent PathTooLongException
        const int maxBaseNameLength = 120;
        if (safeName.Length > maxBaseNameLength)
        {
            safeName = safeName.Substring(0, maxBaseNameLength).TrimEnd();
        }

        var targetExtension = documentFormat is null
            ? null
            : _contentTypeProvider.Mappings
                .Where(x => string.Equals(x.Value, documentFormat, StringComparison.OrdinalIgnoreCase))
                .Select(x => x.Key)
                .FirstOrDefault();

        targetExtension ??= ".unknown";

        // Avoid duplicate extension if safeName already ends with target extension
        if (safeName.EndsWith(targetExtension, StringComparison.OrdinalIgnoreCase))
        {
            return $"{prefix}_{safeName}";
        }

        // If safeName ends with any recognized mime extension, strip it before appending targetExtension
        var existingExt = fileSystem.Path.GetExtension(safeName);
        if (!string.IsNullOrEmpty(existingExt) && _contentTypeProvider.Mappings.ContainsKey(existingExt))
        {
            safeName = fileSystem.Path.GetFileNameWithoutExtension(safeName);
        }

        return $"{prefix}_{safeName}{targetExtension}";
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
        using (var fileStream = fileSystem.FileStream.New(path, FileMode.Create, FileAccess.Write, FileShare.None))
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

        processName = Environment.ExpandEnvironmentVariables(processName.Trim());

        try
        {
            string arguments = string.IsNullOrWhiteSpace(options.PostProcessArguments)
                ? $"\"{filePath}\""
                : FormatFilePathPlaceholders(options.PostProcessArguments, filePath);

            arguments = Environment.ExpandEnvironmentVariables(arguments);

            logger.LogDebug("Executing PostProcess '{ProcessName}' for file '{FilePath}' with arguments: {Arguments}", processName, filePath, arguments);

            var startInfo = new ProcessStartInfo();

            if (OperatingSystem.IsWindows() &&
                (processName.EndsWith(".bat", StringComparison.OrdinalIgnoreCase) || processName.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)))
            {
                startInfo.FileName = "cmd.exe";
                startInfo.Arguments = $"/c \"\"{processName}\" {arguments}\"";
            }
            else
            {
                startInfo.FileName = processName;
                startInfo.Arguments = arguments;
            }

            startInfo.UseShellExecute = false;
            startInfo.CreateNoWindow = true;

            using var process = Process.Start(startInfo);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to execute PostProcess '{ProcessName}' for file '{FilePath}'", processName, filePath);
        }
    }

    private string FormatFilePathPlaceholders(string input, string filePath)
    {
        var dir = fileSystem.Path.GetDirectoryName(filePath) ?? string.Empty;
        var fileName = fileSystem.Path.GetFileName(filePath);

        var result = input;

        // {fullName}
        if (result.Contains("\"{fullName}\"", StringComparison.OrdinalIgnoreCase))
        {
            result = result.Replace("\"{fullName}\"", $"\"{filePath}\"", StringComparison.OrdinalIgnoreCase);
        }
        else if (result.Contains("{fullName}", StringComparison.OrdinalIgnoreCase))
        {
            var replacement = filePath.Contains(' ') ? $"\"{filePath}\"" : filePath;
            result = result.Replace("{fullName}", replacement, StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            result = $"{result} \"{filePath}\"";
        }

        // {directory} / {dir}
        var dirReplacement = dir.Contains(' ') ? $"\"{dir}\"" : dir;
        result = result.Replace("{directory}", dirReplacement, StringComparison.OrdinalIgnoreCase);
        result = result.Replace("{dir}", dirReplacement, StringComparison.OrdinalIgnoreCase);

        // {fileName}
        var fileReplacement = fileName.Contains(' ') ? $"\"{fileName}\"" : fileName;
        result = result.Replace("{fileName}", fileReplacement, StringComparison.OrdinalIgnoreCase);

        return result;
    }
}