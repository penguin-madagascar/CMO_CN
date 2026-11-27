[CmdletBinding()]
param([string]$RepositoryRoot)
$ErrorActionPreference = 'Stop'
if (-not $RepositoryRoot) { $RepositoryRoot = Split-Path -Parent $PSScriptRoot }
$repo = (Get-Item -LiteralPath $RepositoryRoot).FullName
$tempRoot = (Get-Item -LiteralPath ([IO.Path]::GetTempPath())).FullName
$work = Join-Path $tempRoot ('cmo-map-migration-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work | Out-Null
try {
    $engineDir = Join-Path $work 'engine'
    & (Join-Path $repo 'scripts/build.ps1') -OutputDir $engineDir
    $dll = Join-Path $engineDir 'CMOLocalizer.dll'
    if (!(Test-Path -LiteralPath $dll -PathType Leaf)) { throw 'Production engine did not build' }
    $exe = Join-Path $work 'map-migration-test.exe'
    $compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
    $options = @('/nologo','/codepage:65001','/langversion:5','/target:exe','/main:MapLabelMigrationTests',"/out:$exe")
    foreach ($ref in 'System.Core','System.Drawing','System.Windows.Forms','System.Xml','System.IO.Compression','System.IO.Compression.FileSystem') {
        $options += "/reference:$ref.dll"
    }
    $options += "/resource:$dll,CMOLocalizer.dll"
    $options += '/resource:' + (Join-Path $repo 'dict/CMOLocalizer.json') + ',CMOLocalizer.json'
    foreach ($set in 'Cities','Synoptic') {
        $options += '/resource:' + (Join-Path $repo "assets/placenames/$set.wwp") + ',PLACE_' + $set.ToUpperInvariant()
    }
    $options += Join-Path (Split-Path -Parent $PSScriptRoot) 'src/tests/MapLabelMigrationTests.cs'
    foreach ($path in 'src/installer/CMO_Chinese_Setup.cs','src/shared/CMOShared.cs',
        'src/shared/MapLabelRenderer.cs','src/shared/MapLabelCache.cs','src/shared/MapLabelOriginalCache.cs') {
        $options += Join-Path $repo $path
    }
    & $compiler @options
    if ($LASTEXITCODE -ne 0) { throw 'Map migration test build failed' }
    & $exe $work
    if ($LASTEXITCODE -ne 0) { throw 'Map migration tests failed' }
}
finally {
    $dir = Get-Item -LiteralPath $work
    if ($dir.Parent.FullName.TrimEnd('\') -ne $tempRoot.TrimEnd('\') -or $dir.Name -notmatch '^cmo-map-migration-[0-9a-f]{32}$' -or
        ($dir.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Unexpected map test path' }
    Remove-Item -LiteralPath $dir.FullName -Recurse -Force
}
