param (
    [string]$Scheme = "http"
)

$printerName = "IppPrinter"
$ipAddress = "localhost"
$portNumber = 631
$printerUrl = "$Scheme`://$ipAddress`:$portNumber/ipp/print"

function Assert-Administrator {
    $isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    if (-not $isAdmin) {
        Write-Error "This script must be run as Administrator. Please open PowerShell as Administrator and run the script again."
        exit 1
    }
}

function Import-Modules {
    if ($PSVersionTable.PSVersion.Major -ge 7) {
        Write-Host "Detected PowerShell 7. Importing DISM module..."
        try {
            Import-Module DISM -UseWindowsPowerShell -ErrorAction Stop -WarningAction SilentlyContinue
            Write-Host "DISM module imported successfully."
        } catch {
            Write-Error "Failed to import DISM module. Ensure PowerShell 5.1 is available."
            exit 1
        }
    }
}

function Install-WindowsFeature {
    try {
        Write-Host "Checking Internet Print Client feature status..."
        $features = @("Printing-Foundation-InternetPrinting-Client", "Printing-InternetPrinting-Client")
        foreach ($featureName in $features) {
            $feature = Get-WindowsOptionalFeature -Online -FeatureName $featureName -ErrorAction SilentlyContinue
            if (-not $feature) {
                continue
            }

            if ($feature.State -eq "Enabled") {
                Write-Host "$featureName is already enabled."
                return $false
            }

            Write-Host "Enabling $featureName..."
            Enable-WindowsOptionalFeature -Online -FeatureName $featureName -NoRestart -ErrorAction Stop
            Write-Host "$featureName has been installed successfully."
            return $true
        }
        Write-Error "Required features not found. Ensure this is a supported Windows version."
        exit 1
    } catch {
        Write-Error "Error enabling Internet Print Client feature: $_"
        exit 1
    }
}

function Restart-Spooler {
    try {
        Write-Host "Restarting Spooler service..."
        Restart-Service -Name 'Spooler' -Force -ErrorAction Stop
        Start-Sleep -Seconds 3
        Write-Host "Spooler service restarted successfully."
    } catch {
        Write-Error "Failed to restart Spooler service: $_"
        exit 1
    }
}

function Add-IppPrinter {
    $existingPrinter = Get-Printer -Name $printerName -ErrorAction SilentlyContinue
    if ($existingPrinter) {
        $existingPort = $existingPrinter.PortName
        $existingUrlProp = $null
        try {
            $prop = Get-PrinterProperty -PrinterName $printerName -Name "PrinterURL" -ErrorAction SilentlyContinue
            if ($prop) { $existingUrlProp = $prop.Value }
        } catch { }

        if ($existingPort -eq $printerUrl -or $existingUrlProp -eq $printerUrl) {
            Write-Host "Printer '$printerName' already exists with matching URL ($printerUrl)."
            return
        }

        Write-Host "Printer '$printerName' exists but URL has changed (Current: '$existingPort', Target: '$printerUrl'). Re-creating printer queue..."
        try {
            Remove-Printer -Name $printerName -ErrorAction Stop
            Write-Host "Removed old printer '$printerName'."
        } catch {
            Write-Warning "Could not remove old printer '$printerName': $_"
        }
    }

    try {
        Write-Host "Waiting for IppPrinter service to become available on port $portNumber..."
        $maxWaitSeconds = 30
        $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
        $isListening = $false

        while ($stopwatch.Elapsed.TotalSeconds -lt $maxWaitSeconds) {
            try {
                $tcp = New-Object System.Net.Sockets.TcpClient
                $asyncResult = $tcp.BeginConnect($ipAddress, $portNumber, $null, $null)
                $success = $asyncResult.AsyncWaitHandle.WaitOne(500, $false)
                if ($success -and $tcp.Connected) {
                    $isListening = $true
                    $tcp.Close()
                    break
                }
                $tcp.Close()
            } catch { }
            Start-Sleep -Milliseconds 500
        }

        if ($isListening) {
            Write-Host "Port $portNumber is listening."
        }

        Write-Host "Adding printer: $printerName with URL $printerUrl..."
        Add-Printer -Name $printerName -IppURL $printerUrl -ErrorAction Stop
        Write-Host "Printer '$printerName' added successfully."
    } catch {
        Write-Error "Error adding printer '$printerName': $_"
        exit 1
    }
}

# Execute steps
Assert-Administrator
Import-Modules
$featureInstalled = [bool](Install-WindowsFeature)
if ($featureInstalled) {
    Restart-Spooler
}
Add-IppPrinter

Write-Host "Printer setup completed successfully."