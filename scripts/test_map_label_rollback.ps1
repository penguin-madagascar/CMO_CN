# Exercise Rollback.Run with real child processes and isolated package and game directories.
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$sandbox = Join-Path $tempRoot ('CMO map rollback ' + [Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($sandbox)
try {
    $packages = Join-Path $sandbox 'packages'
    [void][IO.Directory]::CreateDirectory($packages)
    $dll = Join-Path $sandbox 'fixture.dll.bytes'
    $dict = Join-Path $sandbox 'fixture.json'
    $map = Join-Path $sandbox 'fixture.dat'
    [IO.File]::WriteAllText($dll, 'test engine')
    [IO.File]::WriteAllText($map, 'test map')
    foreach ($version in '2.0.0', '2.0.1', '2.0.2') {
        $setup = Join-Path $packages ("CMO_Chinese_Setup_v$version.exe")
        [IO.File]::WriteAllText($dict, '{"_meta":{"version":"' + $version + '"}}')
        $options = @('/nologo', '/utf8output', '/langversion:5', '/codepage:65001', '/target:exe',
            "/out:$setup", "/resource:$dll,CMOLocalizer.dll", "/resource:$dict,CMOLocalizer.json")
        if ($version -eq '2.0.1') { $options += "/resource:$map,MAP_TILES" }
        & $compiler @options (Join-Path $repo 'src\tests\MapLabelRollbackFixture.cs')
        if ($LASTEXITCODE -ne 0) { throw "Failed to compile rollback fixture $version" }
        if ($version -eq '2.0.2') {
            $sha = (Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash.ToLowerInvariant()
            [IO.File]::Copy($map, "$setup.$sha.map.dat")
        }
    }
    $test = Join-Path $sandbox 'MapLabelRollbackTests.exe'
    & $compiler /nologo /utf8output /langversion:5 /codepage:65001 /target:exe "/out:$test" `
        (Join-Path $repo 'src\shared\CMOShared.cs') `
        (Join-Path $repo 'src\shared\MapLabelOriginalCache.cs') `
        (Join-Path $repo 'src\tests\MapLabelRollbackTests.cs')
    if ($LASTEXITCODE -ne 0) { throw 'Failed to compile rollback tests' }
    & $test $sandbox
    if ($LASTEXITCODE -ne 0) { throw 'Map label rollback tests failed' }
}
finally {
    $path = [IO.DirectoryInfo]::new($sandbox)
    if ($path.Parent.FullName.TrimEnd('\') -ieq $tempRoot.TrimEnd('\') -and
        $path.Name -match '^CMO map rollback [a-f0-9]{32}$' -and
        -not ($path.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        Remove-Item -LiteralPath $path.FullName -Recurse -Force
    }
}
