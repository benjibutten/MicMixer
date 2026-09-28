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

# VB-Audio allows bundling this package with a free app as long as the installer names
# VB-Audio and its donationware model (https://vb-audio.com/Services/licensing.htm).
# Pinned: a changed hash stops the build until someone has looked at the new package.
$vbCableUrl = 'https://download.vb-audio.com/Download_CABLE/VBCABLE_Driver_Pack45.zip'
$vbCableSha256 = 'B950E39F01AF1D04EA623C8F6D8EB9B6EA5C477C637295FABF20631C85116BFB'

# The GitHub runner image installs Inno Setup through Chocolatey.
$iscc = @(
  (Get-Command ISCC.exe -ErrorAction SilentlyContinue)?.Source
  "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe"
  "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
if (-not $iscc) { throw 'Inno Setup (ISCC.exe) is not installed.' }

$vbCableDirectory = Join-Path ([IO.Path]::GetTempPath()) "MicMixer-vbcable-$([guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $vbCableDirectory | Out-Null
try
{
  $vbCableZip = Join-Path $vbCableDirectory 'VBCABLE_Driver_Pack.zip'
  Invoke-WebRequest -Uri $vbCableUrl -OutFile $vbCableZip
  $actual = (Get-FileHash -LiteralPath $vbCableZip -Algorithm SHA256).Hash
  if ($actual -ne $vbCableSha256) { throw "The VB-CABLE package has SHA-256 $actual, expected $vbCableSha256." }
  $vbCableFiles = Join-Path $vbCableDirectory 'files'
  Expand-Archive -LiteralPath $vbCableZip -DestinationPath $vbCableFiles

  $script = Join-Path (Split-Path $PSScriptRoot) 'installer\MicMixer.iss'
  $arguments = @(
    '/Q'
    "/DAppVersion=$Version"
    "/DPublishDir=$(Resolve-Path $PublishDirectory)"
    "/DVBCableDir=$vbCableFiles"
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
}
finally
{
  Remove-Item -LiteralPath $vbCableDirectory -Recurse -Force -ErrorAction SilentlyContinue
}

return Join-Path $OutputDirectory "MicMixer-$Version-win-x64-setup.exe"
