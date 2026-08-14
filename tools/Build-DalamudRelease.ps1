[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string] $Configuration = "Release",

    [string] $PackageUrl = "",

    [string] $OutputDirectory = "",

    [switch] $SkipBuild
)

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
. (Join-Path $PSScriptRoot 'Resolve-PinnedFranthropyRoot.ps1')
$projectDir = Join-Path $repoRoot "src\RQ"
$projectPath = Join-Path $projectDir "RQ.csproj"
$pluginName = "RQ"

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $repoRoot "dist"
}

$outputDirectoryFull = [System.IO.Path]::GetFullPath($OutputDirectory)
$repoRootFull = [System.IO.Path]::GetFullPath($repoRoot)
if (-not $outputDirectoryFull.StartsWith($repoRootFull, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "OutputDirectory must be inside the repository: $repoRootFull"
}

$buildOutput = Join-Path $projectDir "bin\$Configuration"
$packageStaging = Join-Path $OutputDirectory "package"
$zipPath = Join-Path $OutputDirectory "latest.zip"
$repoJsonPath = Join-Path $OutputDirectory "repo.json"

if (-not $SkipBuild) {
    $franthropyRoot = Resolve-PinnedFranthropyRoot -QuartermasterRepoRoot $repoRoot
    dotnet build $projectPath -c $Configuration -p:UseSharedCompilation=false "-p:FranthropyRoot=$franthropyRoot"
    if ($LASTEXITCODE -ne 0) {
        exit $LASTEXITCODE
    }
}

$manifestPath = Join-Path $buildOutput "$pluginName.json"
if (-not (Test-Path -LiteralPath $manifestPath)) {
    throw "Expected manifest was not found: $manifestPath"
}

if (Test-Path -LiteralPath $OutputDirectory) {
    Remove-Item -LiteralPath $OutputDirectory -Recurse -Force
}
New-Item -ItemType Directory -Path $packageStaging | Out-Null

$packageFiles = @(
    "RQ.dll",
    "RQ.deps.json",
    "RQ.json",
    "ECommons.dll",
    "Franthropy.AgentBridge.dll",
    "Franthropy.Dalamud.dll",
    "Franthropy.FFXIV.dll",
    "Franthropy.Filtering.dll",
    "Franthropy.Observations.dll",
    "Microsoft.Data.Sqlite.dll",
    "SQLitePCLRaw.batteries_v2.dll",
    "SQLitePCLRaw.core.dll",
    "SQLitePCLRaw.provider.e_sqlite3.dll",
    "e_sqlite3.dll",
    "System.Security.Cryptography.ProtectedData.dll"
)

foreach ($fileName in $packageFiles) {
    $sourcePath = Join-Path $buildOutput $fileName
    if (-not (Test-Path -LiteralPath $sourcePath)) {
        throw "Expected package file was not found: $sourcePath"
    }

    Copy-Item -LiteralPath $sourcePath -Destination $packageStaging
}

Compress-Archive -Path (Join-Path $packageStaging "*") -DestinationPath $zipPath -Force

$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
$releaseTag = "v$($manifest.AssemblyVersion)"
if ([string]::IsNullOrWhiteSpace($PackageUrl)) {
    $PackageUrl = "https://github.com/FranFkntastic/Quartermaster/releases/download/$releaseTag/latest.zip"
}

$repoEntry = [ordered]@{
    Author = $manifest.Author
    Name = $manifest.Name
    InternalName = $manifest.InternalName
    AssemblyVersion = $manifest.AssemblyVersion
    TestingAssemblyVersion = $null
    Description = $manifest.Description
    ApplicableVersion = $manifest.ApplicableVersion
    RepoUrl = $manifest.RepoUrl
    DalamudApiLevel = $manifest.DalamudApiLevel
    Punchline = $manifest.Punchline
    Tags = $manifest.Tags
    CategoryTags = $manifest.CategoryTags
    IsHide = $false
    IsTestingExclusive = $false
    DownloadCount = 0
    DownloadLinkInstall = $PackageUrl
    DownloadLinkTesting = $PackageUrl
    DownloadLinkUpdate = $PackageUrl
    LastUpdate = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds().ToString()
}

$repoEntryJson = $repoEntry | ConvertTo-Json -Depth 8
$repoJson = "[$repoEntryJson]"
$utf8NoBom = [System.Text.UTF8Encoding]::new($false)
[System.IO.File]::WriteAllText($repoJsonPath, $repoJson, $utf8NoBom)

Write-Host "Built Dalamud package:"
Write-Host "  Zip:  $zipPath"
Write-Host "  Repo: $repoJsonPath"
Write-Host "  Package URL: $PackageUrl"
