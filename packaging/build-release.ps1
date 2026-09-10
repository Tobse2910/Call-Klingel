<#
.SYNOPSIS
    Builds an installable Call Klingel release and, on request, publishes it to GitHub.

.DESCRIPTION
    Produces three things in packaging\releases:

      CallKlingel-win-Setup.exe   the installer you hand to a person
      *-full.nupkg                the payload the app downloads when updating
      RELEASES-win.json           the index the app reads to notice a new version

    All three belong together. Publishing only the setup file gives new users the current
    version and leaves everyone who already installed it stuck: without the index and the
    package, the app has nothing to compare against and nothing to fetch.

.PARAMETER Version
    Release version, for example 0.4.0. Must climb with every release - the updater
    compares version numbers, so a release that is not higher than the installed one is
    invisible to it.

.PARAMETER Publish
    Also creates the GitHub release and uploads the files. Requires "gh auth login".

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\packaging\build-release.ps1 -Version 0.4.0

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\packaging\build-release.ps1 -Version 0.4.0 -Publish
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version,

    [switch]$Publish
)

$ErrorActionPreference = 'Stop'

$root      = Split-Path $PSScriptRoot
$project   = Join-Path $root 'src\CallKlingel.App\CallKlingel.App.csproj'
$publishTo = Join-Path $PSScriptRoot 'publish'
$releases  = Join-Path $PSScriptRoot 'releases'
$icon      = Join-Path $root 'src\CallKlingel.App\Assets\app.ico'

# The repository the app checks for updates, read out of the source rather than repeated
# here. Two copies of this address would eventually disagree, and the failure is silent:
# releases land in one place while every installed copy looks in the other, so nobody ever
# sees an update and nothing reports an error.
$serviceFile = Join-Path $root 'src\CallKlingel.App\Services\VelopackUpdateService.cs'
$match = Select-String -Path $serviceFile -Pattern 'ReleaseUrl\s*=\s*"([^"]+)"'
if (-not $match) { throw "ReleaseUrl nicht in $serviceFile gefunden." }
$repoUrl = $match.Matches[0].Groups[1].Value
Write-Host "Updatequelle: $repoUrl" -ForegroundColor DarkGray

function Step($text) { Write-Host "`n=== $text ===" -ForegroundColor Cyan }

# --- Prerequisites ---------------------------------------------------------------
Step 'Werkzeuge prüfen'

if (-not (Get-Command vpk -ErrorAction SilentlyContinue)) {
    throw "vpk fehlt. Installieren mit:  dotnet tool install -g vpk"
}
vpk --version | Write-Host

if ($Publish) {
    if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
        throw "GitHub CLI fehlt. Von https://cli.github.com installieren."
    }
    gh auth status 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "Nicht bei GitHub angemeldet. Zuerst ausführen:  gh auth login"
    }
}

# --- Icons -----------------------------------------------------------------------
Step 'Icons erzeugen'
& (Join-Path $PSScriptRoot 'make-icon.ps1') | Write-Host

# --- Tests -----------------------------------------------------------------------
# A release that ships without running the tests is a release nobody checked.
Step 'Tests'
dotnet test (Join-Path $root 'CallKlingel.sln') -c Release --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "Tests fehlgeschlagen - kein Release." }

# --- Publish -------------------------------------------------------------------
Step "Anwendung veröffentlichen (Version $Version)"

if (Test-Path $publishTo) { Remove-Item $publishTo -Recurse -Force }

# Self-contained: the target PC needs no .NET installation. That is the difference
# between "double-click and it runs" and "download this runtime first".
dotnet publish $project `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=false `
    -p:Version=$Version `
    -o $publishTo
if ($LASTEXITCODE -ne 0) { throw "Publish fehlgeschlagen." }

# --- Package -------------------------------------------------------------------
Step 'Setup und Updatepaket bauen'

New-Item -ItemType Directory -Path $releases -Force | Out-Null

$vpkArgs = @(
    'pack',
    '--packId', 'CallKlingel',
    '--packVersion', $Version,
    '--packDir', $publishTo,
    '--mainExe', 'CallKlingel.App.exe',
    '--packTitle', 'Call Klingel',
    '--packAuthors', 'Tobse2910',
    '--outputDir', $releases
)

if (Test-Path $icon) { $vpkArgs += @('--icon', $icon) }

vpk @vpkArgs
if ($LASTEXITCODE -ne 0) { throw "vpk pack fehlgeschlagen." }

Step 'Ergebnis'
Get-ChildItem $releases -File |
    Sort-Object LastWriteTime -Descending |
    Select-Object Name, @{ N = 'Größe'; E = { '{0:N1} MB' -f ($_.Length / 1MB) } } |
    Format-Table -AutoSize |
    Out-String |
    Write-Host

# --- Publish to GitHub ---------------------------------------------------------
if (-not $Publish) {
    Write-Host "Fertig. Zum Veröffentlichen erneut mit -Publish aufrufen." -ForegroundColor Green
    Write-Host "Der Installer liegt hier: $releases" -ForegroundColor DarkGray
    return
}

Step "GitHub-Release v$Version"

$repo = ($repoUrl -replace '^https://github\.com/', '')

# Upload all three artefacts together. vpk's own upload command keeps the release index
# consistent, which a hand-made "gh release create" does not.
vpk upload github `
    --repoUrl $repoUrl `
    --publish `
    --releaseName "Call Klingel $Version" `
    --tag "v$Version" `
    --outputDir $releases
if ($LASTEXITCODE -ne 0) { throw "Upload fehlgeschlagen." }

Write-Host "`nVeröffentlicht: https://github.com/$repo/releases/tag/v$Version" -ForegroundColor Green
Write-Host "Installierte Kopien finden das Update beim nächsten Start." -ForegroundColor DarkGray
