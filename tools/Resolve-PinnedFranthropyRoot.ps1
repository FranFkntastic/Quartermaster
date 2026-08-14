function Resolve-PinnedFranthropyRoot {
    param(
        [Parameter(Mandatory = $true)]
        [string]$QuartermasterRepoRoot
    )

    $refPath = Join-Path -Path $QuartermasterRepoRoot -ChildPath 'eng\franthropy.ref'
    if (-not (Test-Path -LiteralPath $refPath)) {
        throw "Pinned Franthropy revision was not found: $refPath"
    }

    $expectedRevision = (Get-Content -LiteralPath $refPath -Raw).Trim()
    if ($expectedRevision -notmatch '^[0-9a-fA-F]{40}$') {
        throw "Pinned Franthropy revision is invalid: '$expectedRevision'."
    }

    $repository = [System.IO.Path]::GetFullPath((Join-Path -Path $QuartermasterRepoRoot -ChildPath '..\Franthropy'))
    if (-not (Test-Path -LiteralPath (Join-Path -Path $repository -ChildPath '.git'))) {
        throw "Franthropy was not found beside Quartermaster at '$repository'."
    }

    $worktreeLines = @(& git -C $repository worktree list --porcelain)
    if ($LASTEXITCODE -ne 0) {
        throw 'Could not enumerate Franthropy worktrees.'
    }

    $candidates = New-Object System.Collections.Generic.List[string]
    $currentPath = $null
    $currentHead = $null
    foreach ($line in @($worktreeLines + '')) {
        if ([string]::IsNullOrWhiteSpace($line)) {
            if (-not [string]::IsNullOrWhiteSpace($currentPath) -and $currentHead -eq $expectedRevision) {
                $candidates.Add([System.IO.Path]::GetFullPath($currentPath))
            }
            $currentPath = $null
            $currentHead = $null
            continue
        }
        if ($line.StartsWith('worktree ')) { $currentPath = $line.Substring('worktree '.Length) }
        elseif ($line.StartsWith('HEAD ')) { $currentHead = $line.Substring('HEAD '.Length) }
    }

    foreach ($candidate in $candidates) {
        $dirty = @(& git -C $candidate status --porcelain --untracked-files=normal)
        if ($LASTEXITCODE -eq 0 -and $dirty.Count -eq 0) {
            Write-Host "Using pinned Franthropy worktree: $candidate@$expectedRevision"
            return $candidate
        }
    }

    throw "No clean Franthropy worktree is checked out at pinned revision '$expectedRevision'."
}
