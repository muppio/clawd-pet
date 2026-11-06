<#
  Makes the prebuilt Windows app that ships inside the plugin, so people can
  install Clawd on Windows without the .NET SDK:
    app\dist-windows\ClawdPet-win-x64.zip   (self-contained, x64)
    app\dist-windows\VERSION                (the build id /pet compares with the installed app's)
  Run it on Windows after changing anything in windows\, before publishing,
  or let the "Windows app" GitHub workflow build it and download its artifact.
#>
$ErrorActionPreference = 'Stop'

$here = $PSScriptRoot
$dist = Join-Path (Split-Path $here -Parent) 'app\dist-windows'
$work = Join-Path ([IO.Path]::GetTempPath()) ('clawdpet-release-' + [guid]::NewGuid().ToString('N'))

try {
    & (Join-Path $here 'build.ps1') -Out $work
    if ($LASTEXITCODE) { exit $LASTEXITCODE }

    New-Item -ItemType Directory -Force -Path $dist | Out-Null
    $zip = Join-Path $dist 'ClawdPet-win-x64.zip'
    Remove-Item -LiteralPath $zip -ErrorAction SilentlyContinue
    Compress-Archive -Path (Join-Path $work '*') -DestinationPath $zip -CompressionLevel Optimal
    Copy-Item -LiteralPath (Join-Path $work 'build-id') -Destination (Join-Path $dist 'VERSION') -Force

    $size = [math]::Round((Get-Item -LiteralPath $zip).Length / 1MB, 1)
    $version = Get-Content -LiteralPath (Join-Path $dist 'VERSION') -Raw
    Write-Host "Released build ${version}: $zip ($size MB)"
}
finally {
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}
