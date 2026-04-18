$ErrorActionPreference = 'Stop'

$packageName = 'stylobot'
$version = $env:chocolateyPackageVersion

# Detect architecture
if ([Environment]::Is64BitOperatingSystem) {
    if ($env:PROCESSOR_ARCHITECTURE -eq 'ARM64') {
        $arch = 'win-arm64'
        $archiveType = 'zip'
    } else {
        $arch = 'win-x64'
        $archiveType = 'zip'
    }
} else {
    throw "StyloBot requires a 64-bit operating system"
}

$url = "https://github.com/scottgal/stylobot/releases/download/console-v$version/stylobot-$arch.zip"

$installDir = Join-Path $env:ChocolateyInstall "lib\$packageName\tools"

$packageArgs = @{
    packageName    = $packageName
    url            = $url
    unzipLocation  = $installDir
    checksum       = '' # Updated by CI
    checksumType   = 'sha256'
}

Install-ChocolateyZipPackage @packageArgs

# Create shim for the binary
$exePath = Join-Path $installDir 'stylobot.exe'
Install-BinFile -Name 'stylobot' -Path $exePath

Write-Host ""
Write-Host "StyloBot installed! Quick start:" -ForegroundColor Green
Write-Host "  stylobot 5080 http://localhost:3000"
Write-Host "  stylobot --help"
Write-Host ""
