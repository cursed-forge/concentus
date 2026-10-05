# CursedForge fork (branch cursedforge/no-alloc): proves the fork is bit-exact
# with the Concentus 2.2.2 NuGet package and allocates nothing per frame.
#
# Usage (Windows PowerShell 5.1 or pwsh), from any folder:
#   powershell -NoProfile -ExecutionPolicy Bypass -File CSharp/NoAllocParity/run-parity.ps1 [-Out <folder>]
#
# Pass lines:   PARITY_SAME net8.0, PARITY_SAME netstandard2.0, ALLOC_ZERO net8.0, ALLOC_ZERO netstandard2.0
# Exit code 0 only if all four pass.

param(
    [string]$Out = (Join-Path ([System.IO.Path]::GetTempPath()) "concentus-noalloc-parity")
)

$ErrorActionPreference = "Stop"
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
New-Item -ItemType Directory -Force $Out | Out-Null
$failed = $false

function Build([string]$project, [string]$outDir, [string[]]$extra) {
    & dotnet build $project -c Release --nologo -v q -o $outDir @extra | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "build failed: $project $extra" }
}

function Run([string]$dll, [string[]]$arguments) {
    $output = & dotnet $dll @arguments
    $code = $LASTEXITCODE
    $output | ForEach-Object { Write-Host "  $_" }
    return $code
}

Build (Join-Path $here "Reference\Reference.csproj") (Join-Path $Out "reference") @()
$refBin = Join-Path $Out "reference.bin"
if ((Run (Join-Path $Out "reference\Reference.dll") @("parity", $refBin)) -ne 0) { throw "reference run failed" }

foreach ($tfm in @("net8.0", "netstandard2.0")) {
    $dir = Join-Path $Out "fork-$tfm"
    Build (Join-Path $here "Fork\Fork.csproj") $dir @("-p:ConcentusTfm=$tfm")
    $forkBin = Join-Path $Out "fork-$tfm.bin"
    if ((Run (Join-Path $dir "Fork.dll") @("parity", $forkBin)) -ne 0) { throw "fork run failed ($tfm)" }

    $a = (Get-FileHash $refBin -Algorithm SHA256).Hash
    $b = (Get-FileHash $forkBin -Algorithm SHA256).Hash
    $same = $false
    if ($a -eq $b) {
        $same = [System.Linq.Enumerable]::SequenceEqual([System.IO.File]::ReadAllBytes($refBin), [System.IO.File]::ReadAllBytes($forkBin))
    }

    if ($same) {
        Write-Host "PARITY_SAME $tfm sha256=$a"
    } else {
        Write-Host "PARITY_DIFFERENT $tfm reference=$a fork=$b"
        $failed = $true
    }

    if ((Run (Join-Path $dir "Fork.dll") @("alloc")) -eq 0) {
        Write-Host "ALLOC_ZERO $tfm"
    } else {
        Write-Host "ALLOC_NONZERO $tfm"
        $failed = $true
    }
}

if ($failed) { exit 1 }
exit 0
