param([string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
$repository = Split-Path $PSScriptRoot
& dotnet build (Join-Path $repository 'MicMixer.slnx') -c $Configuration --nologo --no-restore
if ($LASTEXITCODE) { throw 'Build failed.' }
$runner = Join-Path $repository "tests/MicMixer.Tests/bin/$Configuration/net10.0-windows10.0.19041.0/MicMixer.Tests.exe"
& $runner -noLogo
if ($LASTEXITCODE) { throw "Test execution failed: $LASTEXITCODE" }
