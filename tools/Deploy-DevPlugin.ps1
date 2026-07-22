[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$Target,

    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repository 'src\RQ\RQ.csproj'
$source = Join-Path $repository 'src\RQ\bin\Release'
$targetPath = [System.IO.Path]::GetFullPath($Target)

if (-not $SkipBuild) {
    & dotnet build $project -c Release
    if ($LASTEXITCODE -ne 0) {
        throw "Quartermaster Release build failed with exit code $LASTEXITCODE."
    }
}

$assembly = Join-Path $source 'RQ.dll'
$manifest = Join-Path $source 'RQ.json'
if (-not (Test-Path -LiteralPath $assembly) -or -not (Test-Path -LiteralPath $manifest)) {
    throw "Release output is incomplete at '$source'. Build first or omit -SkipBuild."
}

$targetParent = Split-Path -Parent $targetPath
if (-not (Test-Path -LiteralPath $targetParent)) {
    throw "Deployment target parent does not exist: '$targetParent'."
}
if (-not (Test-Path -LiteralPath $targetPath)) {
    New-Item -ItemType Directory -Path $targetPath | Out-Null
}

Get-ChildItem -LiteralPath $source -File | ForEach-Object {
    Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $targetPath $_.Name) -Force
}

"Deployed Quartermaster to '$targetPath'."
