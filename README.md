# IppPrinter

[![win | installer](https://img.shields.io/badge/win-installer-blue)](https://github.com/danielklecha/IppPrinter/releases/latest/download/IppPrinter-Setup-x64.exe)
[![GitHub downloads](https://img.shields.io/github/downloads/danielklecha/IppPrinter/total.svg)](https://github.com/danielklecha/IppPrinter/releases)
[![License](https://img.shields.io/badge/license-MIT-green.svg)](LICENSE.txt)

**IppPrinter** is a lightweight, cross-platform IPP (Internet Printing Protocol) print server web application built with ASP.NET Core and powered by the [`SharpIppNext`](https://github.com/danielklecha/SharpIppNext) library.

## Features

- **Full IPP Protocol Support**: Implements standard IPP 1.1 and 2.0 operations (`Print-Job`, `Create-Job`, `Send-Document`, `Get-Printer-Attributes`, `Get-Job-Attributes`, `Get-Jobs`, `Cancel-Job`, subscriptions, etc.) via `SharpIppNext`.
- **HTTP & HTTPS (IPPS) Support**: Supports secure encrypted printing over TLS/HTTPS (IPPS) as well as standard unencrypted HTTP connections on port `631`.
- **Automatic Document Conversion**: Converts incoming document payloads automatically to PDF format for storage and processing:
  - **PWG Raster** (`image/pwg-raster`)
  - **Apple Raster / URF** (`image/urf`)
  - **Images** (`image/png`, `image/jpeg`, `image/gif`, `image/bmp`)
  - **Plain Text** (`text/plain`) with automatic line wrapping and PDF layout generation
  - **Native PDF** (`application/pdf`) pass-through
- **Post-Process Job Action**: Automatically execute external application (e.g. Adobe Acrobat, SumatraPDF), CLI print utility (`lp`/`lpr`), or custom script after receiving print job.
- **mDNS / Zeroconf Discovery**: Automatic network printer advertisement (`PrinterDiscoveryService`) allowing clients on the network (Windows, macOS, iOS AirPrint, Android, Linux CUPS) to discover the printer automatically.
- **Multiple IPP Endpoints**: Supports common IPP path patterns (`/`, `/ipp`, `/ipp/print`, `/printers/{PrinterName}`, `/{PrinterName}`).
- **Cross-Platform & Windows Service**: Runs on Windows, Linux, and macOS. Includes native Windows Service hosting and Event Log integration.
- **Background Job Queue**: Async job queue with background processing and job timeout handling.
- **Easy Deployment & Setup**: Comes with PowerShell automation scripts (`Add-Printer.ps1` / `Remove-Printer.ps1`) and an optional Inno Setup GUI installer.

## Requirements

- [.NET 10.0 Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) installed.

## Installation

The printer is compatible with any standard IPP client.

### Windows (GUI Installer)

1. Download [`IppPrinter-Setup-x64.exe`](https://github.com/danielklecha/IppPrinter/releases/latest/download/IppPrinter-Setup-x64.exe) from [Releases](https://github.com/danielklecha/IppPrinter/releases).
2. Run the installer wizard as Administrator.
3. Select your desired **Installation Type**:
   - **Windows Service (Default)**: Runs system-wide in the background before user logon (Session 0). Recommended for headless servers. *Note: Cannot display GUI windows or call interactive desktop applications.*
   - **Startup Application**: Runs automatically upon user logon in the active desktop session. **Allows post-process actions to launch applications with a Graphical User Interface (GUI)** (e.g. Acrobat Reader, SumatraPDF window, or interactive scripts).
4. The setup automatically configures the firewall rule for port `631`, sets up permissions, and registers the `IppPrinter` print queue.

### Windows (Printer Wizard)

1. Open **Printers & scanners** in Windows Settings.
2. Click **Add device**.
3. Click **Add a new device manually**.
4. Select **Add a printer using an IP address or hostname**.
5. Select **IPP Device** as the device type.
6. Enter `http://127.0.0.1:631/` or `https://127.0.0.1:631/` as the **Hostname or IP address**.
7. Click **Next** to finish adding the printer.
8. (Optionally) Click **Print a test page**.
9. Click **Finish**.

### Windows (PowerShell Script)

```powershell
Add-Printer -Name "IppPrinter" -IppURL "http://127.0.0.1:631/ipp/print"
```

All installation and removal steps are also automated via `Setup/Add-Printer.ps1` and `Setup/Remove-Printer.ps1`.

### Android

Tested with the `NetPrinter` app and standard Android IPP print services.

## Post-Processing Configuration

`IppPrinter` can automatically execute an external application, viewer, or command-line tool whenever a print job is received and converted to PDF.

You can configure post-processing actions in `appsettings.json` under the `"Printer"` configuration section:

- **`PostProcessName`**: The full path to the executable or command (e.g. `AcroRd32.exe`, `SumatraPDF.exe`, `lp`, `powershell.exe`).
- **`PostProcessArguments`**: Arguments passed to the process. You can use placeholders for the target PDF file path:
  - `{fullName}`: Replaced with the path of the saved PDF file.
  - If no placeholder is included, the file path is automatically appended in quotes (`"{fullName}"`).

### Usage Examples 

#### Open pdf after printing via SumatraPDF

1. Install SumatraPDF: `winget install --id SumatraPDF.SumatraPDF -e -s winget --accept-package-agreements --accept-source-agreements --silent --scope machine`
2. Install IppPrinter as Startup Application with PostProcessName = "C:\Program Files\SumatraPDF\SumatraPDF.exe" and PostProcessArguments = "{fullName}" using `Setup/Add-Printer.ps1`

#### Open PDF after printing via Adobe Acrobat Reader

1. Install Adobe Acrobat Reader: `winget install --id Adobe.Acrobat.Reader.64-bit -e -s winget --accept-package-agreements --accept-source-agreements --silent --scope machine`
2. Install IppPrinter as Startup Application with PostProcessName = "C:\\Program Files\\Adobe\\Acrobat Reader DC\\Reader\\AcroRd32.exe" and PostProcessArguments = "/t \"{fullName}\""

#### Silent Print to Default Windows Printer via SumatraPDF

1. Install SumatraPDF: `winget install --id SumatraPDF.SumatraPDF -e -s winget --accept-package-agreements --accept-source-agreements --silent --scope machine`
2. Install IppPrinter as Windows Service with PostProcessName = "C:\Program Files\SumatraPDF\SumatraPDF.exe" and PostProcessArguments = "-print-to-default \"{fullName}\"" using `Setup/Add-Printer.ps1`

## Contributing & Testing

For local development setup, building the installer, and integration testing instructions, see [CONTRIBUTION.md](CONTRIBUTION.md).

## License

`IppPrinter` is provided as-is under the [MIT license](LICENSE.txt).

For details on third-party dependencies and their licenses, see [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt).