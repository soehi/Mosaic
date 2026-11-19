param(
    [string]$ProgramVersion,
    [string]$SourcePath = (Join-Path $PSScriptRoot 'release-content.json'),
    [string]$IndexPath = (Join-Path $PSScriptRoot 'published-release-index.json'),
    [string]$ManifestPath = (Join-Path $PSScriptRoot '../artifacts/release/release-manifest.json'),
    [string]$NotesPath,
    [string]$CheckManifestPath,
    [switch]$CheckOnly
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Get-ReleaseFingerprint($Release) {
    $lines = [System.Collections.Generic.List[string]]::new()
    foreach ($value in @($Release.releaseId, $Release.version, $Release.releasedOn, $Release.previousReleaseId)) {
        $lines.Add([string]$value)
    }
    foreach ($category in @('added', 'improved', 'fixed')) {
        $lines.Add($category)
        $items = @($Release.$category)
        $lines.Add($items.Count.ToString([Globalization.CultureInfo]::InvariantCulture))
        foreach ($item in $items) { $lines.Add([string]$item) }
    }
    return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData(
        [Text.Encoding]::UTF8.GetBytes([string]::Join("`n", $lines))))
}

function Get-SemanticVersion([string]$Text) {
    if ($Text -notmatch '^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?$') {
        throw "Invalid semantic release version: $Text"
    }
    $parsed = [System.Management.Automation.SemanticVersion]::Parse($Text)
    foreach ($part in @($parsed.PreReleaseLabel -split '\.')) {
        if ($part -match '^\d+$' -and $part.Length -gt 1 -and $part.StartsWith('0', [StringComparison]::Ordinal)) {
            throw "Numeric prerelease identifiers cannot have leading zeroes: $Text"
        }
    }
    return $parsed
}

function Get-SummaryText($Current, [object[]]$Applicable, [string]$PreviousVersion) {
    $lines = [System.Collections.Generic.List[string]]::new()
    $lines.Add("Mosaic $($Current.version) · 更新日期 $($Current.releasedOn)")
    $lines.Add("适用范围：$PreviousVersion → $($Current.version)")
    foreach ($category in @(
        @('added', '新增功能'), @('improved', '功能改进'), @('fixed', '问题修复与兼容变化'))) {
        $items = [System.Collections.Generic.List[string]]::new()
        foreach ($release in $Applicable) {
            foreach ($item in @($release.($category[0]))) {
                $items.Add($(if ($Applicable.Count -gt 1) { "[$($release.version)] $item" } else { [string]$item }))
            }
        }
        if ($items.Count -eq 0) { continue }
        $lines.Add('')
        $lines.Add($category[1])
        for ($index = 0; $index -lt $items.Count; $index++) { $lines.Add("$($index + 1). $($items[$index])") }
    }
    return [string]::Join("`n", $lines)
}

function Get-RuneCount([string]$Text) {
    $count = 0
    for ($index = 0; $index -lt $Text.Length; $index++) {
        if ([char]::IsHighSurrogate($Text[$index]) -and $index + 1 -lt $Text.Length -and
            [char]::IsLowSurrogate($Text[$index + 1])) { $index++ }
        $count++
    }
    return $count
}

function Write-Utf8File([string]$Path, [string]$Text) {
    $absolute = [IO.Path]::GetFullPath($Path)
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($absolute)) | Out-Null
    [IO.File]::WriteAllText($absolute, $Text, [Text.UTF8Encoding]::new($false))
}

if (-not $ProgramVersion) {
    [xml]$properties = Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot '../Directory.Build.props')
    $ProgramVersion = [string]$properties.Project.PropertyGroup.Version
}
$program = Get-SemanticVersion (($ProgramVersion -split '\+')[0])
$source = Get-Content -Raw -LiteralPath $SourcePath | ConvertFrom-Json
if ($source.schemaVersion -ne 1 -or @($source.releases).Count -eq 0) { throw 'Release source is empty or has an unsupported schema.' }
$byId = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::Ordinal)
$versions = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($release in @($source.releases)) {
    if ([string]::IsNullOrWhiteSpace($release.releaseId) -or $release.releaseId.Length -gt 160 -or
        -not $byId.TryAdd($release.releaseId, $release) -or -not $versions.Add($release.version)) {
        throw 'Release IDs and versions must be present and unique.'
    }
    $null = Get-SemanticVersion $release.version
    $date = [DateOnly]::MinValue
    if (-not [DateOnly]::TryParseExact($release.releasedOn, 'yyyy-MM-dd',
            [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::None, [ref]$date)) {
        throw "Invalid release date: $($release.releaseId)"
    }
    foreach ($category in @('added', 'improved', 'fixed')) {
        if ($null -eq $release.$category -or @($release.$category | Where-Object { [string]::IsNullOrWhiteSpace($_) }).Count -gt 0) {
            throw "Invalid release category $category in $($release.releaseId)"
        }
    }
    if ($release.contentSha256 -cne (Get-ReleaseFingerprint $release)) {
        throw "Release content fingerprint changed for $($release.releaseId). Increase the release version instead of replacing published content."
    }
}
foreach ($release in @($source.releases)) {
    if ($null -eq $release.previousReleaseId) { continue }
    if (-not $byId.ContainsKey($release.previousReleaseId) -or
        (Get-SemanticVersion $byId[$release.previousReleaseId].version).CompareTo((Get-SemanticVersion $release.version)) -ge 0) {
        throw "Missing or non-increasing predecessor for $($release.releaseId)"
    }
}
if (-not $byId.ContainsKey($source.currentReleaseId)) { throw 'Current release is absent from the source.' }
$current = $byId[$source.currentReleaseId]
if ((Get-SemanticVersion $current.version).CompareTo($program) -ne 0) { throw 'Release source and program version do not match.' }

$indexEntries = @(Get-Content -Raw -LiteralPath $IndexPath | ConvertFrom-Json)
$indexIds = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($entry in $indexEntries) {
    if (-not $indexIds.Add($entry.releaseId) -or -not $byId.ContainsKey($entry.releaseId)) {
        throw "Published release index entry was removed or duplicated: $($entry.releaseId)"
    }
    $release = $byId[$entry.releaseId]
    if ($entry.version -cne $release.version -or $entry.contentSha256 -cne $release.contentSha256) {
        throw "Published release identity or content changed: $($entry.releaseId)"
    }
}
if (-not $indexIds.Contains($source.currentReleaseId)) { throw 'Current release must be frozen in the release index before publishing.' }

# Once an index has been committed, additions are allowed but existing identities and hashes are immutable.
$repoRoot = Split-Path $PSScriptRoot -Parent
$relativeIndex = [IO.Path]::GetRelativePath($repoRoot, [IO.Path]::GetFullPath($IndexPath)).Replace('\', '/')
$gitPath = "HEAD:$relativeIndex"
git -C $repoRoot cat-file -e $gitPath 2>$null
if ($LASTEXITCODE -eq 0) {
    $committedIndex = @(git -C $repoRoot show $gitPath | ConvertFrom-Json)
    foreach ($entry in $committedIndex) {
        $candidate = @($indexEntries | Where-Object { $_.releaseId -ceq $entry.releaseId })
        if ($candidate.Count -ne 1 -or $candidate[0].version -cne $entry.version -or $candidate[0].contentSha256 -cne $entry.contentSha256) {
            throw "Previously committed release index entry cannot be changed: $($entry.releaseId)"
        }
    }
}
$global:LASTEXITCODE = 0

$chain = [Collections.Generic.List[object]]::new()
$visited = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
$next = $current
while ($null -ne $next) {
    if (-not $visited.Add($next.releaseId)) { throw 'Release history contains a cycle.' }
    if (@($next.added).Count + @($next.improved).Count + @($next.fixed).Count -eq 0) {
        throw "Required release history has no content: $($next.releaseId)"
    }
    $chain.Insert(0, $next)
    $next = if ($null -ne $next.previousReleaseId) { $byId[$next.previousReleaseId] } else { $null }
}
for ($from = 0; $from -lt $chain.Count; $from++) {
    $applicable = @($chain | Select-Object -Skip $from)
    $text = Get-SummaryText $current $applicable $(if ($from -eq 0) { '首次使用' } else { $chain[$from - 1].version })
    if ((Get-RuneCount '更新简要') -gt 200 -or (Get-RuneCount $text) -gt 20000) {
        throw 'A supported update range exceeds the default task title or notes limit.'
    }
}
$json = ($source | ConvertTo-Json -Depth 30) + "`n"
if ($CheckManifestPath) {
    $packaged = Get-Content -Raw -LiteralPath $CheckManifestPath | ConvertFrom-Json
    if (($packaged | ConvertTo-Json -Depth 30 -Compress) -cne ($source | ConvertTo-Json -Depth 30 -Compress)) {
        throw 'Packaged release manifest differs from the verified release source.'
    }
}
if (-not $CheckOnly) {
    Write-Utf8File $ManifestPath $json
    if ($NotesPath) {
        $notes = "# Mosaic $($current.version)`n`n" + (Get-SummaryText $current @($current) '首次使用') + "`n"
        Write-Utf8File $NotesPath $notes
    }
}
Write-Host "Release content verified: $($current.version), $($source.currentReleaseId), $($byId.Count) indexed releases."
