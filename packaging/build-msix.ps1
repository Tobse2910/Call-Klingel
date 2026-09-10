<#
.SYNOPSIS
    Baut, signiert und installiert Call Klingel als MSIX-Paket.

.DESCRIPTION
    Die App MUSS als MSIX ausgeliefert werden. Nur ein Paket kann die Restricted
    Capability "phoneCall" deklarieren, und ohne die liefert
    PhoneLineTransportDevice.RequestAccessAsync() ein DeniedBySystem.
    Nachgewiesen am 2026-09-10, siehe docs/WINDOWS_TELEPHONY.md.

    Der Microsoft Store ist nicht noetig. Ein selbstsigniertes Zertifikat im Speicher
    TrustedPeople genuegt fuer Sideloading.

.PARAMETER Install
    Paket nach dem Bauen auch installieren. Benoetigt Administratorrechte fuer den
    Zertifikatsimport.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File packaging\build-msix.ps1 -Install
#>
[CmdletBinding()]
param(
    [switch]$Install,
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'

$repo    = Split-Path $PSScriptRoot -Parent
$staging = Join-Path $PSScriptRoot 'app'
$payload = Join-Path $staging 'payload'
$msix    = Join-Path $staging 'CallKlingel.msix'
$pfx     = Join-Path $staging 'CallKlingel-Dev.pfx'
$subject = 'CN=CallKlingel-Dev'
$password = 'CallKlingelDev!2026'

function Find-SdkTool([string]$name) {
    $root = 'C:\Program Files (x86)\Windows Kits\10\bin'
    $tool = Get-ChildItem $root -Recurse -Filter $name -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -match '\\x64\\' } |
            Sort-Object FullName -Descending | Select-Object -First 1
    if (-not $tool) { throw "$name nicht gefunden. Windows SDK installieren." }
    return $tool.FullName
}

Write-Host '=== 1. Anwendung veroeffentlichen ===' -ForegroundColor Cyan
if (Test-Path $payload) { Remove-Item -LiteralPath $payload -Recurse -Force }
New-Item -ItemType Directory -Path $payload -Force | Out-Null

& dotnet publish (Join-Path $repo 'src\CallKlingel.App') `
    -c $Configuration -r win-x64 --self-contained false `
    -o $payload --nologo -v minimal
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish fehlgeschlagen.' }

Write-Host '=== 2. Manifest und Assets ===' -ForegroundColor Cyan
Copy-Item (Join-Path $PSScriptRoot 'AppxManifest.xml') (Join-Path $payload 'AppxManifest.xml') -Force

# Icons vom eigenen Zeichenskript erzeugen, damit Paket und Fenster dieselbe Marke tragen.
& (Join-Path $PSScriptRoot 'make-icon.ps1') -OutDir (Join-Path $payload 'Assets')

Write-Host '=== 3. Paket schnueren ===' -ForegroundColor Cyan
$makeappx = Find-SdkTool 'makeappx.exe'
if (Test-Path $msix) { Remove-Item -LiteralPath $msix -Force }
& $makeappx pack '/d' $payload '/p' $msix '/overwrite' | Out-Null
if (-not (Test-Path $msix)) { throw 'makeappx hat kein Paket erzeugt.' }
Write-Host "  $msix"

Write-Host '=== 4. Signieren ===' -ForegroundColor Cyan
$cert = Get-ChildItem Cert:\CurrentUser\My -ErrorAction SilentlyContinue |
        Where-Object { $_.Subject -eq $subject } | Select-Object -First 1
if (-not $cert) {
    $cert = New-SelfSignedCertificate -Type Custom -Subject $subject `
              -KeyUsage DigitalSignature -FriendlyName 'Call Klingel Testzertifikat' `
              -CertStoreLocation 'Cert:\CurrentUser\My' `
              -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3',
                               '2.5.29.19={text}Subject Type:End Entity') `
              -NotAfter (Get-Date).AddYears(2)
    Write-Host "  Zertifikat erstellt: $($cert.Thumbprint)"
} else {
    Write-Host "  Zertifikat vorhanden: $($cert.Thumbprint)"
}

$secure = ConvertTo-SecureString -String $password -Force -AsPlainText
Export-PfxCertificate -Cert $cert -FilePath $pfx -Password $secure -Force | Out-Null

$signtool = Find-SdkTool 'signtool.exe'
& $signtool sign '/fd' 'SHA256' '/a' '/f' $pfx '/p' $password $msix | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Signieren fehlgeschlagen.' }
Write-Host '  Signiert.'

if (-not $Install) {
    Write-Host ''
    Write-Host "Fertig. Zum Installieren erneut mit -Install ausfuehren." -ForegroundColor Green
    return
}

Write-Host '=== 5. Installieren ===' -ForegroundColor Cyan
$admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
         ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $admin) { throw 'Fuer -Install werden Administratorrechte benoetigt (Zertifikatsimport).' }

Import-PfxCertificate -FilePath $pfx -CertStoreLocation 'Cert:\LocalMachine\TrustedPeople' -Password $secure | Out-Null
Get-AppxPackage -Name 'CallKlingel' -ErrorAction SilentlyContinue |
    ForEach-Object { Remove-AppxPackage $_.PackageFullName -ErrorAction SilentlyContinue }
Add-AppxPackage -Path $msix

$pkg = Get-AppxPackage -Name 'CallKlingel'
Write-Host ''
Write-Host 'Installiert:' -ForegroundColor Green
Write-Host "  $($pkg.PackageFullName)"
Write-Host "  Start ueber: shell:appsFolder\$($pkg.PackageFamilyName)!App"
