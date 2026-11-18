# Builds dist\DeviceBattery-Setup-<version>.exe (and a portable zip).
# Needs: .NET 8 SDK and Inno Setup 6 (winget install JRSoftware.InnoSetup).
param([string]$Version = "1.1.1")
$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root

$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
if (-not $dotnet) { $dotnet = "C:\Program Files\dotnet\dotnet.exe" }

# 1. Publish the self-contained single-file exe. Do NOT enable single-file compression: it makes the app
#    use ~45 MB of RAM instead of ~12 MB (the installer compresses the file anyway).
& $dotnet publish "$root\src\DeviceBattery.csproj" -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:Version=$Version -o "$root\dist\self-contained"
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed." }

# 2. Compile the installer.
$iscc = @("$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe", "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe") |
    Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { $iscc = (Get-Command ISCC.exe -ErrorAction SilentlyContinue).Source }
if (-not $iscc) { throw "Inno Setup not found (winget install JRSoftware.InnoSetup)." }
& $iscc "/DAppVersion=$Version" (Join-Path $PSScriptRoot "DeviceBattery.iss")
if ($LASTEXITCODE -ne 0) { throw "ISCC failed." }

# 3. Portable zip: the same exe, no installer.
$zip = "$root\dist\DeviceBattery-$Version-portable.zip"
Remove-Item $zip -ErrorAction SilentlyContinue
Compress-Archive -Path "$root\dist\self-contained\DeviceBattery.exe", "$root\README.md" -DestinationPath $zip
Write-Host "Done: dist\DeviceBattery-Setup-$Version.exe and dist\DeviceBattery-$Version-portable.zip"
