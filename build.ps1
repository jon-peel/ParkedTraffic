# Builds the stripped-down single-file ParkedTraffic.exe (self-contained, trimmed, compressed).
#
#   .\build.ps1                 -> dist\ParkedTraffic.exe and dist\ParkedTraffic-win-x64.zip
#
# Needs the .NET 10 SDK and lib\SimConnect.dll (from the MSFS SDK; it is bundled into the exe).
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

if (-not (Test-Path 'lib\SimConnect.dll')) {
    throw 'lib\SimConnect.dll is missing. Copy it from the MSFS SDK (SimConnect SDK\lib\SimConnect.dll) into lib\.'
}

$dist = Join-Path $PSScriptRoot 'dist'
if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }

# Release config in ParkedTraffic.csproj: self-contained, single file, trimmed (partial), compressed, no PDB.
dotnet publish ParkedTraffic.csproj -c Release -o $dist
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }

# The app creates its config files on first run; don't ship anyone's local settings.
if (Test-Path "$dist\config") { Remove-Item "$dist\config" -Recurse -Force }

$exe = Join-Path $dist 'ParkedTraffic.exe'
Compress-Archive -Path $exe -DestinationPath (Join-Path $dist 'ParkedTraffic-win-x64.zip') -Force
'{0}  {1:N1} MB' -f $exe, ((Get-Item $exe).Length / 1MB)
