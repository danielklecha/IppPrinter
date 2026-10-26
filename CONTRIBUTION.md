# Contributing to IppPrinter

Thank you for your interest in contributing to the **IppPrinter** project! This document explains how to set up the development environment, work with the codebase, compile the Windows Installer, and understand the automated CI/CD pipeline.

---

## 1. Prerequisites

To build and run this project, make sure you have the following installed:
- **.NET 10 SDK** (v10.0.301 or later)
- **Inno Setup 6** (Required for compiling the installer; can be installed via `choco install innosetup -y` in an Administrator shell)
- **PowerShell 5.1 or PowerShell 7**

---

## 2. Development Guide

### Clone the Repository

```bash
git clone https://github.com/danielklecha/IppPrinter.git
cd IppPrinter
```

### Build the Codebase

Restore packages and build the solution:

```bash
dotnet restore
dotnet build
```

### Run the Application Locally
You can run the application directly from the CLI. By default, the production profile binds to HTTP port `631` (the standard Internet Printing Protocol port):

```bash
dotnet run --project IppPrinter/IppPrinter.csproj
```

### Running the Self-Test

The application includes a self-test command that generates a sample wrapped PDF document using the printer's layout converters.

```bash
dotnet run --project IppPrinter/IppPrinter.csproj -- --test
```

This will create a `test_text.pdf` file under the executable's `jobs/` directory.

---

## 3. Working with the Windows Installer

The project is configured to run as a **Windows Service** when started by the Service Control Manager.

The installation package is created using **Inno Setup**.

### Repository Structure
- `IppPrinter/`: Core IPP printer server code (ASP.NET Core / .NET 10).
- `Setup/`: Installation assets and script.
  - [installer.iss](Setup/installer.iss): The Inno Setup definition script.
  - [Publish-Installer.ps1](Setup/Publish-Installer.ps1): A PowerShell automation script to publish the application and compile the installer.
  - [Add-Printer.ps1](Setup/Add-Printer.ps1): Installs the print driver and registers the printer queue.
  - [Remove-Printer.ps1](Setup/Remove-Printer.ps1): Cleans up the print driver and queue.

### Building the Installer Locally

1. Open an **Administrator** PowerShell session.
2. Navigate to the `Setup` directory.
3. Run the build script:

   ```powershell
   cd Setup
   .\Publish-Installer.ps1
   ```
This script will publish a framework-dependent release target for `win-x64`, resolve your local Inno Setup compiler (`ISCC.exe`), compile the installer, and save the final executable in `Setup\Output\IppPrinterSetup.exe`.

### Installer Behavior

When run, the installer performs the following actions:

1. Copies compiled binaries to `C:\Program Files\IppPrinter`.
2. Registers `IppPrinter.exe` as a Windows Service set to automatic startup (`sc create`).
3. Creates a Windows Firewall rule to allow incoming TCP traffic on port `631`.
4. Starts the Windows Service (`sc start`).
5. Runs `Add-Printer.ps1` to configure the printer queue in Windows.

On uninstallation:

1. Runs `Remove-Printer.ps1` to delete the print queue.
2. Removes the Windows Firewall rule.
3. Stops and deletes the Windows Service.
4. Removes all installed files.

---

## 4. CI/CD Release Pipeline

We use GitHub Actions to automate versioning and releasing.

The workflow is defined in [.github/workflows/create-release.yml](.github/workflows/create-release.yml).

### How Versioning Works

When you push a tag starting with `v` (e.g. `v1.0.0`), the workflow triggers automatically:

1. It runs on a Windows runner (`windows-latest`).
2. It extracts the version number from the git tag (e.g., `1.0.0`).
3. It updates:
   - The `<Version>` elements inside all `*.csproj` files.
   - The `#define MyAppVersion` definition in [installer.iss](Setup/installer.iss).
4. It publishes the packages for `win-x64`, `linux-x64`, and `osx-x64`.
5. It compiles the Inno Setup installer.
6. It creates a GitHub Release and attaches the platform ZIP files and the `IppPrinter-Setup-x64.exe` installer file as assets.

---

## 5. Integration Testing

You can test the IPP server against standard IPP diagnostic tools such as `ipptool` (from CUPS).

### OpenSUSE / Linux (`ipptool`)

1. Install CUPS client tools:
   ```bash
   sudo zypper install cups-client cups-backends
   ```
2. Obtain the host IP address:
   ```bash
   ip route
   ```
3. Run the IPP test suite:
   ```bash
   ipptool -t ipp://<HOST_IP>:631/ipp/print ipp-1.1.test
   ```

