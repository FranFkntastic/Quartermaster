# Legacy helper retained for rollback callers; source selection is explicit.
function Resolve-PinnedFranthropyRoot {
    param([string]$QuartermasterRepoRoot, [Parameter(Mandatory)][string]$FranthropyRoot)
    $root = (Resolve-Path -LiteralPath $FranthropyRoot).Path
    if (-not (Test-Path (Join-Path $root 'src/Franthropy.Dalamud/Franthropy.Dalamud.csproj'))) {
        throw 'FranthropyRoot must contain the canonical source projects.'
    }
    return $root
}
