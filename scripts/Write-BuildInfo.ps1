param(
    [Parameter(Mandatory = $true)]
    [string]$OutFile,

    [Parameter(Mandatory = $true)]
    [string]$BuildId
)

$dir = Split-Path -Parent $OutFile
if ($dir -and -not (Test-Path $dir)) {
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
}

$content = @"
namespace LinuxDo.Core.Utilities;

// auto-generated - do not edit
public static class BuildInfo
{
    public const string BuildId = "$BuildId";
}
"@

# UTF-8 without BOM (avoids C# lexer weirdness with BOM + corrupted lines)
$utf8NoBom = New-Object System.Text.UTF8Encoding $false
[System.IO.File]::WriteAllText($OutFile, $content, $utf8NoBom)
Write-Host "LinuxDo BuildId = $BuildId -> $OutFile"
