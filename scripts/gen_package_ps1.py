from pathlib import Path
p = Path(__file__).resolve().parent.parent / "Package.ps1"
p.write_text(r'''# LinuxDo MSIX packager - signed sideload package
# Usage: .\Package.ps1   |   .\Package.ps1 -InstallCert   |   .\Package.ps1 -Open

[CmdletBinding()]
param(
    [ValidateSet('x64', 'ARM64', 'x86')]
    [string]$Platform = $(if ($env:PROCESSOR_ARCHITECTURE -eq 'ARM64') { 'ARM64' } else { 'x64' }),
    [string]$Configuration = 'Release',
    [string]$Project = '',
    [string]$OutputDir = '',
    [string]$CertPath = '',
    [string]$CertPassword = 'password',
    [string]$Publisher = 'CN=LinuxDo',
    [switch]$InstallCert,
    [switch]$SkipBuild,
    [switch]$Open,
    [switch]$Quiet
)

$ErrorActionPreference = 'Stop'
function Write-Step([string]$msg) { if (-not $Quiet) { Write-Host "`n==> $msg" -ForegroundColor Cyan } }
function Write-Ok([string]$msg)   { if (-not $Quiet) { Write-Host "  OK  $msg" -ForegroundColor Green } }
function Write-Warn([string]$msg) { Write-Host "  !   $msg" -ForegroundColor Yellow }
function Write-Fail([string]$msg) { Write-Host "  X   $msg" -ForegroundColor Red }

$Root = $PSScriptRoot
if (-not $Root) { $Root = (Get-Location).Path }
Set-Location $Root

if (-not $Project) {
    if (Test-Path (Join-Path $Root 'LinuxDo.csproj')) { $Project = Join-Path $Root 'LinuxDo.csproj' }
    else {
        $cs = Get-ChildItem $Root -Filter '*.csproj' -File
        if ($cs.Count -eq 1) { $Project = $cs[0].FullName } else { Write-Fail 'No .csproj found'; exit 1 }
    }
}

$ProjectName = [IO.Path]::GetFileNameWithoutExtension($Project)
$Manifest = Join-Path $Root 'Package.appxmanifest'
if (-not (Test-Path $Manifest)) { Write-Fail "Missing $Manifest"; exit 1 }

if (-not $OutputDir) { $OutputDir = Join-Path $Root 'dist' }
$CertDir = Join-Path $Root 'certs'
if (-not $CertPath) { $CertPath = Join-Path $CertDir 'devcert.pfx' }

$buildId = Get-Date -Format 'yyyyMMdd-HHmmss'
$rid = "win-$($Platform.ToLowerInvariant())"

Write-Step 'Checking tools'
$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if (-not $dotnet) { Write-Fail 'dotnet not found'; exit 1 }
Write-Ok $dotnet.Source
$winapp = Get-Command winapp -ErrorAction SilentlyContinue
if (-not $winapp) { Write-Fail 'winapp not found. winget install Microsoft.WinAppCLI'; exit 1 }
Write-Ok $winapp.Source
Write-Ok "BuildId: $buildId  Publisher: $Publisher"

$devMode = $false
try {
    $reg = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock'
    if (Test-Path $reg) {
        $v = Get-ItemProperty $reg -Name AllowDevelopmentWithoutDevLicense -ErrorAction SilentlyContinue
        if ($v.AllowDevelopmentWithoutDevLicense -eq 1) { $devMode = $true }
    }
} catch {}
if ($devMode) { Write-Ok 'Developer Mode: ON' } else { Write-Warn 'Developer Mode OFF - enable for sideloading' }

Write-Step 'Stamping BuildId'
$writeBuildInfo = Join-Path $Root 'scripts\Write-BuildInfo.ps1'
$buildInfoFile = Join-Path $Root 'Core\Utilities\BuildInfo.g.cs'
if (Test-Path $writeBuildInfo) {
    & powershell -NoProfile -ExecutionPolicy Bypass -File $writeBuildInfo -OutFile $buildInfoFile -BuildId $buildId
}
Write-Ok "BuildId = $buildId"

try {
    [xml]$mf = Get-Content $Manifest -Raw
    $ns = New-Object System.Xml.XmlNamespaceManager($mf.NameTable)
    $ns.AddNamespace('m', 'http://schemas.microsoft.com/appx/manifest/foundation/windows10')
    $id = $mf.SelectSingleNode('//m:Identity', $ns)
    if ($id -and $id.GetAttribute('Publisher') -ne $Publisher) {
        Write-Warn "Updating manifest Publisher to $Publisher"
        $id.SetAttribute('Publisher', $Publisher)
        $props = $mf.SelectSingleNode('//m:Properties/m:PublisherDisplayName', $ns)
        if ($props) { $props.InnerText = ($Publisher -replace '^CN=', '') }
        $utf8 = New-Object System.Text.UTF8Encoding $false
        [System.IO.File]::WriteAllText($Manifest, $mf.OuterXml, $utf8)
    }
} catch { Write-Warn "manifest publisher: $($_.Exception.Message)" }

function Find-BuildOutput([string]$platform, [string]$config) {
    $candidates = @(
        (Join-Path $Root "bin\$platform\$config\net10.0-windows10.0.26100.0\win-$($platform.ToLowerInvariant())"),
        (Join-Path $Root "bin\$platform\$config\net10.0-windows10.0.19041.0\win-$($platform.ToLowerInvariant())")
    )
    foreach ($c in $candidates) {
        if (-not (Test-Path $c)) { continue }
        $exe = Get-ChildItem $c -Filter "$ProjectName.exe" -Recurse -EA SilentlyContinue | Select-Object -First 1
        if ($exe) { return $exe.Directory.FullName }
    }
    return $null
}

if (-not $SkipBuild) {
    Write-Step "Building $ProjectName ($Configuration | $Platform)"
    & dotnet build $Project -c $Configuration -p:Platform=$Platform -p:RuntimeIdentifier=$rid -p:LinuxDoPackageBuildId=$buildId -p:WindowsAppSDKSelfContained=true --nologo
    if ($LASTEXITCODE -ne 0) { Write-Fail "build failed ($LASTEXITCODE)"; exit $LASTEXITCODE }
    Write-Ok 'Build succeeded'
}

$buildOutput = Find-BuildOutput $Platform $Configuration
if (-not $buildOutput) { Write-Fail 'Build output folder not found'; exit 1 }
Write-Ok "Output: $buildOutput"

$icon = Join-Path $buildOutput 'Assets\AppIcon.ico'
if (-not (Test-Path $icon)) {
    Write-Warn 'Copying Assets into build output'
    $dst = Join-Path $buildOutput 'Assets'
    New-Item -ItemType Directory -Force -Path $dst | Out-Null
    Copy-Item (Join-Path $Root 'Assets\*') $dst -Recurse -Force
}
if (Test-Path $icon) { Write-Ok 'Assets/AppIcon.ico present' } else { Write-Warn 'AppIcon.ico still missing' }

Write-Step 'Signing certificate'
New-Item -ItemType Directory -Force -Path $CertDir | Out-Null
$cerPath = [IO.Path]::ChangeExtension($CertPath, '.cer')

if (-not (Test-Path $CertPath)) {
    Write-Host "  Generating $CertPath (password: $CertPassword)" -ForegroundColor DarkGray
    & winapp cert generate --manifest $Manifest --publisher $Publisher --output $CertPath --password $CertPassword --export-cer --if-exists overwrite
    if ($LASTEXITCODE -ne 0) { Write-Fail "cert generate failed ($LASTEXITCODE)"; exit $LASTEXITCODE }
} else {
    Write-Ok "Using existing cert: $CertPath"
}

if ($InstallCert) {
    Write-Step 'Installing certificate (Admin required)'
    & winapp cert install $CertPath --password $CertPassword --force
    if ($LASTEXITCODE -ne 0) {
        Write-Warn "cert install failed ($LASTEXITCODE). Run elevated: winapp cert install `"$CertPath`" --password $CertPassword"
    } else { Write-Ok 'Certificate trusted' }
}

Write-Step 'Creating signed MSIX'
New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null
$msixName = "${ProjectName}_${buildId}_${Platform}.msix"
$msixPath = Join-Path $OutputDir $msixName

& winapp package $buildOutput --manifest $Manifest --output $msixPath --cert $CertPath --cert-password $CertPassword --self-contained --verbose
if ($LASTEXITCODE -ne 0) { Write-Fail "winapp package failed ($LASTEXITCODE)"; exit $LASTEXITCODE }

if (-not (Test-Path $msixPath)) {
    $found = Get-ChildItem $Root, $OutputDir, $buildOutput -Filter '*.msix' -EA SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if ($found) { Copy-Item $found.FullName (Join-Path $OutputDir $found.Name) -Force; $msixPath = Join-Path $OutputDir $found.Name }
}
if (-not (Test-Path $msixPath)) { Write-Fail 'MSIX not produced'; exit 1 }

$latestMsix = Join-Path $OutputDir "$ProjectName-latest.msix"
Copy-Item $msixPath $latestMsix -Force
Write-Ok "MSIX: $msixPath"

$distCer = Join-Path $OutputDir 'LinuxDo-Signing.cer'
if (Test-Path $cerPath) { Copy-Item $cerPath $distCer -Force; Write-Ok "CER: $distCer" }
elseif (T
