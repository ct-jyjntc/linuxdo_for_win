# LinuxDo MSIX packager - signed sideload package
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

Write-Step 'Stamping BuildId + package version'
$writeBuildInfo = Join-Path $Root 'scripts\Write-BuildInfo.ps1'
$buildInfoFile = Join-Path $Root 'Core\Utilities\BuildInfo.g.cs'
if (Test-Path $writeBuildInfo) {
    & powershell -NoProfile -ExecutionPolicy Bypass -File $writeBuildInfo -OutFile $buildInfoFile -BuildId $buildId
}
Write-Ok "BuildId = $buildId"

# MSIX Identity.Version is Major.Minor.Build.Revision (each 0-65535).
# Map yyyyMMdd-HHmmss -> yyyy.MMdd.HHmm.ss so every package is a strict upgrade
# over older 1.0.0.0 builds (Windows blocks same-version different-content installs).
function Convert-BuildIdToPackageVersion([string]$id) {
    if ($id -match '^(\d{4})(\d{2})(\d{2})-(\d{2})(\d{2})(\d{2})$') {
        $major = [int]$Matches[1]                 # year, e.g. 2026
        $minor = [int]($Matches[2] + $Matches[3]) # MMDD, e.g. 711
        $build = [int]($Matches[4] + $Matches[5]) # HHmm, e.g. 1058
        $rev   = [int]$Matches[6]                 # ss
        if ($major -gt 65535) { $major = 65535 }
        if ($minor -gt 65535) { $minor = 65535 }
        if ($build -gt 65535) { $build = 65535 }
        if ($rev   -gt 65535) { $rev   = 65535 }
        return "$major.$minor.$build.$rev"
    }
    # Fallback: tick-based unique version under 1.x
    $t = [DateTime]::UtcNow
    return ("1.{0}.{1}.{2}" -f $t.DayOfYear, ($t.Hour * 60 + $t.Minute), $t.Second)
}

$packageVersion = Convert-BuildIdToPackageVersion $buildId
Write-Ok "Package version = $packageVersion"

# Stamp a packaging-only manifest so source Package.appxmanifest stays a baseline.
# IMPORTANT: never regex-replace bare Version="..." — it also hits XML decl version="1.0"
# and MinVersion="..." attributes, which corrupts the manifest for winapp.
$packManifest = Join-Path $OutputDir ("Package.packaging.$buildId.appxmanifest")
New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null
try {
    if (-not (Test-Path $Manifest)) { throw "Missing $Manifest" }
    $utf8 = New-Object System.Text.UTF8Encoding $false
    $raw = [System.IO.File]::ReadAllText($Manifest)

    # Touch only the <Identity ... Version="x.x.x.x" ...> attribute (case-sensitive, single).
    $identityVersionRx = [regex]::new(
        '(?s)(<Identity\b[^>]*?\bVersion=")([^"]+)(")',
        [System.Text.RegularExpressions.RegexOptions]::None)
    if (-not $identityVersionRx.IsMatch($raw)) {
        throw 'Identity Version attribute missing in Package.appxmanifest'
    }
    $raw = $identityVersionRx.Replace($raw, ('${1}' + $packageVersion + '${3}'), 1)

    if ($Publisher) {
        $identityPublisherRx = [regex]::new(
            '(?s)(<Identity\b[^>]*?\bPublisher=")([^"]+)(")',
            [System.Text.RegularExpressions.RegexOptions]::None)
        $raw = $identityPublisherRx.Replace($raw, ('${1}' + $Publisher + '${3}'), 1)
    }

    # Sanity: XML declaration must stay version="1.0"
    if ($raw -notmatch '^\s*<\?xml\s+version="1\.0"') {
        throw "Packaging manifest XML declaration corrupted after stamp"
    }
    if ($raw -notmatch [regex]::Escape('Version="' + $packageVersion + '"')) {
        throw "Identity Version was not stamped to $packageVersion"
    }
    # MinVersion on TargetDeviceFamily must not have been rewritten
    if ($raw -match 'MinVersion="' + [regex]::Escape($packageVersion) + '"') {
        throw 'MinVersion was incorrectly rewritten to package version'
    }

    [System.IO.File]::WriteAllText($packManifest, $raw, $utf8)

    # Validate with XmlDocument before handing to winapp
    try {
        $xmlCheck = New-Object System.Xml.XmlDocument
        $xmlCheck.Load($packManifest)
        $idNode = $xmlCheck.DocumentElement.SelectSingleNode(
            "/*[local-name()='Package']/*[local-name()='Identity']")
        if ($null -eq $idNode) { throw 'Identity node missing after stamp' }
        $got = $idNode.Attributes['Version'].Value
        if ($got -ne $packageVersion) { throw "Identity.Version=$got expected $packageVersion" }
    } catch {
        throw "stamped manifest invalid: $($_.Exception.Message)"
    }

    # Packaging uses the stamped copy only — leave source Package.appxmanifest untouched.
    $Manifest = $packManifest
    Write-Ok "Packaging manifest: $Manifest (Version=$packageVersion)"
} catch {
    Write-Fail "manifest version stamp failed: $($_.Exception.Message)"
    exit 1
}

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

