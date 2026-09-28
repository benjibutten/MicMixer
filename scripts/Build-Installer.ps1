# Builds installer\MicMixer.iss from a published app and returns the installer's path.
# -Sign signs the installer and the uninstaller inside it through Sign-Release.ps1,
# which needs its certificate secrets in the environment.
param(
  [Parameter(Mandatory)][string]$PublishDirectory,
  [Parameter(Mandatory)][string]$OutputDirectory,
  [string]$Version = '0.0.0',
  [switch]$Sign
)
$ErrorActionPreference = 'Stop'

# The GitHub runner image installs Inno Setup through Chocolatey.
$iscc = @(
  (Get-Command ISCC.exe -ErrorAction SilentlyContinue)?.Source
  "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe"
  "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
if (-not $iscc) { throw 'Inno Setup (ISCC.exe) is not installed.' }

$script = Join-Path (Split-Path $PSScriptRoot) 'installer\MicMixer.iss'
$arguments = @(
  '/Q'
  "/DAppVersion=$Version"
  "/DPublishDir=$(Resolve-Path $PublishDirectory)"
  "/O$OutputDirectory"
)
if ($Sign)
{
  # Inno Setup replaces $f with the quoted path of each file it signs, and $q with
  # a quote, which survives being passed to ISCC where a literal one does not.
  $signScript = Join-Path $PSScriptRoot 'Sign-Release.ps1'
  $arguments += '/DSign'
  $arguments += "/Smicmixer=pwsh -NoProfile -File `$q$signScript`$q -Path `$f"
}

& $iscc @arguments $script | Out-Host
if ($LASTEXITCODE -ne 0) { throw "ISCC failed with exit code $LASTEXITCODE." }

return Join-Path $OutputDirectory "MicMixer-$Version-win-x64-setup.exe"
