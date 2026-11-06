<#
  Builds Clawd Pet for Windows from source. Needs the .NET 8 SDK
  (winget install Microsoft.DotNet.SDK.8). The result is self-contained:
  the PC that runs it needs no .NET at all.

    build.ps1               publish into %LOCALAPPDATA%\ClawdPet, then start it
    build.ps1 -Out <dir>    publish into <dir> and don't start it (release.ps1 uses this)
#>
param([string]$Out = "")
$ErrorActionPreference = 'Stop'

$here = $PSScriptRoot
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    [Console]::Error.WriteLine('Building Clawd needs the .NET 8 SDK. Install it with: winget install Microsoft.DotNet.SDK.8')
    exit 1
}

$dest = if ($Out) { $Out } else { Join-Path $env:LOCALAPPDATA 'ClawdPet' }
$work = Join-Path ([IO.Path]::GetTempPath()) ('clawdpet-' + [guid]::NewGuid().ToString('N'))

try {
    Write-Host 'Compiling...'
    & dotnet publish (Join-Path $here 'ClawdPet.csproj') --nologo -c Release -r win-x64 --self-contained true `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:EnableCompressionInSingleFile=true -p:DebugType=none -o $work
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed (exit $LASTEXITCODE)" }

    # Which source this app was built from: /pet compares it with the prebuilt app's.
    # Line endings are normalised so a CRLF checkout hashes like the LF one.
    $sha = [Security.Cryptography.SHA256]::Create()
    $bytes = New-Object System.Collections.Generic.List[byte]
    Get-ChildItem $here -File |
        Where-Object { $_.Extension -in '.cs', '.csproj', '.manifest', '.ico' } |
        Sort-Object Name |
        ForEach-Object {
            if ($_.Extension -eq '.ico') {
                $bytes.AddRange([IO.File]::ReadAllBytes($_.FullName))
            } else {
                $text = [IO.File]::ReadAllText($_.FullName) -replace "`r`n", "`n"
                $bytes.AddRange([Text.Encoding]::UTF8.GetBytes($text))
            }
        }
    $hash = -join ($sha.ComputeHash($bytes.ToArray()) | ForEach-Object { $_.ToString('x2') })
    [IO.File]::WriteAllText((Join-Path $work 'build-id'), $hash.Substring(0, 16))

    if (-not $Out) {
        Stop-Process -Name ClawdPet -Force -ErrorAction SilentlyContinue
        Get-Process -Name ClawdPet -ErrorAction SilentlyContinue | Wait-Process -Timeout 5 -ErrorAction SilentlyContinue
        if (Test-Path -LiteralPath $dest) { Remove-Item -LiteralPath $dest -Recurse -Force }
    }
    New-Item -ItemType Directory -Force -Path $dest | Out-Null
    Copy-Item -Path (Join-Path $work '*') -Destination $dest -Recurse -Force

    if (-not $Out) {
        Write-Host 'Starting Clawd...'
        Start-Process -FilePath (Join-Path $dest 'ClawdPet.exe')
    }
    Write-Host "Done: $dest"
}
finally {
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}