# Multiple .exe may exist (e.g. createdump.exe) — pin the app executable.
$relExe = "$ProjectName.exe"
if (-not (Test-Path (Join-Path $buildOutput $relExe))) {
    $foundExe = Get-ChildItem $buildOutput -Filter "$ProjectName.exe" -Recurse -EA SilentlyContinue | Select-Object -First 1
    if ($foundExe) {
        $relExe = $foundExe.FullName.Substring($buildOutput.Length).TrimStart('\','/')
    }
}
Write-Ok "Package executable: $relExe"

& winapp package $buildOutput --manifest $Manifest --output $msixPath --cert $CertPath --cert-password $CertPassword --self-contained --executable $relExe --verbose
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
elseif (Test-Path $CertPath) {
    try {
        $pfx = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2($CertPath, $CertPassword)
        [IO.File]::WriteAllBytes($distCer, $pfx.Export([System.Security.Cryptography.X509Certificates.X509ContentType]::Cert))
        Write-Ok "CER exported: $distCer"
    } catch { Write-Warn "export cer: $($_.Exception.Message)" }
}

$installPs1 = Join-Path $OutputDir 'Install-LinuxDo.ps1'
$installBody = @'
#Requires -Version 5.1
# Install LinuxDo MSIX + trust dev signing cert.
# Right-click -> Run with PowerShell. If publisher check fails, run as Administrator.

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$msix = Get-ChildItem $here -Filter 'LinuxDo-latest.msix' -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $msix) { $msix = Get-ChildItem $here -Filter 'LinuxDo_*.msix' | Sort-Object LastWriteTime -Descending | Select-Object -First 1 }
$cer = Join-Path $here 'LinuxDo-Signing.cer'
if (-not $msix) { Write-Host 'No .msix found next to this script.' -ForegroundColor Red; exit 1 }

Write-Host "Package: $($msix.FullName)" -ForegroundColor Cyan

