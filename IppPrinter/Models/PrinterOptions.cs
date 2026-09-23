using SharpIpp.Protocol.Models;

namespace IppPrinter.Models;

public class PrinterOptions
{
    public string Name { get; set; } = "IppPrinter";
    public string DnsSdName { get; set; } = "IppPrinter [231076]";
    public Guid UUID { get; set; } = new Guid("d178b387-3a93-4d17-a561-007f876c4901");
    public string FirmwareName { get; set; } = "SIN22183498";
    public Sides[] Sides { get; set; } = [
        SharpIpp.Protocol.Models.Sides.OneSided,
        SharpIpp.Protocol.Models.Sides.TwoSidedLongEdge,
        SharpIpp.Protocol.Models.Sides.TwoSidedShortEdge
    ];
    public PrintScaling[] PrintScaling { get; set; } = [SharpIpp.Protocol.Models.PrintScaling.Auto];
    public Media[] Media { get; set; } = [
        "iso_a4_210x297mm",
        "na_executive_7.25x10.5in",
        "na_letter_8.5x11in",
        "na_legal_8.5x14in",
        "na_govt-letter_8x10in",
        "na_invoice_5.5x8.5in",
        "iso_a5_148x210mm",
        "jis_b5_182x257mm",
        "jpn_hagaki_100x148mm",
        "iso_a6_105x148mm",
        "na_index-4x6_4x6in",
        "na_index-5x8_5x8in",
        "na_index-3x5_3x5in",
        "na_monarch_3.875x7.5in",
        "na_number-10_4.125x9.5in",
        "iso_dl_110x220mm",
        "iso_c5_162x229mm",
        "iso_c6_114x162mm",
        "na_a2_4.375x5.75in",
        "jpn_chou3_120x235mm",
        "jpn_chou4_90x205mm",
        "oe_photo-l_3.5x5in",
        "jpn_photo-2l_127x177.8mm",
        "na_5x7_5x7in",
        "oe_photo_4x5in",
        "na_personal_3.625x6.5in",
        "iso_b5_176x250mm",
        "om_small-photo_100x150mm",
        "na_foolscap_8.5x13in",
        "custom_min_3x5in",
        "custom_max_8.5x14in",
        "stationery",
        "photographic-glossy"
    ];
    public Resolution[] Resolution { get; set; } = [new(600, 600, ResolutionUnit.DotsPerInch)];
    public Finishings[] Finishings { get; set; } = [SharpIpp.Protocol.Models.Finishings.None];
    public PrintQuality[] PrintQuality { get; set; } = [SharpIpp.Protocol.Models.PrintQuality.High];
    public int JobPriority { get; set; } = 1;
    public int Copies { get; set; } = 1;
    public int MaxCopies { get; set; } = 9999;
    public Orientation Orientation { get; set; } = Orientation.Portrait;
    public JobHoldUntil JobHoldUntil { get; set; } = JobHoldUntil.NoHold;
    public DocumentFormat DocumentFormat { get; set; } = DocumentFormat.ApplicationPdf;
    public OutputBin[] OutputBin { get; set; } = [SharpIpp.Protocol.Models.OutputBin.Top];
    public PrintColorMode[] PrintColorModes { get; set; } = [PrintColorMode.Color];
    public string Location { get; set; } = "Internet";
    public string Manufacturer { get; set; } = "danielklecha";
    public string Model { get; set; } = "IppPrinter1";
    public string SerialNumber { get; set; } = "SIN279BJ23J07PX";
    public int PagesPerMinute { get; set; } = 20;
    public int PagesPerMinuteColor { get; set; } = 20;
    public bool MultipleDocumentJobsSupported { get; set; } = true;
    public bool PageRangesSupported { get; set; } = false;
    public int MultipleOperationTimeout { get; set; } = 120;
    public JobHoldUntil[] JobHoldUntilSupported { get; set; } = [JobHoldUntil.NoHold];
    public UriScheme[] ReferenceUriSchemesSupported { get; set; } = [UriScheme.Ftp, UriScheme.Http, UriScheme.Https];
    public int MaxPendingJobs { get; set; } = 10;
    public int MaxDocumentsPerJob { get; set; } = 10;
    public int MaxJobHistory { get; set; } = 100;
    public int JobCancelAfter { get; set; } = 10800;
    public string? JobsPath { get; set; }
    public string? PostProcessName { get; set; }
    public string? PostProcessArguments { get; set; }

    public bool? ColorSupported { get; set; }
    public string? MakeAndModel { get; set; }
    public DocumentFormat[] DocumentFormatSupported { get; set; } = [
        DocumentFormat.ApplicationPdf,
        DocumentFormat.ImagePwgRaster,
        DocumentFormat.ImageUrf,
        DocumentFormat.ImageJpeg,
        (DocumentFormat)"image/png",
        (DocumentFormat)"image/gif",
        (DocumentFormat)"image/webp",
        (DocumentFormat)"image/bmp",
        (DocumentFormat)"image/tiff",
        (DocumentFormat)"text/plain"
    ];
    public string DriverInstallerUrl { get; set; } = "https://github.com/danielklecha/SharpIppNext";
    public string MoreInfoManufacturerUrl { get; set; } = "https://github.com/danielklecha/SharpIppNext";
    public bool SupplyReportingEnabled { get; set; } = true;
    public PrinterSupply[] Supplies { get; set; } = [
        new PrinterSupply
        {
            Type = PrinterSupplyType.Toner,
            Level = 100,
            MaxCapacity = 100,
            ColorName = "black",
            MarkerName = "Black Toner",
            MarkerType = MarkerType.Toner,
            Unit = CapacityUnit.Percent
        },
        new PrinterSupply
        {
            Type = PrinterSupplyType.Toner,
            Level = 100,
            MaxCapacity = 100,
            ColorName = "cyan",
            MarkerName = "Cyan Toner",
            MarkerType = MarkerType.Toner,
            Unit = CapacityUnit.Percent
        },
        new PrinterSupply
        {
            Type = PrinterSupplyType.Toner,
            Level = 100,
            MaxCapacity = 100,
            ColorName = "magenta",
            MarkerName = "Magenta Toner",
            MarkerType = MarkerType.Toner,
            Unit = CapacityUnit.Percent
        },
        new PrinterSupply
        {
            Type = PrinterSupplyType.Toner,
            Level = 100,
            MaxCapacity = 100,
            ColorName = "yellow",
            MarkerName = "Yellow Toner",
            MarkerType = MarkerType.Toner,
            Unit = CapacityUnit.Percent
        }
    ];
    public SharpIpp.Protocol.Models.NaturalLanguage NaturalLanguageConfigured { get; set; } = SharpIpp.Protocol.Models.NaturalLanguage.EnUs;
    public string NaturalLanguage { get; set; } = "en-us";
}