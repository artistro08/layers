<#
    Builds the Layers release packages.

    Outputs:  dist\Layers-<version>.msi (per-user, unpackaged, Native AOT)
              dist\Layers-<version>.msix (packaged, Native AOT)
    Used for: producing signed GitHub release assets by hand. There's no CI.
    Needs:    .NET SDK 10 (https://dotnet.microsoft.com/download)
              Visual Studio C++ build tools, found through vswhere, for the Native AOT link step
              Windows SDK signtool (https://learn.microsoft.com/windows/win32/seccrypto/signtool)
    Signing:  LAYERS_SIGN_CERT      path to a .pfx, or a certificate thumbprint in your store
              LAYERS_SIGN_PASSWORD  .pfx password (optional)
              LAYERS_TIMESTAMP_URL  RFC 3161 timestamp server (default http://timestamp.digicert.com)
              When LAYERS_SIGN_CERT is unset, packages are built unsigned with a warning.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

# Paths
$root     = $PSScriptRoot
$dist     = Join-Path $root "dist"
$publish  = Join-Path $root "artifacts\publish"
$msixOut  = Join-Path $root "artifacts\msix"
$app      = Join-Path $root "src\Layers\Layers.csproj"
$version  = ([xml](Get-Content (Join-Path $root "Directory.Build.props"))).SelectSingleNode("//Version").InnerText

# Stop On Tool Failure
function Invoke-Step([string]$Name, [scriptblock]$Command) {
    Write-Host "==> $Name" -ForegroundColor Cyan
    & $Command
    if ($LASTEXITCODE -ne 0) { throw "$Name failed with exit code $LASTEXITCODE" }
}

# Sign One File, Or Warn When No Certificate Is Configured
function Invoke-Sign([string]$Path) {
    if (-not $env:LAYERS_SIGN_CERT) {
        Write-Warning "LAYERS_SIGN_CERT is not set; $Path is unsigned"
        return
    }

    $signtool  = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\signtool.exe" -ErrorAction SilentlyContinue |
        Sort-Object FullName -Descending | Select-Object -First 1 -ExpandProperty FullName
    if (-not $signtool) {
        throw "signtool.exe not found under ${env:ProgramFiles(x86)}\Windows Kits\10\bin\<version>\x64. Install the Windows SDK signing tools, or unset LAYERS_SIGN_CERT to build unsigned."
    }

    $timestamp = if ($env:LAYERS_TIMESTAMP_URL) { $env:LAYERS_TIMESTAMP_URL } else { "http://timestamp.digicert.com" }
    $signArgs  = @("sign", "/fd", "sha256", "/tr", $timestamp, "/td", "sha256")

    if (Test-Path $env:LAYERS_SIGN_CERT) {
        $signArgs += @("/f", $env:LAYERS_SIGN_CERT)
        if ($env:LAYERS_SIGN_PASSWORD) { $signArgs += @("/p", $env:LAYERS_SIGN_PASSWORD) }
    }
    else {
        $signArgs += @("/sha1", $env:LAYERS_SIGN_CERT)
    }

    Invoke-Step "Sign $(Split-Path $Path -Leaf)" { & $signtool @signArgs $Path }
}

# Put vswhere On PATH So The Native AOT Link Step Can Find link.exe
if (-not (Get-Command vswhere.exe -ErrorAction SilentlyContinue)) {
    $env:PATH = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer;$env:PATH"
}

# 1. Tests
Invoke-Step "Test" { dotnet test --project (Join-Path $root "tests\Layers.Tests") -c Release --filter "TestCategory!=Hardware&TestCategory!=Packaging" }

# 1b. UI Tests, In Their Own Step
# A crash after every test passed (the WinUI host exiting) only warns; anything short of all discovered tests passing fails
$uiTests = Join-Path $root "tests\Layers.UITests"
$listed  = dotnet test --project $uiTests -c Release --list-tests 2>&1 | Out-String
if ($LASTEXITCODE -ne 0) { throw "UI test discovery failed with exit code $LASTEXITCODE`n$listed" }
$expected = [int][regex]::Match($listed, 'Discovered (\d+) tests\.').Groups[1].Value

Write-Host "==> UI Test" -ForegroundColor Cyan
dotnet test --project $uiTests -c Release --no-build 2>&1 | Tee-Object -Variable uiLog | Out-Host
$uiExit = $LASTEXITCODE
if ($uiExit -ne 0) {
    $summary   = $uiLog | Out-String
    $count     = { param($name) [int][regex]::Match($summary, "(?m)^\s*${name}: (\d+)").Groups[1].Value }
    $allPassed = $expected -gt 0 -and (& $count "failed") -eq 0 -and (& $count "total") -eq $expected -and (& $count "succeeded") -eq $expected
    if (-not $allPassed) { throw "UI Test failed with exit code $uiExit" }
    Write-Warning "UI Test: all $expected tests passed, but the test host exited with code $uiExit afterward"
}

# 2. MSI (clean publish first, then a clean dist, so nothing stale gets packed)
Remove-Item $publish -Recurse -Force -ErrorAction SilentlyContinue
Invoke-Step "Publish unpackaged" { dotnet publish $app -c Release -r win-x64 -p:PublishAot=true -o $publish }
Invoke-Sign (Join-Path $publish "Layers.exe")

Remove-Item $dist -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $dist | Out-Null
Invoke-Step "Build MSI" { dotnet build (Join-Path $root "src\Layers.Installer\Layers.Installer.wixproj") -c Release "-p:PublishDir=$publish\" -o $dist }
Invoke-Sign (Join-Path $dist "Layers-$version.msi")
Invoke-Step "Check MSI" { dotnet test --project (Join-Path $root "tests\Layers.Tests") -c Release --filter "TestCategory=Packaging" }

# 3. MSIX
Remove-Item $msixOut -Recurse -Force -ErrorAction SilentlyContinue
Invoke-Step "Build MSIX" {
    dotnet publish $app -c Release -r win-x64 `
        -p:WindowsPackageType=MSIX -p:PublishAot=true -p:GenerateAppxPackageOnBuild=true `
        "-p:AppxPackageDir=$msixOut\"
}

$msix = Get-ChildItem $msixOut -Recurse -Filter *.msix | Select-Object -First 1
if (-not $msix) { throw "Build MSIX produced no .msix under $msixOut" }

# Left Unsigned For Partner Center, Which Signs It With The Store Identity
# GitHub Releases Ship The Store-Signed Copy, Downloaded From Partner Center After Certification
Copy-Item $msix.FullName (Join-Path $dist "Layers-$version.msix")

Write-Host "Done: $dist" -ForegroundColor Green
