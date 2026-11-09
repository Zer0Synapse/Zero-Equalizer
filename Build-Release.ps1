param(
    [string]$Dotnet = 'dotnet',
    [switch]$NoRestore
)

$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'ZeroEqualizer.csproj'
$projectXml = [xml](Get-Content -LiteralPath $project -Raw)
$version = [string]$projectXml.Project.PropertyGroup.Version
if ($version -notmatch '^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$') {
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
$portableFiles = @(
    'ZeroEqualizer.exe'
    'ZeroEqualizer.dll'
    'ZeroEqualizer.deps.json'
    'ZeroEqualizer.runtimeconfig.json'
    'Start here.txt'
) | ForEach-Object { Join-Path $publish $_ }
$portableZip = Join-Path $release "$name.zip"
Compress-Archive -LiteralPath $portableFiles -DestinationPath $portableZip -Force

$sourceFiles = Get-ChildItem -LiteralPath $PSScriptRoot -File -Force | Where-Object {
    $_.Extension -in '.cs', '.xaml', '.csproj' -or
    $_.Name -in '.gitignore', 'README.md', 'Start here.txt', 'Build-Release.ps1'
} | Select-Object -ExpandProperty FullName
$sourceZip = Join-Path $release "$name-source.zip"
Compress-Archive -LiteralPath $sourceFiles -DestinationPath $sourceZip -Force

Write-Output $portableZip
Write-Output $sourceZip
