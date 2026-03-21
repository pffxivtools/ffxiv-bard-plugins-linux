param(
    [switch]$SkipUpstream
)

$ErrorActionPreference = 'Stop'

if (-not [System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([System.Runtime.InteropServices.OSPlatform]::Windows)) {
    Write-Host "Windows-native tests skipped: current host is not Windows."
    exit 0
}

$Root = Split-Path -Parent $PSScriptRoot
$ShimProject = Join-Path $Root 'XivIpc.WindowsTests/XivIpc.WindowsTests.csproj'
$UpstreamProject = Join-Path $Root 'TinyIpc/test/TinyIpc.Tests/TinyIpc.Tests.csproj'
$UpstreamRoot = Join-Path $Root 'TinyIpc'

Write-Host "Running Windows-native TinyIpc regression suite..."

if (-not $SkipUpstream) {
    Write-Host "1. Upstream TinyIpc Windows tests"
    Push-Location $UpstreamRoot
    try {
        dotnet test --project $UpstreamProject -f net9.0-windows -v minimal
    }
    finally {
        Pop-Location
    }
}

Write-Host "2. TinyIpc.Shim Windows smoke/regression tests"
dotnet test $ShimProject -f net9.0-windows -v minimal
