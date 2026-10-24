using Microsoft.Extensions.Options;
using SharpIpp;
using SharpIpp.Exceptions;
using SharpIpp.Models;
using SharpIpp.Models.Requests;
using SharpIpp.Models.Responses;
using SharpIpp.Protocol;
using SharpIpp.Protocol.Models;
using IppPrinter.Models;
using System.Collections.Concurrent;
using System.Text;

namespace IppPrinter.Services;

public class PrinterService(
    ISharpIppServer sharpIppServer,
    IHttpContextAccessor httpContextAccessor,
    ILogger<PrinterService> logger,
    IOptions<PrinterOptions> printerOptions,
    IDateTimeOffsetProvider dateTimeOffsetProvider,
    JobQueue jobQueue,
    SubscriptionsService subscriptionsService) : IDisposable, IAsyncDisposable
{
    private bool disposedValue;
    private int _newJobIndex = dateTimeOffsetProvider.UtcNow.Day * 1000;
    private bool _isPaused;
    private readonly ConcurrentDictionary<int, PrinterJob> _jobs = new();
    private readonly DateTimeOffset _startTime = dateTimeOffsetProvider.UtcNow.AddMinutes(-1);

    private int GetNextValue()
    {
        return Interlocked.Increment(ref _newJobIndex);
    }

    private bool CanAddJob()
    {
        int activeJobCount = _jobs.Values.Count(x => x.State == JobState.Pending || x.State == JobState.Processing || x.IsNew || x.IsHold);
        return activeJobCount < printerOptions.Value.MaxPendingJobs;
    }

    private void PruneJobHistory()
    {
        var limit = printerOptions.Value.MaxJobHistory;
        var inactiveJobs = _jobs.Values
            .Where(x => x.State == JobState.Completed || x.State == JobState.Canceled || x.State == JobState.Aborted)
            .OrderBy(x => x.CreatedDateTime)
            .ToList();

        int toRemoveCount = _jobs.Count - limit;
        if (toRemoveCount <= 0)
            return;

        int removedCount = 0;
        foreach (var job in inactiveJobs)
        {
            if (removedCount >= toRemoveCount)
                break;

            if (_jobs.TryRemove(job.Id, out var removedJob))
            {
                removedJob.Dispose();
                removedCount++;
            }
        }
    }

    public async Task ProcessRequestAsync(Stream inputStream, Stream outputStream)
    {
        try
        {
            IIppRequest request = await sharpIppServer.ReceiveRequestAsync(inputStream);
            IIppResponse response = await GetResponseAsync(request);
            IIppResponseMessage rawResponse = await sharpIppServer.CreateRawResponseAsync(response);
            ImproveRawResponse(request, response, rawResponse);
            await sharpIppServer.SendRawResponseAsync(rawResponse, outputStream);
        }
        catch (IppRequestException ex)
        {
            logger.LogError(ex, "Unable to process request");
            var response = new IppResponseMessage
            {
                RequestId = ex.RequestMessage.RequestId,
                Version = ex.RequestMessage.Version,
                StatusCode = ex.StatusCode
            };
            response.OperationAttributes.Add([
                new IppAttribute(Tag.Charset, IppAttributeNames.AttributesCharset, "utf-8"),
                new IppAttribute(Tag.NaturalLanguage, IppAttributeNames.AttributesNaturalLanguage, "en")]);
            await sharpIppServer.SendRawResponseAsync(response, outputStream);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unable to process request");
            if (httpContextAccessor.HttpContext != null)
                httpContextAccessor.HttpContext.Response.StatusCode = 500;
        }
    }
    private async Task<IIppResponse> GetResponseAsync(IIppRequest request)
    {
        return request switch
        {
            CancelJobRequest x => await GetCancelJobResponseAsync(x),
            CreateJobRequest x => GetCreateJobResponse(x),
            CUPSGetPrintersRequest x => GetCUPSGetPrintersResponse(x),
            GetJobAttributesRequest x => GetGetJobAttributesResponse(x),
            GetJobsRequest x => GetGetJobsResponse(x),
            GetPrinterAttributesRequest x => GetGetPrinterAttributesResponse(x),
            HoldJobRequest x => await GetHoldJobResponseAsync(x),
            PausePrinterRequest x => GetPausePrinterResponse(x),
            PrintJobRequest x => await GetPrintJobResponseAsync(x),
            ReleaseJobRequest x => await GetReleaseJobResponseAsync(x),
            ResumePrinterRequest x => GetResumePrinterResponse(x),
            SendDocumentRequest x => await GetSendDocumentResponseAsync(x),
            ValidateJobRequest x => GetValidateJobResponse(x),
            CloseJobRequest x => await GetCloseJobResponseAsync(x),
            IdentifyPrinterRequest x => GetIdentifyPrinterResponse(x),
            ResubmitJobRequest x => await GetResubmitJobResponseAsync(x),
            CancelJobsRequest x => await GetCancelJobsResponseAsync(x),
            CancelMyJobsRequest x => await GetCancelMyJobsResponseAsync(x),
            SetJobAttributesRequest x => await GetSetJobAttributesResponseAsync(x),
            SetPrinterAttributesRequest x => GetSetPrinterAttributesResponse(x),
            GetPrinterSupportedValuesRequest x => GetGetPrinterSupportedValuesResponse(x),
            CreatePrinterSubscriptionsRequest x => subscriptionsService.GetCreatePrinterSubscriptionsResponse(x),
            CancelSubscriptionRequest x => subscriptionsService.GetCancelSubscriptionResponse(x),
            GetSubscriptionAttributesRequest x => subscriptionsService.GetGetSubscriptionAttributesResponse(x),
            _ => throw new NotImplementedException()
        };
    }

    private void ImproveRawResponse(IIppRequest request, IIppResponse response, IIppResponseMessage rawResponse)
    {
        switch(request)
        {
            case GetPrinterAttributesRequest x:
                ImproveGetPrinterAttributesRawResponse(x, rawResponse);
                break;
        }
    }

    private void ImproveGetPrinterAttributesRawResponse(GetPrinterAttributesRequest request, IIppResponseMessage rawResponse)
    {
        var list = rawResponse.PrinterAttributes.FirstOrDefault();
        if(list is null)
            return;
        bool IsRequired(string attributeName) => !list.Any(x => x.Name.Equals(attributeName))
            && IsAttributeRequired(request.OperationAttributes?.RequestedAttributes, attributeName);
        var options = printerOptions.Value;
        if(IsRequired("printer-dns-sd-name"))
            list.Add(new IppAttribute(Tag.NameWithoutLanguage, "printer-dns-sd-name", options.DnsSdName));
        if(IsRequired("printer-make-and-model"))
            list.Add(new IppAttribute(Tag.TextWithoutLanguage, "printer-make-and-model", options.Name));
        if(IsRequired("printer-firmware-name"))
            list.Add(new IppAttribute(Tag.NameWithoutLanguage, "printer-firmware-name", options.FirmwareName));
        if(IsRequired("printer-firmware-string-version"))
            list.Add(new IppAttribute(Tag.TextWithoutLanguage, "printer-firmware-string-version", options.FirmwareName));
    }

    private ValidateJobResponse GetValidateJobResponse(ValidateJobRequest request)
    {
        logger.LogInformation("Job has been validated");
        return new ValidateJobResponse
        {
            RequestId = request.RequestId,
            Version = request.Version,
            StatusCode = IppStatusCode.SuccessfulOk
        };
    }

    private string GetPrinterDeviceId(PrinterOptions options)
    {
        return new StringBuilder()
            .Append($"MFG:{options.Manufacturer};") //Manufacturer
            .Append($"MDL:{options.Model};") //Model
            .Append("CMD:Automatic,JPEG;") //Command Set
            .Append("CLS:PRINTER") //Class
            .Append("DES:SIN1;") //Designator or Description
            .Append($"CID:{options.Model}_1;") //Compatible ID
            .Append("LEDMDIS:USB#FF#CC#00,USB#07#01#02,USB#FF#04#01;") //Legacy Device ID String
            .Append($"SN:{options.SerialNumber};") //Serial Number
            .Append("S:038000C480a00001002c240005ac1400032;") //Status
            .Append("Z:05000008000009,12000,17000000000000,181;") //Vendor-Specific
            .ToString();
    }


    private async Task<SendDocumentResponse> GetSendDocumentResponseAsync(SendDocumentRequest request)
    {
        var response = new SendDocumentResponse
        {
            RequestId = request.RequestId,
            Version = request.Version,
            StatusCode = IppStatusCode.ClientErrorNotPossible,
            JobAttributes = new()
        };
        var jobId = GetJobId(request);
        if (!jobId.HasValue)
            return response;
        response.JobAttributes.JobId = jobId.Value;
        if (!_jobs.TryGetValue(jobId.Value, out var job))
            return response;

        int docCount = job.Requests.Count(x => x is SendDocumentRequest);
        if (docCount >= printerOptions.Value.MaxDocumentsPerJob)
        {
            logger.LogWarning("Rejecting SendDocument for job {id} because limit of {limit} documents is reached", job.Id, printerOptions.Value.MaxDocumentsPerJob);
            response.StatusCode = IppStatusCode.ClientErrorNotPossible;
            return response;
        }

        if (request.Document != null)
        {
            if (!request.Document.CanSeek)
            {
                throw new ArgumentException("Document stream must be seekable.", nameof(request.Document));
            }
            if (request.Document.Position > 0)
            {
                request.Document.Position = 0;
            }
        }

        var copy = new PrinterJob(job);
        if (request.OperationAttributes?.LastDocument ?? false)
        {
            if (!copy.TrySetState(JobState.Pending, dateTimeOffsetProvider.UtcNow))
                return response;
            logger.LogInformation("Job {id} has been moved to queue", job.Id);
        }
        FillWithDefaultValues(request.OperationAttributes ??= new());
        job.Requests.Add(request);
        logger.LogInformation("Document has been added to job {id}", job.Id);
        if (!_jobs.TryUpdate(jobId.Value, copy, job))
            return response;
        response.JobAttributes.JobState = JobState.Pending;
        response.StatusCode = IppStatusCode.SuccessfulOk;
        if (request.OperationAttributes?.LastDocument ?? false)
        {
            jobQueue.Writer.TryWrite(job.Id);
        }
        return response;
    }

    private ReleaseJobResponse GetResumePrinterResponse(ResumePrinterRequest request)
    {
        _isPaused = false;
        logger.LogInformation("Printer has been resumed");
        return new ReleaseJobResponse
        {
            RequestId = request.RequestId,
            Version = request.Version
        };
    }

    private async Task<IIppResponse> GetCloseJobResponseAsync(CloseJobRequest request)
    {
        var jobId = GetJobId(request);
        if (!jobId.HasValue)
            return new CloseJobResponse
            {
                RequestId = request.RequestId,
                Version = request.Version,
                StatusCode = IppStatusCode.ClientErrorNotPossible
            };
        if (!_jobs.TryGetValue(jobId.Value, out var job))
            return new CloseJobResponse
            {
                RequestId = request.RequestId,
                Version = request.Version,
                StatusCode = IppStatusCode.ClientErrorNotPossible
            };
        var copy = new PrinterJob(job);
        if (!copy.TrySetState(JobState.Pending, dateTimeOffsetProvider.UtcNow))
            return new CloseJobResponse
            {
                RequestId = request.RequestId,
                Version = request.Version,
                StatusCode = IppStatusCode.ClientErrorNotPossible
            };
        if (!_jobs.TryUpdate(jobId.Value, copy, job))
            return new CloseJobResponse
            {
                RequestId = request.RequestId,
                Version = request.Version,
                StatusCode = IppStatusCode.ClientErrorNotPossible
            };
        logger.LogInformation("Job {id} has been closed", jobId.Value);
        jobQueue.Writer.TryWrite(jobId.Value);
        return new CloseJobResponse
        {
            RequestId = request.RequestId,
            Version = request.Version,
            StatusCode = IppStatusCode.SuccessfulOk,
            JobAttributes = new JobAttributes
            {
                JobId = jobId.Value,
                JobState = JobState.Pending,
                JobStateReasons = new ([JobStateReason.None])
            }
        };
    }

    private async Task<IIppResponse> GetResubmitJobResponseAsync(ResubmitJobRequest request)
    {
        var jobId = GetJobId(request);
        if (!jobId.HasValue)
            return new ResubmitJobResponse
            {
                RequestId = request.RequestId,
                Version = request.Version,
                StatusCode = IppStatusCode.ClientErrorNotPossible
            };
        if (!_jobs.TryGetValue(jobId.Value, out var job))
            return new ResubmitJobResponse
            {
                RequestId = request.RequestId,
                Version = request.Version,
                StatusCode = IppStatusCode.ClientErrorNotPossible
            };

        if (!CanAddJob())
        {
            logger.LogWarning("Rejecting ResubmitJob for job {id} because active job limit is reached", jobId);
            return new ResubmitJobResponse
            {
                RequestId = request.RequestId,
                Version = request.Version,
                StatusCode = IppStatusCode.ServerErrorBusy
            };
        }

        var newJob = new PrinterJob(GetNextValue(), job.UserName, dateTimeOffsetProvider.UtcNow);
        foreach (var r in job.Requests)
        {
            if (r is PrintJobRequest pjr)
            {
                newJob.Requests.Add(pjr);
            }
            else if (r is SendDocumentRequest sdr)
            {
                newJob.Requests.Add(sdr);
            }
        }
        if (!newJob.TrySetState(JobState.Pending, dateTimeOffsetProvider.UtcNow))
            return new ResubmitJobResponse
            {
                RequestId = request.RequestId,
                Version = request.Version,
                StatusCode = IppStatusCode.ClientErrorNotPossible
            };
        if (!_jobs.TryAdd(newJob.Id, newJob))
            return new ResubmitJobResponse
            {
                RequestId = request.RequestId,
                Version = request.Version,
                StatusCode = IppStatusCode.ClientErrorNotPossible
            };
        PruneJobHistory();
        logger.LogInformation("Job {id} has been resubmitted as new job {newId}", jobId, newJob.Id);
        jobQueue.Writer.TryWrite(newJob.Id);
        return new ResubmitJobResponse
        {
            RequestId = request.RequestId,
            Version = request.Version,
            StatusCode = IppStatusCode.SuccessfulOk,
            JobAttributes = new JobAttributes
            {
                JobId = newJob.Id,
                JobState = JobState.Pending,
                JobStateReasons = new([JobStateReason.None])
            }
        };
    }

    private async Task<ReleaseJobResponse> GetReleaseJobResponseAsync(ReleaseJobRequest request)
    {
        var response = new ReleaseJobResponse
        {
            RequestId = request.RequestId,
            Version = request.Version,
            StatusCode = IppStatusCode.ClientErrorNotPossible
        };
        var jobId = GetJobId(request);
        if (!jobId.HasValue)
            return response;
        if (!_jobs.TryGetValue(jobId.Value, out var job))
            return response;
        var copy = new PrinterJob(job);
        if (!copy.TrySetState(JobState.Pending, dateTimeOffsetProvider.UtcNow))
            return response;
        if (!_jobs.TryUpdate(jobId.Value, copy, job))
            return response;
        response.StatusCode = IppStatusCode.SuccessfulOk;
        logger.LogInformation("Job {id} has been released", jobId);
        jobQueue.Writer.TryWrite(jobId.Value);
        return response;
    }

    private async Task<CancelJobsResponse> GetCancelJobsResponseAsync(CancelJobsRequest request)
    {
        foreach (var job in _jobs.Values.Where(x => x.State == JobState.Pending || x.State == JobState.Processing || !x.State.HasValue))
        {
            var copy = new PrinterJob(job);
            if (copy.TrySetState(JobState.Canceled, dateTimeOffsetProvider.UtcNow))
            {
                if (_jobs.TryUpdate(job.Id, copy, job))
                {
                    await copy.ClearDocumentStreamsAsync();
                }
            }
        }
        logger.LogInformation("System canceled all jobs");
        return new CancelJobsResponse
        {
            RequestId = request.RequestId,
            Version = request.Version,
            StatusCode = IppStatusCode.SuccessfulOk
        };
    }

    private async Task<CancelMyJobsResponse> GetCancelMyJobsResponseAsync(CancelMyJobsRequest request)
    {
        var userName = request.OperationAttributes?.RequestingUserName;
        foreach (var job in _jobs.Values.Where(x => (x.State == JobState.Pending || x.State == JobState.Processing || !x.State.HasValue) && x.UserName == userName))
        {
            var copy = new PrinterJob(job);
            if (copy.TrySetState(JobState.Canceled, dateTimeOffsetProvider.UtcNow))
            {
                if (_jobs.TryUpdate(job.Id, copy, job))
                {
                    await copy.ClearDocumentStreamsAsync();
                }
            }
        }
        logger.LogInformation("System canceled all jobs for user {user}", userName);
        return new CancelMyJobsResponse
        {
            RequestId = request.RequestId,
            Version = request.Version,
            StatusCode = IppStatusCode.SuccessfulOk
        };
    }

    private IdentifyPrinterResponse GetIdentifyPrinterResponse(IdentifyPrinterRequest request)
    {
        logger.LogInformation("Printer identify requested");
        return new IdentifyPrinterResponse
        {
            RequestId = request.RequestId,
            Version = request.Version,
            StatusCode = IppStatusCode.SuccessfulOk
        };
    }

    private PausePrinterResponse GetPausePrinterResponse(PausePrinterRequest request)
    {
        _isPaused = true;
        logger.LogInformation("Printer has been paused");
        return new PausePrinterResponse
        {
            RequestId = request.RequestId,
            Version = request.Version
        };
    }

    private async Task<HoldJobResponse> GetHoldJobResponseAsync(HoldJobRequest request)
    {
        var response = new HoldJobResponse
        {
            RequestId = request.RequestId,
            Version = request.Version,
            StatusCode = IppStatusCode.ClientErrorNotPossible
        };
        var jobId = GetJobId(request);
        if (!jobId.HasValue)
            return response;
        if (!_jobs.TryGetValue(jobId.Value, out var job))
            return response;
        var copy = new PrinterJob(job);
        if (!copy.TrySetState(null, dateTimeOffsetProvider.UtcNow))
            return response;
        if (!_jobs.TryUpdate(jobId.Value, copy, job))
            return response;
        response.StatusCode = IppStatusCode.SuccessfulOk;
        logger.LogInformation("Job {id} has been held", jobId);
        return response;
    }

    private static bool IsAttributeRequired(string[]? requestedAttributes, string attributeName)
    {
        return requestedAttributes is null
                || requestedAttributes.Length == 0
                || requestedAttributes.All(x => x == string.Empty)
                || requestedAttributes.Any(x => x.Equals("all", StringComparison.InvariantCultureIgnoreCase))
                || requestedAttributes.Contains(attributeName);
    }

    private GetPrinterAttributesResponse GetGetPrinterAttributesResponse(GetPrinterAttributesRequest request)
    {
        var options = printerOptions.Value;
        bool IsRequired(string attributeName) => IsAttributeRequired(request.OperationAttributes?.RequestedAttributes, attributeName);
        logger.LogInformation("System returned printer attributes");
        return new GetPrinterAttributesResponse
        {
            RequestId = request.RequestId,
            Version = request.Version,
            StatusCode = IppStatusCode.SuccessfulOk,
            PrinterAttributes = new()
            {
                AccuracyUnitsSupported = !IsRequired(IppAttributeNames.AccuracyUnitsSupported) ? null : default,
                BalingTypeSupported = !IsRequired(IppAttributeNames.BalingTypeSupported) ? null : default,
                BalingWhenSupported = !IsRequired(IppAttributeNames.BalingWhenSupported) ? null : default,
                BindingReferenceEdgeSupported = !IsRequired(IppAttributeNames.BindingReferenceEdgeSupported) ? null : default,
                BindingTypeSupported = !IsRequired(IppAttributeNames.BindingTypeSupported) ? null : default,
                ChamberHumidityCurrent = !IsRequired(IppAttributeNames.ChamberHumidityCurrent) ? null : default,
                ChamberHumidityDefault = !IsRequired(IppAttributeNames.ChamberHumidityDefault) ? null : default,
                ChamberHumiditySupported = !IsRequired(IppAttributeNames.ChamberHumiditySupported) ? null : default,
                ChamberTemperatureCurrent = !IsRequired(IppAttributeNames.ChamberTemperatureCurrent) ? null : default,
                ChamberTemperatureDefault = !IsRequired(IppAttributeNames.ChamberTemperatureDefault) ? null : default,
                ChamberTemperatureSupported = !IsRequired(IppAttributeNames.ChamberTemperatureSupported) ? null : default,
                CharsetConfigured = !IsRequired(IppAttributeNames.CharsetConfigured) ? (Charset?)null : Charset.Utf8,
                CharsetSupported = !IsRequired(IppAttributeNames.CharsetSupported) ? null : new([Charset.Utf8]),
                ClientInfoSupported = !IsRequired(IppAttributeNames.ClientInfoSupported) ? null : default,
                CoatingSidesSupported = !IsRequired(IppAttributeNames.CoatingSidesSupported) ? null : default,
                CoatingTypeSupported = !IsRequired(IppAttributeNames.CoatingTypeSupported) ? null : default,
                ColorSupported = !IsRequired(IppAttributeNames.ColorSupported) ? null : (options.ColorSupported ?? options.PrintColorModes.Contains(PrintColorMode.Color)),
                CompressionDefault = !IsRequired(IppAttributeNames.CompressionDefault) ? (Compression?)null : Compression.None,
                CompressionSupported = !IsRequired(IppAttributeNames.CompressionSupported) ? null : new([Compression.None]),
                ConfirmationSheetPrintDefault = !IsRequired(IppAttributeNames.ConfirmationSheetPrintDefault) ? null : default,
                CopiesDefault = !IsRequired(IppAttributeNames.CopiesDefault) ? null : options.Copies,
                CopiesSupported = !IsRequired(IppAttributeNames.CopiesSupported) ? null : new SharpIpp.Protocol.Models.Range(1, options.MaxCopies),
                CoverBackDefault = !IsRequired(IppAttributeNames.CoverBackDefault) ? null : default,
                CoverBackSupported = !IsRequired(IppAttributeNames.CoverBackSupported) ? null : default,
                CoverFrontDefault = !IsRequired(IppAttributeNames.CoverFrontDefault) ? null : default,
                CoverFrontSupported = !IsRequired(IppAttributeNames.CoverFrontSupported) ? null : default,
                CoveringNameSupported = !IsRequired(IppAttributeNames.CoveringNameSupported) ? null : default,
                CoverSheetInfoDefault = !IsRequired(IppAttributeNames.CoverSheetInfoDefault) ? null : default,
                CoverSheetInfoSupported = !IsRequired(IppAttributeNames.CoverSheetInfoSupported) ? null : default,
                CoverTypeSupported = !IsRequired(IppAttributeNames.CoverTypeSupported) ? null : default,
                DestinationAccessesSupported = !IsRequired(IppAttributeNames.DestinationAccessesSupported) ? null : default,
                DestinationUriReady = !IsRequired(IppAttributeNames.DestinationUriReady) ? null : default,
                DestinationUriSchemesSupported = !IsRequired(IppAttributeNames.DestinationUriSchemesSupported) ? null : default,
                DestinationUrisSupported = !IsRequired(IppAttributeNames.DestinationUrisSupported) ? null : default,
                DocumentAccessSupported = !IsRequired(IppAttributeNames.DocumentAccessSupported) ? null : default,
                DocumentCharsetDefault = !IsRequired(IppAttributeNames.DocumentCharsetDefault) ? (Charset?)null : Charset.Utf8,
                DocumentCharsetSupported = !IsRequired(IppAttributeNames.DocumentCharsetSupported) ? null : new([Charset.Utf8]),
                DocumentCreationAttributesSupported = !IsRequired(IppAttributeNames.DocumentCreationAttributesSupported) ? null : default,
                DocumentFormatDefault = !IsRequired(IppAttributeNames.DocumentFormatDefault) ? (DocumentFormat?)null : options.DocumentFormat,
                DocumentFormatDetailsSupported = !IsRequired(IppAttributeNames.DocumentFormatDetailsSupported) ? null : new([]),
                DocumentFormatSupported = !IsRequired(IppAttributeNames.DocumentFormatSupported) ? null : options.DocumentFormatSupported,
                DocumentNaturalLanguageDefault = !IsRequired(IppAttributeNames.DocumentNaturalLanguageDefault) ? (NaturalLanguage?)null : options.NaturalLanguageConfigured,
                DocumentNaturalLanguageSupported = !IsRequired(IppAttributeNames.DocumentNaturalLanguageSupported) ? null : new([options.NaturalLanguageConfigured]),
                DocumentPasswordSupported = !IsRequired(IppAttributeNames.DocumentPasswordSupported) ? null : default,
                FetchDocumentAttributesSupported = !IsRequired(IppAttributeNames.FetchDocumentAttributesSupported) ? null : default,
                FinishingsColDatabase = !IsRequired(IppAttributeNames.FinishingsColDatabase) ? null : default,
                FinishingsColDefault = !IsRequired(IppAttributeNames.FinishingsColDefault) ? null : default,
                FinishingsColReady = !IsRequired(IppAttributeNames.FinishingsColReady) ? null : default,
                FinishingsColSupported = !IsRequired(IppAttributeNames.FinishingsColSupported) ? null : default,
                FinishingsDefault = !IsRequired(IppAttributeNames.FinishingsDefault) ? null : options.Finishings.FirstOrDefault(),
                FinishingsSupported = !IsRequired(IppAttributeNames.FinishingsSupported) ? null : options.Finishings,
                FinishingTemplateSupported = !IsRequired(IppAttributeNames.FinishingTemplateSupported) ? null : default,
                FoldingDirectionSupported = !IsRequired(IppAttributeNames.FoldingDirectionSupported) ? null : default,
                FoldingOffsetSupported = !IsRequired(IppAttributeNames.FoldingOffsetSupported) ? null : default,
                FoldingReferenceEdgeSupported = !IsRequired(IppAttributeNames.FoldingReferenceEdgeSupported) ? null : default,
                ForceFrontSideSupported = !IsRequired(IppAttributeNames.ForceFrontSideSupported) ? null : default,
                FromNameSupported = !IsRequired(IppAttributeNames.FromNameSupported) ? null : default,
                GeneratedNaturalLanguageSupported = !IsRequired(IppAttributeNames.GeneratedNaturalLanguageSupported) ? null : new([options.NaturalLanguage]),
                ImageOrientationDefault = !IsRequired(IppAttributeNames.ImageOrientationDefault) ? null : default,
                ImageOrientationSupported = !IsRequired(IppAttributeNames.ImageOrientationSupported) ? null : default,
                ImpositionTemplateDefault = !IsRequired(IppAttributeNames.ImpositionTemplateDefault) ? null : default,
                ImpositionTemplateSupported = !IsRequired(IppAttributeNames.ImpositionTemplateSupported) ? null : default,
                InputAttributesDefault = !IsRequired(IppAttributeNames.InputAttributesDefault) ? null : default,
                InputAttributesSupported = !IsRequired(IppAttributeNames.InputAttributesSupported) ? null : default,
                InputColorModeSupported = !IsRequired(IppAttributeNames.InputColorModeSupported) ? null : default,
                InputContentTypeSupported = !IsRequired(IppAttributeNames.InputContentTypeSupported) ? null : default,
                InputFilmScanModeSupported = !IsRequired(IppAttributeNames.InputFilmScanModeSupported) ? null : default,
                InputMediaSupported = !IsRequired(IppAttributeNames.InputMediaSupported) ? null : default,
                InputOrientationRequestedSupported = !IsRequired(IppAttributeNames.InputOrientationRequestedSupported) ? null : default,
                InputQualitySupported = !IsRequired(IppAttributeNames.InputQualitySupported) ? null : default,
                InputResolutionSupported = !IsRequired(IppAttributeNames.InputResolutionSupported) ? null : default,
                InputSidesSupported = !IsRequired(IppAttributeNames.InputSidesSupported) ? null : default,
                InputSourceSupported = !IsRequired(IppAttributeNames.InputSourceSupported) ? null : default,
                InsertCountSupported = !IsRequired(IppAttributeNames.InsertCountSupported) ? null : default,
                InsertSheetDefault = !IsRequired(IppAttributeNames.InsertSheetDefault) ? null : default,
                InsertSheetSupported = !IsRequired(IppAttributeNames.InsertSheetSupported) ? null : default,
                IppFeaturesSupported = !IsRequired(IppAttributeNames.IppFeaturesSupported) ? null : default,
                IppVersionsSupported = !IsRequired(IppAttributeNames.IppVersionsSupported) ? null : new IppVersion[] { new IppVersion(1, 0), new IppVersion(1, 1), new IppVersion(2, 0), new IppVersion(2, 1), new IppVersion(2, 2) },
                JobAccountIdDefault = !IsRequired(IppAttributeNames.JobAccountIdDefault) ? null : default,
                JobAccountIdSupported = !IsRequired(IppAttributeNames.JobAccountIdSupported) ? null : default,
                JobAccountingOutputBinSupported = !IsRequired(IppAttributeNames.JobAccountingOutputBinSupported) ? null : default,
                JobAccountingSheetsDefault = !IsRequired(IppAttributeNames.JobAccountingSheetsDefault) ? null : default,
                JobAccountingSheetsSupported = !IsRequired(IppAttributeNames.JobAccountingSheetsSupported) ? null : default,
                JobAccountingSheetsTypeSupported = !IsRequired(IppAttributeNames.JobAccountingSheetsTypeSupported) ? null : default,
                JobAccountingUserIdDefault = !IsRequired(IppAttributeNames.JobAccountingUserIdDefault) ? null : default,
                JobAccountingUserIdSupported = !IsRequired(IppAttributeNames.JobAccountingUserIdSupported) ? null : default,
                JobAccountTypeDefault = !IsRequired(IppAttributeNames.JobAccountTypeDefault) ? null : default,
                JobAccountTypeSupported = !IsRequired(IppAttributeNames.JobAccountTypeSupported) ? null : default,
                JobAuthorizationUriSupported = !IsRequired(IppAttributeNames.JobAuthorizationUriSupported) ? null : default,
                JobCancelAfterDefault = !IsRequired(IppAttributeNames.JobCancelAfterDefault) ? null : options.JobCancelAfter,
                JobCancelAfterSupported = !IsRequired(IppAttributeNames.JobCancelAfterSupported) ? null : new SharpIpp.Protocol.Models.Range(0, int.MaxValue),
                JobCompleteBeforeSupported = !IsRequired(IppAttributeNames.JobCompleteBeforeSupported) ? null : default,
                JobCompleteBeforeTimeSupported = !IsRequired(IppAttributeNames.JobCompleteBeforeTimeSupported) ? null : default,
                JobConstraintsSupported = !IsRequired(IppAttributeNames.JobConstraintsSupported) ? null : default,
                JobCreationAttributesSupported = !IsRequired(IppAttributeNames.JobCreationAttributesSupported) ? null : new([
                    IppAttributeNames.Copies,
                    IppAttributeNames.Finishings,
                    IppAttributeNames.IppAttributeFidelity,
                    IppAttributeNames.JobHoldUntil,
                    IppAttributeNames.JobImpressions,
                    IppAttributeNames.JobName,
                    IppAttributeNames.JobPriority,
                    IppAttributeNames.JobSheets,
                    IppAttributeNames.Media,
                    IppAttributeNames.MultipleDocumentHandling,
                    IppAttributeNames.OrientationRequested,
                    IppAttributeNames.PrintQuality,
                    IppAttributeNames.PrinterResolution,
                    IppAttributeNames.Sides
                ]),
                JobDelayOutputUntilDefault = !IsRequired(IppAttributeNames.JobDelayOutputUntilDefault) ? (JobHoldUntil?)null : JobHoldUntil.NoHold,
                JobDelayOutputUntilSupported = !IsRequired(IppAttributeNames.JobDelayOutputUntilSupported) ? null : new([JobHoldUntil.NoHold]),
                JobDelayOutputUntilTimeSupported = !IsRequired(IppAttributeNames.JobDelayOutputUntilTimeSupported) ? null : default,
                JobDestinationSpoolingSupported = !IsRequired(IppAttributeNames.JobDestinationSpoolingSupported) ? null : default,
                JobErrorSheetDefault = !IsRequired(IppAttributeNames.JobErrorSheetDefault) ? null : default,
                JobErrorSheetSupported = !IsRequired(IppAttributeNames.JobErrorSheetSupported) ? null : default,
                JobErrorSheetTypeSupported = !IsRequired(IppAttributeNames.JobErrorSheetTypeSupported) ? null : default,
                JobErrorSheetWhenSupported = !IsRequired(IppAttributeNames.JobErrorSheetWhenSupported) ? null : default,
                JobHistoryAttributesConfigured = !IsRequired(IppAttributeNames.JobHistoryAttributesConfigured) ? null : new([JobHistoryAttribute.JobId, JobHistoryAttribute.JobName, JobHistoryAttribute.JobState, JobHistoryAttribute.JobStateReasons]),
                JobHistoryAttributesSupported = !IsRequired(IppAttributeNames.JobHistoryAttributesSupported) ? null : new([JobHistoryAttribute.JobId, JobHistoryAttribute.JobName, JobHistoryAttribute.JobState, JobHistoryAttribute.JobStateReasons]),
                JobHistoryIntervalConfigured = !IsRequired(IppAttributeNames.JobHistoryIntervalConfigured) ? null : 0,
                JobHistoryIntervalSupported = !IsRequired(IppAttributeNames.JobHistoryIntervalSupported) ? null : new SharpIpp.Protocol.Models.Range(0, int.MaxValue),
                JobHoldUntilDefault = !IsRequired(IppAttributeNames.JobHoldUntilDefault) ? (JobHoldUntil?)null : options.JobHoldUntil,
                JobHoldUntilSupported = !IsRequired(IppAttributeNames.JobHoldUntilSupported) ? null : options.JobHoldUntilSupported,
                JobHoldUntilTimeSupported = !IsRequired(IppAttributeNames.JobHoldUntilTimeSupported) ? null : false,
                JobIdsSupported = !IsRequired(IppAttributeNames.JobIdsSupported) ? null : true,
                JobImpressionsSupported = !IsRequired(IppAttributeNames.JobImpressionsSupported) ? null : new SharpIpp.Protocol.Models.Range(1, int.MaxValue),
                JobKOctetsSupported = !IsRequired(IppAttributeNames.JobKOctetsSupported) ? null : default,
                JobMandatoryAttributesSupported = !IsRequired(IppAttributeNames.JobMandatoryAttributesSupported) ? null : default,
                JobMediaSheetsSupported = !IsRequired(IppAttributeNames.JobMediaSheetsSupported) ? null : default,
                JobMessageToOperatorSupported = !IsRequired(IppAttributeNames.JobMessageToOperatorSupported) ? null : default,
                JobPagesPerSetSupported = !IsRequired(IppAttributeNames.JobPagesPerSetSupported) ? null : default,
                JobPasswordEncryptionSupported = !IsRequired(IppAttributeNames.JobPasswordEncryptionSupported) ? null : default,
                JobPasswordLengthSupported = !IsRequired(IppAttributeNames.JobPasswordLengthSupported) ? null : default,
                JobPasswordSupported = !IsRequired(IppAttributeNames.JobPasswordSupported) ? null : default,
                JobPhoneNumberDefault = !IsRequired(IppAttributeNames.JobPhoneNumberDefault) ? null : default,
                JobPhoneNumberSchemeSupported = !IsRequired(IppAttributeNames.JobPhoneNumberSchemeSupported) ? null : default,
                JobPhoneNumberSupported = !IsRequired(IppAttributeNames.JobPhoneNumberSupported) ? null : default,
                JobPresetsSupported = !IsRequired(IppAttributeNames.JobPresetsSupported) ? null : default,
                JobPriorityDefault = !IsRequired(IppAttributeNames.JobPriorityDefault) ? null : options.JobPriority,
                JobPrioritySupported = !IsRequired(IppAttributeNames.JobPrioritySupported) ? null : options.JobPriority,
                JobRecipientNameSupported = !IsRequired(IppAttributeNames.JobRecipientNameSupported) ? null : default,
                JobResolversSupported = !IsRequired(IppAttributeNames.JobResolversSupported) ? null : default,
                JobRetainUntilDefault = !IsRequired(IppAttributeNames.JobRetainUntilDefault) ? (JobHoldUntil?)null : JobHoldUntil.NoHold,
                JobRetainUntilIntervalDefault = !IsRequired(IppAttributeNames.JobRetainUntilIntervalDefault) ? null : 0,
                JobRetainUntilIntervalSupported = !IsRequired(IppAttributeNames.JobRetainUntilIntervalSupported) ? null : new SharpIpp.Protocol.Models.Range(0, int.MaxValue),
                JobRetainUntilSupported = !IsRequired(IppAttributeNames.JobRetainUntilSupported) ? null : new([JobHoldUntil.NoHold]),
                JobRetainUntilTimeSupported = !IsRequired(IppAttributeNames.JobRetainUntilTimeSupported) ? null : false,
                JobSheetMessageSupported = !IsRequired(IppAttributeNames.JobSheetMessageSupported) ? null : default,
                JobSheetsColDefault = !IsRequired(IppAttributeNames.JobSheetsColDefault) ? null : default,
                JobSheetsColSupported = !IsRequired(IppAttributeNames.JobSheetsColSupported) ? null : default,
                JobSheetsDefault = !IsRequired(IppAttributeNames.JobSheetsDefault) ? (JobSheets?)null : JobSheets.None,
                JobSheetsSupported = !IsRequired(IppAttributeNames.JobSheetsSupported) ? null : new ([JobSheets.None]),
                JobSpoolingSupported = !IsRequired(IppAttributeNames.JobSpoolingSupported) ? null : default,
                JobTriggersSupported = !IsRequired(IppAttributeNames.JobTriggersSupported) ? null : default,
                JpegKOctetsSupported = !IsRequired(IppAttributeNames.JpegKOctetsSupported) ? null : default,
                JpegXDimensionSupported = !IsRequired(IppAttributeNames.JpegXDimensionSupported) ? null : default,
                JpegYDimensionSupported = !IsRequired(IppAttributeNames.JpegYDimensionSupported) ? null : default,
                LaminatingSidesSupported = !IsRequired(IppAttributeNames.LaminatingSidesSupported) ? null : default,
                LaminatingTypeSupported = !IsRequired(IppAttributeNames.LaminatingTypeSupported) ? null : default,
                LogoUriFormatsSupported = !IsRequired(IppAttributeNames.LogoUriFormatsSupported) ? null : default,
                LogoUriSchemesSupported = !IsRequired(IppAttributeNames.LogoUriSchemesSupported) ? null : default,
                MaterialAmountUnitsSupported = !IsRequired(IppAttributeNames.MaterialAmountUnitsSupported) ? null : default,
                MaterialDiameterSupported = !IsRequired(IppAttributeNames.MaterialDiameterSupported) ? null : default,
                MaterialNozzleDiameterSupported = !IsRequired(IppAttributeNames.MaterialNozzleDiameterSupported) ? null : default,
                MaterialPurposeSupported = !IsRequired(IppAttributeNames.MaterialPurposeSupported) ? null : default,
                MaterialRateSupported = !IsRequired(IppAttributeNames.MaterialRateSupported) ? null : default,
                MaterialRateUnitsSupported = !IsRequired(IppAttributeNames.MaterialRateUnitsSupported) ? null : default,
                MaterialsColDatabase = !IsRequired(IppAttributeNames.MaterialsColDatabase) ? null : default,
                MaterialsColDefault = !IsRequired(IppAttributeNames.MaterialsColDefault) ? null : default,
                MaterialsColReady = !IsRequired(IppAttributeNames.MaterialsColReady) ? null : default,
                MaterialsColSupported = !IsRequired(IppAttributeNames.MaterialsColSupported) ? null : default,
                MaterialShellThicknessSupported = !IsRequired(IppAttributeNames.MaterialShellThicknessSupported) ? null : default,
                MaterialTemperatureSupported = !IsRequired(IppAttributeNames.MaterialTemperatureSupported) ? null : default,
                MaterialTypeSupported = !IsRequired(IppAttributeNames.MaterialTypeSupported) ? null : default,
                MaxClientInfoSupported = !IsRequired(IppAttributeNames.MaxClientInfoSupported) ? null : default,
                MaxMaterialsColSupported = !IsRequired(IppAttributeNames.MaxMaterialsColSupported) ? null : default,
                MaxPageRangesSupported = !IsRequired(IppAttributeNames.MaxPageRangesSupported) ? null : default,
                MediaBackCoatingSupported = !IsRequired(IppAttributeNames.MediaBackCoatingSupported) ? null : default,
                MediaBottomMarginSupported = !IsRequired(IppAttributeNames.MediaBottomMarginSupported) ? null : default,
                MediaColDatabase = !IsRequired(IppAttributeNames.MediaColDatabase) ? null : default,
                MediaColDefault = !IsRequired(IppAttributeNames.MediaColDefault) ? null : new MediaCol
                {
                    MediaBackCoating = MediaCoating.None,
                    MediaBottomMargin = 10,
                    MediaColor = new(MediaColor.Black),
                    MediaLeftMargin = 10,
                    MediaRightMargin = 10,
                    MediaTopMargin = 10,
                    MediaFrontCoating = MediaCoating.None,
                    MediaGrain = MediaGrain.XDirection,
                    MediaHoleCount = 0,
                    MediaInfo = "my black color",
                    MediaOrderCount = 1
                },
                MediaColorSupported = !IsRequired(IppAttributeNames.MediaColorSupported) ? null : default,
                MediaColReady = !IsRequired(IppAttributeNames.MediaColReady) ? null : default,
                MediaColSupported = !IsRequired(IppAttributeNames.MediaColSupported) ? null : default,
                MediaDefault = !IsRequired(IppAttributeNames.MediaDefault) ? (Media?)null : options.Media.FirstOrDefault(),
                MediaFrontCoatingSupported = !IsRequired(IppAttributeNames.MediaFrontCoatingSupported) ? null : default,
                MediaGrainSupported = !IsRequired(IppAttributeNames.MediaGrainSupported) ? null : default,
                MediaHoleCountSupported = !IsRequired(IppAttributeNames.MediaHoleCountSupported) ? null : default,
                MediaKeySupported = !IsRequired(IppAttributeNames.MediaKeySupported) ? null : default,
                MediaLeftMarginSupported = !IsRequired(IppAttributeNames.MediaLeftMarginSupported) ? null : default,
                MediaOrderCountSupported = !IsRequired(IppAttributeNames.MediaOrderCountSupported) ? null : default,
                MediaPrePrintedSupported = !IsRequired(IppAttributeNames.MediaPrePrintedSupported) ? null : default,
                MediaReady = !IsRequired(IppAttributeNames.MediaReady) ? null : default,
                MediaRecycledSupported = !IsRequired(IppAttributeNames.MediaRecycledSupported) ? null : default,
                MediaRightMarginSupported = !IsRequired(IppAttributeNames.MediaRightMarginSupported) ? null : default,
                MediaSizeSupported = !IsRequired(IppAttributeNames.MediaSizeSupported) ? null : default,
                MediaSourceSupported = !IsRequired(IppAttributeNames.MediaSourceSupported) ? null : default,
                MediaSupported = !IsRequired(IppAttributeNames.MediaSupported) ? null : options.Media,
                MediaThicknessSupported = !IsRequired(IppAttributeNames.MediaThicknessSupported) ? null : default,
                MediaToothSupported = !IsRequired(IppAttributeNames.MediaToothSupported) ? null : default,
                MediaTopMarginSupported = !IsRequired(IppAttributeNames.MediaTopMarginSupported) ? null : default,
                MediaTypeSupported = !IsRequired(IppAttributeNames.MediaTypeSupported) ? null : default,
                MediaWeightMetricSupported = !IsRequired(IppAttributeNames.MediaWeightMetricSupported) ? null : default,
                MessageSupported = !IsRequired(IppAttributeNames.MessageSupported) ? null : default,
                MultipleDestinationUrisSupported = !IsRequired(IppAttributeNames.MultipleDestinationUrisSupported) ? null : default,
                MultipleDocumentHandlingDefault = !IsRequired(IppAttributeNames.MultipleDocumentHandlingDefault) ? (MultipleDocumentHandling?)null : MultipleDocumentHandling.SeparateDocumentsCollatedCopies,
                MultipleDocumentHandlingSupported = !IsRequired(IppAttributeNames.MultipleDocumentHandlingSupported) ? null : new ([MultipleDocumentHandling.SeparateDocumentsCollatedCopies, MultipleDocumentHandling.SeparateDocumentsUncollatedCopies]),
                MultipleDocumentJobsSupported = !IsRequired(IppAttributeNames.MultipleDocumentJobsSupported) ? null : options.MultipleDocumentJobsSupported,
                MultipleObjectHandlingDefault = !IsRequired(IppAttributeNames.MultipleObjectHandlingDefault) ? null : default,
                MultipleObjectHandlingSupported = !IsRequired(IppAttributeNames.MultipleObjectHandlingSupported) ? null : default,
                MultipleOperationTimeOut = !IsRequired(IppAttributeNames.MultipleOperationTimeOut) ? null : options.MultipleOperationTimeout,
                MultipleOperationTimeOutAction = !IsRequired(IppAttributeNames.MultipleOperationTimeOutAction) ? null : default,
                NaturalLanguageConfigured = !IsRequired(IppAttributeNames.NaturalLanguageConfigured) ? (NaturalLanguage?)null : options.NaturalLanguageConfigured,
                NumberOfRetriesDefault = !IsRequired(IppAttributeNames.NumberOfRetriesDefault) ? null : default,
                NumberOfRetriesSupported = !IsRequired(IppAttributeNames.NumberOfRetriesSupported) ? null : default,
                NumberUpDefault = !IsRequired(IppAttributeNames.NumberUpDefault) ? null : 1,
                NumberUpSupported = !IsRequired(IppAttributeNames.NumberUpSupported) ? null : new ([new SharpIpp.Protocol.Models.Range(1, 1)]),
                OperationsSupported = !IsRequired(IppAttributeNames.OperationsSupported) ? null : new ([ IppOperation.PrintJob, IppOperation.ValidateJob, IppOperation.CreateJob, IppOperation.SendDocument, IppOperation.CancelJob, IppOperation.GetJobAttributes, IppOperation.GetJobs, IppOperation.GetPrinterAttributes, IppOperation.HoldJob, IppOperation.ReleaseJob, IppOperation.PausePrinter, IppOperation.ResumePrinter, IppOperation.CloseJob, IppOperation.IdentifyPrinter, IppOperation.ResubmitJob, IppOperation.CancelJobs, IppOperation.CancelMyJobs, IppOperation.SetJobAttributes, IppOperation.SetPrinterAttributes, IppOperation.GetPrinterSupportedValues, IppOperation.CreatePrinterSubscriptions, IppOperation.CancelSubscription, IppOperation.GetSubscriptionAttributes ]),
                OrganizationNameSupported = !IsRequired(IppAttributeNames.OrganizationNameSupported) ? null : default,
                OrientationRequestedDefault = !IsRequired(IppAttributeNames.OrientationRequestedDefault) ? null : options.Orientation,
                OrientationRequestedSupported = !IsRequired(IppAttributeNames.OrientationRequestedSupported) ? null : Enum.GetValues(typeof(Orientation)).Cast<Orientation>().ToArray(),
                OutputAttributesDefault = !IsRequired(IppAttributeNames.OutputAttributesDefault) ? null : default,
                OutputAttributesSupported = !IsRequired(IppAttributeNames.OutputAttributesSupported) ? null : default,
                OutputBinDefault = !IsRequired(IppAttributeNames.OutputBinDefault) ? (OutputBin?)null : options.OutputBin.FirstOrDefault(),
                OutputBinSupported = !IsRequired(IppAttributeNames.OutputBinSupported) ? null : options.OutputBin,
                OutputDeviceSupported = !IsRequired(IppAttributeNames.OutputDeviceSupported) ? null : default,
                OutputDeviceUuidSupported = !IsRequired(IppAttributeNames.OutputDeviceUuidSupported) ? null : default,
                OverridesSupported = !IsRequired(IppAttributeNames.OverridesSupported) ? null : default,
                PageDeliveryDefault = !IsRequired(IppAttributeNames.PageDeliveryDefault) ? null : default,
                PageDeliverySupported = !IsRequired(IppAttributeNames.PageDeliverySupported) ? null : default,
                PageRangesSupported = !IsRequired(IppAttributeNames.PageRangesSupported) ? null : options.PageRangesSupported,
                PagesPerMinute = !IsRequired(IppAttributeNames.PagesPerMinute) ? null : options.PagesPerMinute,
                PagesPerMinuteColor = !IsRequired(IppAttributeNames.PagesPerMinuteColor) ? null : options.PagesPerMinuteColor,
                PdfFeaturesSupported = !IsRequired(IppAttributeNames.PdfFeaturesSupported) ? null : default,
                PdfKOctetsSupported = !IsRequired(IppAttributeNames.PdfKOctetsSupported) ? null : default,
                PdfVersionsSupported = !IsRequired(IppAttributeNames.PdfVersionsSupported) ? null : default,
                PdlOverrideSupported = !IsRequired(IppAttributeNames.PdlOverrideSupported) ? (PdlOverride?)null : PdlOverride.Attempted,
                PlatformShape = !IsRequired(IppAttributeNames.PlatformShape) ? null : default,
                PlatformTemperatureDefault = !IsRequired(IppAttributeNames.PlatformTemperatureDefault) ? null : default,
                PlatformTemperatureSupported = !IsRequired(IppAttributeNames.PlatformTemperatureSupported) ? null : default,
                PresentationDirectionNumberUpDefault = !IsRequired(IppAttributeNames.PresentationDirectionNumberUpDefault) ? null : default,
                PresentationDirectionNumberUpSupported = !IsRequired(IppAttributeNames.PresentationDirectionNumberUpSupported) ? null : default,
                PrintAccuracyDefault = !IsRequired(IppAttributeNames.PrintAccuracyDefault) ? null : default,
                PrintAccuracySupported = !IsRequired(IppAttributeNames.PrintAccuracySupported) ? null : default,
                PrintBaseDefault = !IsRequired(IppAttributeNames.PrintBaseDefault) ? null : default,
                PrintBaseSupported = !IsRequired(IppAttributeNames.PrintBaseSupported) ? null : default,
                PrintColorModeDefault = !IsRequired(IppAttributeNames.PrintColorModeDefault) ? (PrintColorMode?)null: options.PrintColorModes.FirstOrDefault(),
                PrintColorModeIccProfile = !IsRequired(IppAttributeNames.PrintColorModeIccProfiles) ? null : default,
                PrintColorModeSupported = !IsRequired(IppAttributeNames.PrintColorModeSupported) ? null : options.PrintColorModes,
                PrintContentOptimizeDefault = !IsRequired(IppAttributeNames.PrintContentOptimizeDefault) ? null : default,
                PrintContentOptimizeSupported = !IsRequired(IppAttributeNames.PrintContentOptimizeSupported) ? null : default,
                PrinterAlert = !IsRequired(IppAttributeNames.PrinterAlert) ? null : default,
                PrinterAlertDescription = !IsRequired(IppAttributeNames.PrinterAlertDescription) ? null : default,
                PrinterCameraImageUri = !IsRequired(IppAttributeNames.PrinterCameraImageUri) ? null : default,
                PrinterChargeInfo = !IsRequired(IppAttributeNames.PrinterChargeInfo) ? null : default,
                PrinterChargeInfoUri = !IsRequired(IppAttributeNames.PrinterChargeInfoUri) ? null : default,
                PrinterConfigChangeDateTime = !IsRequired(IppAttributeNames.PrinterConfigChangeDateTime) ? null : _startTime,
                PrinterConfigChanges = !IsRequired(IppAttributeNames.PrinterConfigChanges) ? null : 0,
                PrinterConfigChangeTime = !IsRequired(IppAttributeNames.PrinterConfigChangeTime) ? null : 0,
                PrinterContactCol = !IsRequired(IppAttributeNames.PrinterContactCol) ? null : default,
                PrinterCurrentTime = !IsRequired(IppAttributeNames.PrinterCurrentTime) ? null : dateTimeOffsetProvider.Now,
                PrinterDetailedStatusMessages = !IsRequired(IppAttributeNames.PrinterDetailedStatusMessages) ? null : new([]),
                PrinterDeviceId = !IsRequired(IppAttributeNames.PrinterDeviceId) ? null : GetPrinterDeviceId(options),
                PrinterDriverInstaller = !IsRequired(IppAttributeNames.PrinterDriverInstaller) ? null : new Uri(options.DriverInstallerUrl),
                PrinterFaxLogUri = !IsRequired(IppAttributeNames.PrinterFaxLogUri) ? null : default,
                PrinterFaxModemInfo = !IsRequired(IppAttributeNames.PrinterFaxModemInfo) ? null : default,
                PrinterFaxModemName = !IsRequired(IppAttributeNames.PrinterFaxModemName) ? null : default,
                PrinterFaxModemNumber = !IsRequired(IppAttributeNames.PrinterFaxModemNumber) ? null : default,
                PrinterFinisher = !IsRequired(IppAttributeNames.PrinterFinisher) ? null : default,
                PrinterFinisherDescription = !IsRequired(IppAttributeNames.PrinterFinisherDescription) ? null : default,
                PrinterFinisherSupplies = !IsRequired(IppAttributeNames.PrinterFinisherSupplies) ? null : default,
                PrinterFinisherSuppliesDescription = !IsRequired(IppAttributeNames.PrinterFinisherSuppliesDescription) ? null : default,
                PrinterGeoLocation = !IsRequired(IppAttributeNames.PrinterGeoLocation) ? null : default,
                PrinterIccProfile = !IsRequired(IppAttributeNames.PrinterIccProfiles) ? null : default,
                PrinterIds = !IsRequired(IppAttributeNames.PrinterIds) ? null : default,
                PrinterImpressionsCompleted = !IsRequired(IppAttributeNames.PrinterImpressionsCompleted) ? null : default,
                PrinterImpressionsCompletedCol = !IsRequired(IppAttributeNames.PrinterImpressionsCompletedCol) ? null : default,
                PrinterInfo = !IsRequired(IppAttributeNames.PrinterInfo) ? null : options.Name,
                PrinterInputTray = !IsRequired(IppAttributeNames.PrinterInputTray) ? null : default,
                PrinterIsAcceptingJobs = !IsRequired(IppAttributeNames.PrinterIsAcceptingJobs) ? null : true,
                PrinterLocation = !IsRequired(IppAttributeNames.PrinterLocation) ? null : options.Location,
                PrinterMakeAndModel = !IsRequired(IppAttributeNames.PrinterMakeAndModel) ? null : (!string.IsNullOrWhiteSpace(options.MakeAndModel) ? options.MakeAndModel : $"{options.Manufacturer} {options.Model}"),
                PrinterMandatoryJobAttributes = !IsRequired(IppAttributeNames.PrinterMandatoryJobAttributes) ? null : default,
                PrinterMediaSheetsCompleted = !IsRequired(IppAttributeNames.PrinterMediaSheetsCompleted) ? null : default,
                PrinterMediaSheetsCompletedCol = !IsRequired(IppAttributeNames.PrinterMediaSheetsCompletedCol) ? null : default,
                PrinterMessageFromOperator = !IsRequired(IppAttributeNames.PrinterMessageFromOperator) ? null : default,
                PrinterModeConfigured = !IsRequired(IppAttributeNames.PrinterModeConfigured) ? null : default,
                PrinterModeSupported = !IsRequired(IppAttributeNames.PrinterModeSupported) ? null : default,
                PrinterMoreInfo = !IsRequired(IppAttributeNames.PrinterMoreInfo) ? null : GetPrinterMoreInfo(),
                PrinterMoreInfoManufacturer = !IsRequired(IppAttributeNames.PrinterMoreInfoManufacturer) ? null : new Uri(options.MoreInfoManufacturerUrl),
                PrinterName = !IsRequired(IppAttributeNames.PrinterName) ? null : options.Name,
                PrinterOutputTray = !IsRequired(IppAttributeNames.PrinterOutputTray) ? null : default,
                PrinterPagesCompleted = !IsRequired(IppAttributeNames.PrinterPagesCompleted) ? null : default,
                PrinterPagesCompletedCol = !IsRequired(IppAttributeNames.PrinterPagesCompletedCol) ? null : default,
                PrinterRequestedClientType = !IsRequired(IppAttributeNames.PrinterRequestedClientType) ? null : default,
                PrinterRequestedJobAttributes = !IsRequired(IppAttributeNames.PrinterRequestedJobAttributes) ? null : default,
                PrinterResolutionDefault = !IsRequired(IppAttributeNames.PrinterResolutionDefault) ? null : options.Resolution.FirstOrDefault(),
                PrinterResolutionSupported = !IsRequired(IppAttributeNames.PrinterResolutionSupported) ? null : options.Resolution,
                PrinterResourceIds = !IsRequired(IppAttributeNames.PrinterResourceIds) ? null : default,
                PrinterServiceType = !IsRequired(IppAttributeNames.PrinterServiceType) ? null : default,
                PrinterState = !IsRequired(IppAttributeNames.PrinterState) ? null : _isPaused ? PrinterState.Stopped : _jobs.Values.Any(x => x.State == JobState.Pending || x.State == JobState.Processing) ? PrinterState.Processing : PrinterState.Idle,
                PrinterStateChangeDateTime = !IsRequired(IppAttributeNames.PrinterStateChangeDateTime) ? null : _startTime,
                PrinterStateChangeTime = !IsRequired(IppAttributeNames.PrinterStateChangeTime) ? null : 0,
                PrinterStateMessage = !IsRequired(IppAttributeNames.PrinterStateMessage) ? null : _isPaused ? "Paused" : _jobs.Values.Any(x => x.State == JobState.Pending || x.State == JobState.Processing) ? "Processing" : "Idle",
                PrinterStateReasons = !IsRequired(IppAttributeNames.PrinterStateReasons) ? null : (_isPaused ? new([PrinterStateReason.Paused]) : new([PrinterStateReason.None])),
                PrinterStaticResourceDirectoryUri = !IsRequired(IppAttributeNames.PrinterStaticResourceDirectoryUri) ? null : default,
                PrinterStaticResourceKOctetsFree = !IsRequired(IppAttributeNames.PrinterStaticResourceKOctetsFree) ? null : default,
                PrinterStaticResourceKOctetsSupported = !IsRequired(IppAttributeNames.PrinterStaticResourceKOctetsSupported) ? null : default,
                PrinterSupply = !IsRequired(IppAttributeNames.PrinterSupply) || !options.SupplyReportingEnabled ? null : options.Supplies,
                PrinterSupplyDescription = !IsRequired(IppAttributeNames.PrinterSupplyDescription) || !options.SupplyReportingEnabled ? null : options.Supplies.Select(s => $"{s.MarkerName?.Value ?? s.ColorName ?? "Supply"}: {s.Level}%").ToArray(),
                PrinterUpTime = !IsRequired(IppAttributeNames.PrinterUpTime) ? null : (int)(dateTimeOffsetProvider.UtcNow - _startTime).TotalSeconds,
                PrinterUriSupported = !IsRequired(IppAttributeNames.PrinterUriSupported) ? null : new([GetPrinterUrl("/ipp/print")]),
                PrinterUUID = !IsRequired(IppAttributeNames.PrinterUUID) ? null : $"urn:uuid:{options.UUID}",
                PrinterVolumeSupported = !IsRequired(IppAttributeNames.PrinterVolumeSupported) ? null : default,
                PrintObjectsSupported = !IsRequired(IppAttributeNames.PrintObjectsSupported) ? null : default,
                PrintQualityDefault = !IsRequired(IppAttributeNames.PrintQualityDefault) ? null : options.PrintQuality.FirstOrDefault(),
                PrintQualitySupported = !IsRequired(IppAttributeNames.PrintQualitySupported) ? null : options.PrintQuality,
                PrintScalingDefault = !IsRequired(IppAttributeNames.PrintScalingDefault) ? (PrintScaling?)null : options.PrintScaling.FirstOrDefault(),
                PrintScalingSupported = !IsRequired(IppAttributeNames.PrintScalingSupported) ? null : options.PrintScaling,
                PrintSupportsDefault = !IsRequired(IppAttributeNames.PrintSupportsDefault) ? null : default,
                PrintSupportsSupported = !IsRequired(IppAttributeNames.PrintSupportsSupported) ? null : default,
                PunchingHoleDiameterConfigured = !IsRequired(IppAttributeNames.PunchingHoleDiameterConfigured) ? null : default,
                PunchingLocationsSupported = !IsRequired(IppAttributeNames.PunchingLocationsSupported) ? null : default,
                PunchingOffsetSupported = !IsRequired(IppAttributeNames.PunchingOffsetSupported) ? null : default,
                PunchingReferenceEdgeSupported = !IsRequired(IppAttributeNames.PunchingReferenceEdgeSupported) ? null : default,
                PwgRasterDocumentResolutionSupported = !IsRequired(IppAttributeNames.PwgRasterDocumentResolutionSupported) ? null : default,
                PwgRasterDocumentSheetBack = !IsRequired(IppAttributeNames.PwgRasterDocumentSheetBack) ? null : default,
                PwgRasterDocumentTypeSupported = !IsRequired(IppAttributeNames.PwgRasterDocumentTypeSupported) ? null : default,
                QueuedJobCount = !IsRequired(IppAttributeNames.QueuedJobCount) ? null : _jobs.Values.Where(x => x.State == JobState.Pending || x.State == JobState.Processing).Count(),
                ReferenceUriSchemesSupported = !IsRequired(IppAttributeNames.ReferenceUriSchemesSupported) ? null : options.ReferenceUriSchemesSupported,
                RepertoireSupported = !IsRequired(IppAttributeNames.RepertoireSupported) ? null : default,
                RetryIntervalDefault = !IsRequired(IppAttributeNames.RetryIntervalDefault) ? null : default,
                RetryIntervalSupported = !IsRequired(IppAttributeNames.RetryIntervalSupported) ? null : default,
                RetryTimeOutDefault = !IsRequired(IppAttributeNames.RetryTimeOutDefault) ? null : default,
                RetryTimeOutSupported = !IsRequired(IppAttributeNames.RetryTimeOutSupported) ? null : default,
                SeparatorSheetsDefault = !IsRequired(IppAttributeNames.SeparatorSheetsDefault) ? null : default,
                SeparatorSheetsSupported = !IsRequired(IppAttributeNames.SeparatorSheetsSupported) ? null : default,
                SeparatorSheetsTypeSupported = !IsRequired(IppAttributeNames.SeparatorSheetsTypeSupported) ? null : default,
                SidesDefault = !IsRequired(IppAttributeNames.SidesDefault) ? (Sides?)null : options.Sides.FirstOrDefault(),
                SidesSupported = !IsRequired(IppAttributeNames.SidesSupported) ? null : options.Sides,
                StitchingAngleSupported = !IsRequired(IppAttributeNames.StitchingAngleSupported) ? null : default,
                StitchingLocationsSupported = !IsRequired(IppAttributeNames.StitchingLocationsSupported) ? null : default,
                StitchingMethodSupported = !IsRequired(IppAttributeNames.StitchingMethodSupported) ? null : default,
                StitchingOffsetSupported = !IsRequired(IppAttributeNames.StitchingOffsetSupported) ? null : default,
                StitchingReferenceEdgeSupported = !IsRequired(IppAttributeNames.StitchingReferenceEdgeSupported) ? null : default,
                SubjectSupported = !IsRequired(IppAttributeNames.SubjectSupported) ? null : default,
                ToNameSupported = !IsRequired(IppAttributeNames.ToNameSupported) ? null : default,
                TrimmingOffsetSupported = !IsRequired(IppAttributeNames.TrimmingOffsetSupported) ? null : default,
                TrimmingReferenceEdgeSupported = !IsRequired(IppAttributeNames.TrimmingReferenceEdgeSupported) ? null : default,
                TrimmingTypeSupported = !IsRequired(IppAttributeNames.TrimmingTypeSupported) ? null : default,
                TrimmingWhenSupported = !IsRequired(IppAttributeNames.TrimmingWhenSupported) ? null : default,
                UriAuthenticationSupported = !IsRequired(IppAttributeNames.UriAuthenticationSupported) ? null : new([UriAuthentication.None]),
                UriSecuritySupported = !IsRequired(IppAttributeNames.UriSecuritySupported) ? null : new([GetUriSecuritySupported()]),
                WhichJobsSupported = !IsRequired(IppAttributeNames.WhichJobsSupported) ? null : new([WhichJobs.Completed, WhichJobs.NotCompleted, WhichJobs.All]),
                XImagePositionDefault = !IsRequired(IppAttributeNames.XImagePositionDefault) ? null : default,
                XImagePositionSupported = !IsRequired(IppAttributeNames.XImagePositionSupported) ? null : default,
                XImageShiftDefault = !IsRequired(IppAttributeNames.XImageShiftDefault) ? null : default,
                XImageShiftSupported = !IsRequired(IppAttributeNames.XImageShiftSupported) ? null : default,
                XSide1ImageShiftDefault = !IsRequired(IppAttributeNames.XSide1ImageShiftDefault) ? null : default,
                XSide2ImageShiftDefault = !IsRequired(IppAttributeNames.XSide2ImageShiftDefault) ? null : default,
                YImagePositionDefault = !IsRequired(IppAttributeNames.YImagePositionDefault) ? null : default,
                YImagePositionSupported = !IsRequired(IppAttributeNames.YImagePositionSupported) ? null : default,
                YImageShiftDefault = !IsRequired(IppAttributeNames.YImageShiftDefault) ? null : default,
                YImageShiftSupported = !IsRequired(IppAttributeNames.YImageShiftSupported) ? null : default,
                YSide1ImageShiftDefault = !IsRequired(IppAttributeNames.YSide1ImageShiftDefault) ? null : default,
                YSide2ImageShiftDefault = !IsRequired(IppAttributeNames.YSide2ImageShiftDefault) ? null : default

            }
        };
    }

    private UriSecurity GetUriSecuritySupported()
    {
        var request = httpContextAccessor.HttpContext?.Request ?? throw new Exception("Unable to access HttpContext");
        return request.IsHttps ? UriSecurity.Tls : UriSecurity.None;
    }

    private IEnumerable<PrinterJob> GetPrinterJobs(WhichJobs? whichJobs)
    {
        if (whichJobs == WhichJobs.Completed)
            return _jobs.Values.Where(x => x.State == JobState.Completed || x.State == JobState.Aborted || x.State == JobState.Canceled);
        if (whichJobs == WhichJobs.NotCompleted)
            return _jobs.Values.Where(x => x.State == JobState.Processing || x.State == JobState.Pending);
        return _jobs.Values.Where(x => x.State.HasValue);
    }

    private GetJobsResponse GetGetJobsResponse(GetJobsRequest request)
    {
        IEnumerable<PrinterJob> jobs = GetPrinterJobs(request.OperationAttributes?.WhichJobs);
        if (request.OperationAttributes != null && request.OperationAttributes.MyJobs.GetValueOrDefault())
            jobs = jobs.Where(x => x.UserName?.Equals(request.OperationAttributes.RequestingUserName) ?? false);
        jobs = jobs.OrderByDescending(x => x.State).ThenByDescending(x => x.Id);
        if (request.OperationAttributes?.Limit.HasValue ?? false)
            jobs = jobs.Take(request.OperationAttributes.Limit.Value);
        logger.LogInformation("System returned jobs attributes");
        return new GetJobsResponse
        {
            RequestId = request.RequestId,
            Version = request.Version,
            StatusCode = IppStatusCode.SuccessfulOk,
            JobsAttributes = jobs.Select(x => GetJobDescriptionAttributes(x, request.OperationAttributes?.RequestedAttributes, true)).ToArray()
        };
    }

    private GetJobAttributesResponse GetGetJobAttributesResponse(GetJobAttributesRequest request)
    {
        var response = new GetJobAttributesResponse
        {
            RequestId = request.RequestId,
            Version = request.Version,
            StatusCode = IppStatusCode.ClientErrorNotPossible,
            JobAttributes = new()
        };
        var jobId = GetJobId(request);
        if (!jobId.HasValue)
            return response;
        if (!_jobs.TryGetValue(jobId.Value, out var job))
            return response;
        response.JobAttributes = GetJobDescriptionAttributes(job, request.OperationAttributes?.RequestedAttributes, false);
        response.StatusCode = IppStatusCode.SuccessfulOk;
        logger.LogInformation("System returned job attributes for job {id}", jobId);
        return response;
    }

    private JobDescriptionAttributes GetJobDescriptionAttributes(PrinterJob job, string[]? requestedAttributes, bool isBatch)
    {
        var jobAttributes = GetEffectiveJobTemplateAttributes(job);
        var jobName = job.Requests.Select(x => x switch
        {
            CreateJobRequest createJobRequest => createJobRequest.OperationAttributes?.JobName,
            PrintJobRequest printJobRequest => printJobRequest.OperationAttributes?.JobName,
            _ => null,
        }).FirstOrDefault(x => x != null);
        var ippAttributeFidelity = job.Requests.Select(x => x switch
        {
            CreateJobRequest createJobRequest => createJobRequest.OperationAttributes?.IppAttributeFidelity,
            PrintJobRequest printJobRequest => printJobRequest.OperationAttributes?.IppAttributeFidelity,
            _ => null,
        }).FirstOrDefault(x => x != null);
        var compression = job.Requests.Select(x => x switch
        {
            PrintJobRequest printJobRequest => printJobRequest.OperationAttributes?.Compression,
            SendDocumentRequest sendDocumentRequest => sendDocumentRequest.OperationAttributes?.Compression,
            _ => null,
        }).FirstOrDefault(x => x != null);


        bool IsRequired(string attributeName)
        {
            if (requestedAttributes is null || requestedAttributes.Length == 0)
                return !isBatch;
            if(requestedAttributes.All(x => x == "all"))
                return true;
            return requestedAttributes.Contains(attributeName);
        }
        var attributes = new JobDescriptionAttributes
        {
            JobId = job.Id,
            JobName = !IsRequired(IppAttributeNames.JobName) ? null : jobName,
            JobPrinterUri = !IsRequired(IppAttributeNames.JobPrinterUri) ? null : GetPrinterUrl(),
            JobState = !IsRequired(IppAttributeNames.JobState) ? null : job.State,
            JobStateReasons = !IsRequired(IppAttributeNames.JobState) ? null : new ([JobStateReason.None]),
            DateTimeAtCreation = !IsRequired(IppAttributeNames.DateTimeAtCreation) ? null : job.CreatedDateTime,
            TimeAtCreation = !IsRequired(IppAttributeNames.TimeAtCreation) ? null : (int)(job.CreatedDateTime - _startTime).TotalSeconds,
            DateTimeAtProcessing = !IsRequired(IppAttributeNames.DateTimeAtProcessing) ? null : job.ProcessingDateTime ?? DateTimeOffset.MinValue,
            TimeAtProcessing = !IsRequired(IppAttributeNames.TimeAtProcessing) ? null : job.ProcessingDateTime.HasValue ? (int)(job.ProcessingDateTime.Value - _startTime).TotalSeconds : -1,
            DateTimeAtCompleted = !IsRequired(IppAttributeNames.DateTimeAtCompleted) ? null : job.CompletedDateTime ?? DateTimeOffset.MinValue,
            TimeAtCompleted = !IsRequired(IppAttributeNames.TimeAtCompleted) ? null : job.CompletedDateTime.HasValue ? (int)(job.CompletedDateTime.Value - _startTime).TotalSeconds : -1,
            JobOriginatingUserName = !IsRequired(IppAttributeNames.JobOriginatingUserName) ? null : job.UserName,
            JobPrinterUpTime = !IsRequired(IppAttributeNames.JobPrinterUpTime) ? null : (int)(dateTimeOffsetProvider.UtcNow - _startTime).TotalSeconds,
            CopiesActual = !IsRequired(IppAttributeNames.Copies) ? null : jobAttributes?.Copies.HasValue ?? false ? new([ jobAttributes.Copies.Value ]) : null,
            MediaActual = !IsRequired(IppAttributeNames.Media) ? null : jobAttributes?.Media.HasValue ?? false ? new([ jobAttributes.Media.Value ]) : null,
            SidesActual = !IsRequired(IppAttributeNames.Sides) ? null : jobAttributes?.Sides.HasValue ?? false ? new([jobAttributes.Sides.Value]) : null,
            FinishingsActual = !IsRequired(IppAttributeNames.Finishings) ? null : jobAttributes?.Finishings,
            PrinterResolutionActual = !IsRequired(IppAttributeNames.PrinterResolution) ? null : jobAttributes?.PrinterResolution.HasValue ?? false ? new([jobAttributes.PrinterResolution.Value]) : null,
            OrientationRequestedActual = !IsRequired(IppAttributeNames.OrientationRequested) ? null : jobAttributes?.OrientationRequested.HasValue ?? false ? new([jobAttributes.OrientationRequested.Value]) : null,
            JobPriorityActual = !IsRequired(IppAttributeNames.JobPriority) ? null : jobAttributes?.JobPriority.HasValue ?? false ? new([jobAttributes.JobPriority.Value]) : null,
            JobHoldUntilActual = !IsRequired(IppAttributeNames.JobHoldUntil) ? null : jobAttributes?.JobHoldUntil.HasValue ?? false ? new([jobAttributes.JobHoldUntil.Value]) : null,
            NumberUpActual = !IsRequired(IppAttributeNames.NumberUp) ? null : jobAttributes?.NumberUp.HasValue ?? false ? new([jobAttributes.NumberUp.Value]) : null,
            JobImpressions = !IsRequired(IppAttributeNames.JobImpressions) ? null : job.Impressions ?? job.ImpressionsCompleted,
            JobImpressionsCompleted = !IsRequired(IppAttributeNames.JobImpressionsCompleted) ? null : job.ImpressionsCompleted ?? (job.State == JobState.Completed ? job.Impressions ?? 1 : 0)
        };
        return attributes;
    }

    private static CUPSGetPrintersResponse GetCUPSGetPrintersResponse(CUPSGetPrintersRequest request)
    {
        return new CUPSGetPrintersResponse
        {
            RequestId = request.RequestId,
            Version = request.Version,
            StatusCode = IppStatusCode.SuccessfulOk
        };
    }

    private CreateJobResponse GetCreateJobResponse(CreateJobRequest request)
    {
        var response = new CreateJobResponse
        {
            RequestId = request.RequestId,
            Version = request.Version,
            StatusCode = IppStatusCode.ClientErrorNotPossible,
            JobAttributes = new()
            {
                JobState = JobState.Pending,
                JobStateReasons = new ([JobStateReason.None])
            }
        };

        if (!CanAddJob())
        {
            logger.LogWarning("Rejecting CreateJob because active job limit is reached");
            response.StatusCode = IppStatusCode.ServerErrorBusy;
            return response;
        }

        var job = new PrinterJob(GetNextValue(), request.OperationAttributes?.RequestingUserName, dateTimeOffsetProvider.UtcNow)
        {
            Impressions = request.OperationAttributes?.JobImpressions ?? request.OperationAttributes?.JobImpressionsEstimated
        };
        response.JobAttributes.JobId = job.Id;
        FillWithDefaultValues(job.Id, request.OperationAttributes ??= new());
        FillWithDefaultValues(request.JobTemplateAttributes ??= new());
        job.Requests.Add(request);
        if (!_jobs.TryAdd(job.Id, job))
            return response;
        PruneJobHistory();
        response.StatusCode = IppStatusCode.SuccessfulOk;
        logger.LogInformation("Job {id} has been added to queue", job.Id);
        return response;
    }

    private async Task<CancelJobResponse> GetCancelJobResponseAsync(CancelJobRequest request)
    {
        var response = new CancelJobResponse
        {
            RequestId = request.RequestId,
            Version = request.Version,
            StatusCode = IppStatusCode.ClientErrorNotPossible
        };
        var jobId = GetJobId(request);
        if (!jobId.HasValue)
            return response;
        if (!_jobs.TryGetValue(jobId.Value, out var job))
            return response;
        var copy = new PrinterJob(job);
        if (!copy.TrySetState(JobState.Canceled, dateTimeOffsetProvider.UtcNow))
            return response;
        if (!_jobs.TryUpdate(jobId.Value, copy, job))
            return response;
        await copy.ClearDocumentStreamsAsync();
        response.StatusCode = IppStatusCode.SuccessfulOk;
        logger.LogInformation("Job {id} has been canceled", jobId);
        return response;
    }

    public async Task<PrinterJob?> StartJobProcessingAsync(int jobId)
    {
        if (_isPaused)
            return null;
        if (!_jobs.TryGetValue(jobId, out var job))
            return null;
        if (job.State != JobState.Pending)
            return null;

        var copy = new PrinterJob(job);
        if (!copy.TrySetState(JobState.Processing, dateTimeOffsetProvider.UtcNow))
            return null;
        if (!_jobs.TryUpdate(jobId, copy, job))
            return null;
        return copy;
    }

    public async Task AddCompletedJobAsync(int jobId, int? impressionsCompleted = null)
    {
        if (!_jobs.TryGetValue(jobId, out var job))
            return;
        var copy = new PrinterJob(job);
        if (impressionsCompleted.HasValue)
            copy.ImpressionsCompleted = impressionsCompleted.Value;
        else
            copy.ImpressionsCompleted ??= copy.Impressions ?? 1;
        if (!copy.TrySetState(JobState.Completed, dateTimeOffsetProvider.UtcNow))
            return;
        if (!_jobs.TryUpdate(jobId, copy, job))
            return;
        await copy.ClearDocumentStreamsAsync();
        logger.LogInformation("Job {id} has been completed", job.Id);
    }

    public async Task AddAbortedJobAsync(int jobId, Exception ex)
    {
        if (!_jobs.TryGetValue(jobId, out var job))
            return;
        var copy = new PrinterJob(job);
        if (!copy.TrySetState(JobState.Aborted, dateTimeOffsetProvider.UtcNow))
            return;
        if (!_jobs.TryUpdate(jobId, copy, job))
            return;
        await copy.ClearDocumentStreamsAsync();
        logger.LogError(ex, "Job {id} has been aborted", job.Id);
    }

    private async Task<PrintJobResponse> GetPrintJobResponseAsync(PrintJobRequest request)
    {
        var response = new PrintJobResponse
        {
            RequestId = request.RequestId,
            Version = request.Version,
            StatusCode = IppStatusCode.ClientErrorNotPossible,
            JobAttributes = new()
            {
                JobId = 0,
                JobState = JobState.Pending,
                JobStateReasons = new([JobStateReason.None])
            }
        };

        if (!CanAddJob())
        {
            logger.LogWarning("Rejecting PrintJob because active job limit is reached");
            response.StatusCode = IppStatusCode.ServerErrorBusy;
            return response;
        }

        if (request.Document != null)
        {
            if (!request.Document.CanSeek)
            {
                throw new ArgumentException("Document stream must be seekable.", nameof(request.Document));
            }
            if (request.Document.Position > 0)
            {
                request.Document.Position = 0;
            }
        }

        var job = new PrinterJob(GetNextValue(), request.OperationAttributes?.RequestingUserName, dateTimeOffsetProvider.UtcNow)
        {
            Impressions = request.OperationAttributes?.JobImpressions ?? request.OperationAttributes?.JobImpressionsEstimated
        };
        response.JobAttributes.JobId = job.Id;
        FillWithDefaultValues(job.Id, request.OperationAttributes ??= new());
        FillWithDefaultValues(request.JobTemplateAttributes ??= new());
        job.Requests.Add(request);
        if (!job.TrySetState(JobState.Pending, dateTimeOffsetProvider.UtcNow))
            return response;
        if (!_jobs.TryAdd(job.Id, job))
            return response;
        PruneJobHistory();
        response.StatusCode = IppStatusCode.SuccessfulOk;
        logger.LogInformation("Job {id} has been added to queue", job.Id);
        jobQueue.Writer.TryWrite(job.Id);
        return response;
    }

    private Uri GetPrinterUrl(string? path = null, string? appendPath = null)
    {
        var request = httpContextAccessor.HttpContext?.Request ?? throw new Exception("Unable to access HttpContext");
        path ??= request.Path;
        if(!string.IsNullOrEmpty(appendPath))
        {
            if (!path.EndsWith('/'))
                path += '/';
            path += appendPath;
        }
        return new Uri($"ipp://{request.Host}{request.PathBase}{(path is null ? request.Path : path)}");
    }

    private Uri GetPrinterMoreInfo()
    {
        var request = httpContextAccessor.HttpContext?.Request ?? throw new Exception("Unable to access HttpContext");
        return new Uri($"{request.Scheme}://{request.Host}{request.PathBase}");
    }

    private static int? GetJobId(IIppJobRequest request)
    {
        if(request.OperationAttributes is not JobOperationAttributes jobOperationAttributes)
            return null;
        if (jobOperationAttributes.JobUri.HasValue
            && jobOperationAttributes.JobUri.Value.HasValue
            && int.TryParse(jobOperationAttributes.JobUri.Value.Value.Segments.LastOrDefault(), out int idFromUri))
            return idFromUri;
        return jobOperationAttributes.JobId;
    }

    private static int? GetJobId(CloseJobRequest request)
    {
        if (request.OperationAttributes is not CloseJobOperationAttributes attr)
            return null;
        if (attr.JobUri.HasValue && attr.JobUri.Value.HasValue && int.TryParse(attr.JobUri.Value.Value.Segments.LastOrDefault(), out int idFromUri))
            return idFromUri;
        return attr.JobId;
    }

    private static int? GetJobId(ResubmitJobRequest request)
    {
        if (request.OperationAttributes is not ResubmitJobOperationAttributes attr)
            return null;
        if (attr.JobUri.HasValue && attr.JobUri.Value.HasValue && int.TryParse(attr.JobUri.Value.Value.Segments.LastOrDefault(), out int idFromUri))
            return idFromUri;
        return attr.JobId;
    }



    private void FillWithDefaultValues(JobTemplateAttributes? attributes)
    {
        if (attributes == null)
            return;
        var options = printerOptions.Value;
        attributes.PrintScaling ??= options.PrintScaling.FirstOrDefault();
        attributes.Sides ??= options.Sides.FirstOrDefault();
        attributes.Media ??= options.Media.FirstOrDefault();
        attributes.PrinterResolution ??= options.Resolution.FirstOrDefault();
        attributes.Finishings ??= options.Finishings;
        attributes.PrintQuality ??= options.PrintQuality.FirstOrDefault();
        attributes.JobPriority ??= options.JobPriority;
        attributes.Copies ??= options.Copies;
        attributes.OrientationRequested ??= options.Orientation;
        attributes.JobHoldUntil ??= options.JobHoldUntil;
        attributes.PrintColorMode ??= options.PrintColorModes.FirstOrDefault();
        attributes.OutputBin ??= options.OutputBin.FirstOrDefault();
    }

    private void FillWithDefaultValues(SendDocumentOperationAttributes? attributes)
    {
        if (attributes is null)
            return;
        var options = printerOptions.Value;
        if (!attributes.DocumentFormat.HasValue || !attributes.DocumentFormat.Value.HasValue)
            attributes.DocumentFormat = options.DocumentFormat;
    }

    private void FillWithDefaultValues(int jobId, PrintJobOperationAttributes? attributes)
    {
        if (attributes is null)
            return;
        var options = printerOptions.Value;
        if (!attributes.DocumentFormat.HasValue || !attributes.DocumentFormat.Value.HasValue)
            attributes.DocumentFormat = options.DocumentFormat;
        FillWithDefaultValues(jobId, attributes as CreateJobOperationAttributes);
    }

    private void FillWithDefaultValues(int jobId, CreateJobOperationAttributes? attributes)
    {
        if (attributes is null)
            return;
        if (string.IsNullOrEmpty(attributes.JobName))
            attributes.JobName = $"Job {jobId}";
    }

    public IEnumerable<PrinterJob> ActiveJobs => _jobs.Values.Where(x => 
        x.State == JobState.Pending || 
        x.State == JobState.Processing || 
        x.IsNew || 
        x.IsHold);

    public async Task<bool> CancelJobAsync(int jobId)
    {
        if (!_jobs.TryGetValue(jobId, out var job))
            return false;
        var copy = new PrinterJob(job);
        if (!copy.TrySetState(JobState.Canceled, dateTimeOffsetProvider.UtcNow))
            return false;
        if (!_jobs.TryUpdate(jobId, copy, job))
            return false;
        await copy.ClearDocumentStreamsAsync();
        logger.LogInformation("Job {id} has been canceled due to timeout", jobId);
        return true;
    }

    private static int? GetJobId(SetJobAttributesRequest request)
    {
        if (request.OperationAttributes is not SetJobAttributesOperationAttributes attr)
            return null;
        if (attr.JobUri.HasValue && attr.JobUri.Value.HasValue && int.TryParse(attr.JobUri.Value.Value.Segments.LastOrDefault(), out int idFromUri))
            return idFromUri;
        return attr.JobId;
    }

    public JobTemplateAttributes GetEffectiveJobTemplateAttributes(PrinterJob job)
    {
        var baseAttributes = job.Requests.Select(x => x switch
        {
            CreateJobRequest createJobRequest => createJobRequest.JobTemplateAttributes,
            PrintJobRequest printJobRequest => printJobRequest.JobTemplateAttributes,
            _ => null,
        }).FirstOrDefault(x => x != null);

        var merged = new JobTemplateAttributes();
        if (baseAttributes != null)
        {
            CopyJobTemplateAttributes(baseAttributes, merged);
        }

        foreach (var req in job.Requests)
        {
            if (req is SetJobAttributesRequest setJobAttributesRequest && setJobAttributesRequest.JobTemplateAttributes != null)
            {
                CopyJobTemplateAttributes(setJobAttributesRequest.JobTemplateAttributes, merged, onlyNonNull: true);
            }
        }
        return merged;
    }

    private static void CopyJobTemplateAttributes(JobTemplateAttributes source, JobTemplateAttributes target, bool onlyNonNull = false)
    {
        if (source == null || target == null)
            return;
        foreach (var prop in typeof(JobTemplateAttributes).GetProperties())
        {
            if (!prop.CanRead || !prop.CanWrite)
                continue;
            var val = prop.GetValue(source);
            if (onlyNonNull && val == null)
                continue;
            prop.SetValue(target, val);
        }
    }

    private async Task<SetJobAttributesResponse> GetSetJobAttributesResponseAsync(SetJobAttributesRequest request)
    {
        var response = new SetJobAttributesResponse
        {
            RequestId = request.RequestId,
            Version = request.Version,
            StatusCode = IppStatusCode.ClientErrorNotPossible
        };
        var jobId = GetJobId(request);
        if (!jobId.HasValue)
            return response;
        if (!_jobs.TryGetValue(jobId.Value, out var job))
        {
            response.StatusCode = SharpIpp.Protocol.Models.IppStatusCode.ClientErrorNotFound;
            return response;
        }

        if (job.State == JobState.Completed || job.State == JobState.Canceled || job.State == JobState.Aborted)
        {
            response.StatusCode = IppStatusCode.ClientErrorNotPossible;
            return response;
        }

        var copy = new PrinterJob(job);
        copy.Requests.Add(request);
        if (!_jobs.TryUpdate(jobId.Value, copy, job))
            return response;

        response.StatusCode = IppStatusCode.SuccessfulOk;
        logger.LogInformation("Job attributes set for job {id}", jobId.Value);
        return response;
    }

    private SetPrinterAttributesResponse GetSetPrinterAttributesResponse(SetPrinterAttributesRequest request)
    {
        logger.LogInformation("Received SetPrinterAttributes request; ignoring changes as configured (no-op)");
        return new SetPrinterAttributesResponse
        {
            RequestId = request.RequestId,
            Version = request.Version,
            StatusCode = IppStatusCode.SuccessfulOk
        };
    }

    private GetPrinterSupportedValuesResponse GetGetPrinterSupportedValuesResponse(GetPrinterSupportedValuesRequest request)
    {
        var options = printerOptions.Value;
        bool IsRequired(string attributeName) => IsAttributeRequired(request.OperationAttributes?.RequestedAttributes, attributeName);

        var response = new GetPrinterSupportedValuesResponse
        {
            RequestId = request.RequestId,
            Version = request.Version,
            StatusCode = IppStatusCode.SuccessfulOk,
            PrinterAttributes = new()
            {
                MediaSupported = !IsRequired("media-supported") && !IsRequired("media") ? null : options.Media,
                FinishingsSupported = !IsRequired("finishings-supported") && !IsRequired("finishings") ? null : options.Finishings,
                SidesSupported = !IsRequired("sides-supported") && !IsRequired("sides") ? null : options.Sides,
                OrientationRequestedSupported = !IsRequired("orientation-requested-supported") && !IsRequired("orientation-requested") ? null : Enum.GetValues(typeof(Orientation)).Cast<Orientation>().ToArray(),
                PrintQualitySupported = !IsRequired("print-quality-supported") && !IsRequired("print-quality") ? null : options.PrintQuality,
                NumberUpSupported = !IsRequired("number-up-supported") && !IsRequired("number-up") ? null : new ([new SharpIpp.Protocol.Models.Range(1, 1)]),
                PrinterResolutionSupported = !IsRequired("printer-resolution-supported") && !IsRequired("printer-resolution") ? null : options.Resolution,
                CopiesSupported = !IsRequired("copies-supported") && !IsRequired("copies") ? null : new SharpIpp.Protocol.Models.Range(1, options.MaxCopies),
                JobHoldUntilSupported = !IsRequired("job-hold-until-supported") && !IsRequired("job-hold-until") ? null : options.JobHoldUntilSupported
            }
        };

        logger.LogInformation("System returned supported values for printer attributes");
        return response;
    }





    public async ValueTask DisposeAsync()
    {
        await DisposeAsyncCore().ConfigureAwait(false);
        Dispose(disposing: false);
        GC.SuppressFinalize(this);
    }

    protected virtual async ValueTask DisposeAsyncCore()
    {
        foreach (var job in _jobs.Values)
            await job.DisposeAsync();
        _jobs.Clear();
    }

    protected virtual void Dispose(bool disposing)
    {
        if (disposedValue)
            return;
        if (disposing)
        {
            foreach (var job in _jobs.Values)
                job.Dispose();
            _jobs.Clear();
        }
        disposedValue = true;
    }

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }
}

