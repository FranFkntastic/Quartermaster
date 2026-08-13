[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$Target,

    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [string]$FranthropyRoot,

    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repository 'src\RQ\RQ.csproj'
$source = Join-Path $repository "src\RQ\bin\$Configuration"
$targetPath = [System.IO.Path]::GetFullPath($Target)

if (-not $SkipBuild) {
    $arguments = @('build', $project, '-c', $Configuration)
    if (-not [string]::IsNullOrWhiteSpace($FranthropyRoot)) {
        $resolvedFranthropy = [System.IO.Path]::GetFullPath($FranthropyRoot)
        $arguments += @(
            "-p:FranthropyDalamudProject=$(Join-Path $resolvedFranthropy 'src\Franthropy.Dalamud\Franthropy.Dalamud.csproj')",
            "-p:FranthropyFfxivProject=$(Join-Path $resolvedFranthropy 'src\Franthropy.FFXIV\Franthropy.FFXIV.csproj')",
            "-p:FranthropyObservationsProject=$(Join-Path $resolvedFranthropy 'src\Franthropy.Observations\Franthropy.Observations.csproj')"
        )
    }
    & dotnet @arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Quartermaster $Configuration build failed with exit code $LASTEXITCODE."
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

$files = Get-ChildItem -LiteralPath $source -File
$files | Where-Object Name -NE 'RQ.dll' | ForEach-Object {
    Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $targetPath $_.Name) -Force
}
Copy-Item -LiteralPath $assembly -Destination (Join-Path $targetPath 'RQ.dll') -Force

$targetDll = Join-Path $targetPath 'RQ.dll'
$sourceHash = (Get-FileHash -LiteralPath $assembly -Algorithm SHA256).Hash
$targetHash = (Get-FileHash -LiteralPath $targetDll -Algorithm SHA256).Hash
if ($sourceHash -ne $targetHash) {
    throw 'Quartermaster target hash does not match the built artifact.'
}

[pscustomobject]@{
    Product = 'Quartermaster'
    Branch = (& git -C $repository branch --show-current).Trim()
    Commit = (& git -C $repository rev-parse HEAD).Trim()
    TargetDll = $targetDll
    SourceSha256 = $sourceHash
    TargetSha256 = $targetHash
} | ConvertTo-Json -Depth 4