function Test-Admin {
    $p = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
    return $p.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

if (Test-Path $cer) {
    Write-Host "Trusting certificate: $cer" -ForegroundColor Cyan
    foreach ($sn in @('TrustedPeople', 'Root')) {
        try {
            $store = New-Object System.Security.Cryptography.X509Certificates.X509Store($sn, 'LocalMachine')
            $store.Open('ReadWrite')
            $cert = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2($cer)
            $store.Add($cert)
            $store.Close()
            Write-Host "  Installed into LocalMachine\$sn" -ForegroundColor Green
        } catch {
            Write-Host "  Skip LocalMachine\$sn : $($_.Exception.Message)" -ForegroundColor Yellow
        }
    }
    if (-not (Test-Admin)) {
        Write-Host 'Tip: if install still fails publisher check, re-run this script as Administrator.' -ForegroundColor Yellow
    }
}

Write-Host 'Installing / upgrading MSIX...' -ForegroundColor Cyan
try {
    # ForceUpdateFromAnyVersion: allow replace when an older same-version 1.0.0.0
    # package is still installed, or when content changed under a previous stamp.
    Add-AppxPackage -Path $msix.FullName -ForceApplicationShutdown -ForceUpdateFromAnyVersion -ErrorAction Stop
    Write-Host 'Installed successfully. Launch LinuxDo from Start Menu.' -ForegroundColor Green
} catch {
    $msg = $_.Exception.Message
    Write-Host "Add-AppxPackage failed: $msg" -ForegroundColor Red
    if ($msg -match '0x80073D06|same version|相同的版本|相同版本') {
        Write-Host 'Retrying after removing previous LinuxDo package...' -ForegroundColor Yellow
        try {
            Get-AppxPackage -Name '*F9B5A1A2-51F9-4CE4-8100-87B6CC5C04D1*' -ErrorAction SilentlyContinue |
                ForEach-Object { Remove-AppxPackage -Package $_.PackageFullName -ErrorAction SilentlyContinue }
            Get-AppxPackage | Where-Object { $_.Name -like '*LinuxDo*' -or $_.PackageFullName -like '*LinuxDo*' } |
                ForEach-Object { Remove-AppxPackage -Package $_.PackageFullName -ErrorAction SilentlyContinue }
            Add-AppxPackage -Path $msix.FullName -ForceApplicationShutdown -ForceUpdateFromAnyVersion -ErrorAction Stop
            Write-Host 'Installed successfully after cleanup. Launch LinuxDo from Start Menu.' -ForegroundColor Green
            exit 0
        } catch {
            Write-Host "Retry failed: $($_.Exception.Message)" -ForegroundColor Red
        }
    }
    Write-Host '1) Developer Mode ON' -ForegroundColor Yellow
    Write-Host '2) Run this script as Administrator' -ForegroundColor Yellow
    Write-Host '3) winapp cert install .\certs\devcert.pfx --password password' -ForegroundColor Yellow
    Write-Host '4) Remove old package: Get-AppxPackage *LinuxDo* | Remove-AppxPackage' -ForegroundColor Yellow
    exit 1
}
'@
$utf8NoBom = New-Object System.Text.UTF8Encoding $false
[System.IO.File]::WriteAllText($installPs1, $installBody, $utf8NoBom)
Write-Ok "Install helper: $installPs1"

$readme = Join-Path $OutputDir 'INSTALL.txt'
$readmeLines = @(
    'LinuxDo Windows - MSIX install notes',
    "BuildId: $buildId",
    "PackageVersion: $packageVersion",
    '',
    '!!! DO NOT double-click the .msix first !!!',
    'Self-signed package will show "publisher could not be verified" until the certificate is trusted.',
    '',
    'Recommended:',
    '  1. Settings > System > For developers > Developer Mode = ON',
    '  2. Keep these files in the SAME folder:',
    '       LinuxDo-latest.msix  +  LinuxDo-Signing.cer  +  Install-LinuxDo.ps1',
    '  3. Right-click Install-LinuxDo.ps1 > Run with PowerShell',
    '     If blocked: open PowerShell as Administrator, then:',
    '       Set-ExecutionPolicy -Scope Process Bypass -Force',
    '       .\Install-LinuxDo.ps1',
    '  4. Start Menu > LinuxDo',
    '',
    'Manual (GUI, no PowerShell):',
    '  1. Developer Mode ON',
    '  2. Double-click LinuxDo-Signing.cer > Install Certificate',
    '     > Local Machine > Place all certificates in: Trusted Root Certification Authorities',
    '  3. THEN double-click the .msix to install',
    '',
    'Why "publisher could not be verified"?',
    '  Sideload builds use a self-signed cert (CN=LinuxDo), not a Store cert. Trust the .cer first.',
    '',
    'Repo: https://github.com/ct-jyjntc/linuxdo_for_win'
)
[System.IO.File]::WriteAllLines($readme, $readmeLines, $utf8NoBom)
Write-Ok $readme

# Chinese install guide: keep Chinese OUT of this .ps1 (Windows PowerShell 5.1
# mis-parses UTF-8 without BOM). Template is UTF-8 under scripts/.
$readmeCn = Join-Path $OutputDir 'INSTALL-zh.txt'
$cnTemplate = Join-Path $Root 'scripts\install-zh.txt'
if (Test-Path $cnTemplate) {
    $cnText = [System.IO.File]::ReadAllText($cnTemplate, [System.Text.Encoding]::UTF8)
    $cnText = $cnText.Replace('__BUILD_ID__', $buildId)
    [System.IO.File]::WriteAllText($readmeCn, $cnText, $utf8NoBom)
    # Also write a UTF-8 BOM copy with Chinese filename when possible
    try {
        $readmeCnNamed = Join-Path $OutputDir ([char]0x5B89 + [char]0x88C5 + [char]0x8BF4 + [char]0x660E + '.txt') # 安装说明.txt via codepoints
        $utf8Bom = New-Object System.Text.UTF8Encoding $true
        [System.IO.File]::WriteAllText($readmeCnNamed, $cnText, $utf8Bom)
    } catch {
        # optional
    }
    Write-Ok $readmeCn
} else {
    Write-Warn 'scripts\install-zh.txt missing - skip Chinese INSTALL note'
}

Write-Step 'SHA256SUMS'
$sums = Join-Path $OutputDir 'SHA256SUMS.txt'
$lines = @()
Get-ChildItem $OutputDir -File | Where-Object { $_.Extension -in '.msix', '.cer' } | ForEach-Object {
    $h = (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    $lines += "$h  $($_.Name)"
}
$lines | Set-Content $sums -Encoding utf8
Write-Ok $sums

$sizeMb = [math]::Round((Get-Item $msixPath).Length / 1MB, 2)
Write-Host ''
Write-Host '========================================' -ForegroundColor Green
Write-Host ' PACKAGED  LinuxDo  (signed MSIX)' -ForegroundColor Green
Write-Host '========================================' -ForegroundColor Green
Write-Host " BuildId  : $buildId"
Write-Host " Version  : $packageVersion"
Write-Host " Platform : $Platform"
Write-Host " Publisher: $Publisher"
Write-Host " MSIX     : $msixPath"
Write-Host " Size     : $sizeMb MB"
Write-Host " CER      : $distCer"
Write-Host " Install  : $installPs1"
Write-Host ''
Write-Host "Quick install: powershell -ExecutionPolicy Bypass -File `"$installPs1`"" -ForegroundColor DarkGray
Write-Host "GitHub: attach MSIX + CER + Install-LinuxDo.ps1, tag = $buildId" -ForegroundColor DarkGray
Write-Host ''

if ($Open) { Start-Process explorer.exe "/select,`"$msixPath`"" }
exit 0
