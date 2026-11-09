param(
    [string]$Dotnet = 'dotnet',
    [switch]$NoRestore
)

$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'ZeroEqualizer.csproj'
$projectXml = [xml](Get-Content -LiteralPath $project -Raw)
$version = [string]$projectXml.Project.PropertyGroup.Version
if ($version -notmatch '^\d+\.\d+\.\d+$') {
    throw 'Set a release version in ZeroEqualizer.csproj before packaging.'
}

$release = Join-Path $PSScriptRoot 'release'
$name = "ZeroEqualizer-$version"
$publish = Join-Path $release $name
$publishArgs = @('publish', $project, '-c', 'Release', '--self-contained', 'false', '-o', $publish)
if ($NoRestore) {
    $publishArgs += '--no-restore'
}

& $Dotnet @publishArgs
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE."
}

$startGuide = Join-Path $PSScriptRoot 'Start here.txt'
Copy-Item -LiteralPath $startGuide -Destination $publish -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'NAudio-LICENSE.txt') -Destination $publish -Force
$portableFiles = @(
    'ZeroEqualizer.exe'
    'ZeroEqualizer.dll'
    'ZeroEqualizer.deps.json'
    'ZeroEqualizer.runtimeconfig.json'
    'NAudio.Core.dll'
    'NAudio.Wasapi.dll'
    'NAudio-LICENSE.txt'
    'Start here.txt'
) | ForEach-Object { Join-Path $publish $_ }
$portableZip = Join-Path $release "$name.zip"
Compress-Archive -LiteralPath $portableFiles -DestinationPath $portableZip -Force
$manifest = [ordered]@{
    version = $version
    url = "https://github.com/Zer0Synapse/Zero-Equalizer/releases/download/v$version/$name.zip"
    sha256 = (Get-FileHash -LiteralPath $portableZip -Algorithm SHA256).Hash.ToLowerInvariant()
    size = (Get-Item -LiteralPath $portableZip).Length
}
$feed = Join-Path $release 'update.json'
[IO.File]::WriteAllText($feed, ($manifest | ConvertTo-Json), [Text.UTF8Encoding]::new($false))

$sourceFiles = Get-ChildItem -LiteralPath $PSScriptRoot -File -Force | Where-Object {
    $_.Extension -in '.cs', '.xaml', '.csproj' -or
    $_.Name -in '.gitignore', 'README.md', 'Start here.txt', 'Build-Release.ps1', 'NAudio-LICENSE.txt'
} | Select-Object -ExpandProperty FullName
$sourceZip = Join-Path $release "$name-source.zip"
Compress-Archive -LiteralPath $sourceFiles -DestinationPath $sourceZip -Force

Write-Output $portableZip
Write-Output $sourceZip
Write-Output $feed
