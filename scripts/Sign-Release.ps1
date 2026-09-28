# Signs and verifies each file with the release certificate from the
# WINDOWS_CODESIGN_CERT_PFX_BASE64 and WINDOWS_CODESIGN_CERT_PASSWORD secrets.
# Returns $false, signing nothing, when neither secret is configured.
param([Parameter(Mandatory)][string[]]$Path)
$ErrorActionPreference = 'Stop'

$hasCertificate = -not [string]::IsNullOrWhiteSpace($env:WINDOWS_CODESIGN_CERT_PFX_BASE64)
$hasPassword = -not [string]::IsNullOrWhiteSpace($env:WINDOWS_CODESIGN_CERT_PASSWORD)

if ($hasCertificate -ne $hasPassword)
{
  throw "Both WINDOWS_CODESIGN_CERT_PFX_BASE64 and WINDOWS_CODESIGN_CERT_PASSWORD must be configured together."
}

if (-not $hasCertificate)
{
  return $false
}

$certPath = Join-Path $env:RUNNER_TEMP "micmixer-codesign.pfx"
try
{
  [IO.File]::WriteAllBytes($certPath, [Convert]::FromBase64String($env:WINDOWS_CODESIGN_CERT_PFX_BASE64))
  foreach ($file in $Path)
  {
    signtool sign `
      /fd SHA256 `
      /f $certPath `
      /p $env:WINDOWS_CODESIGN_CERT_PASSWORD `
      /tr "http://timestamp.digicert.com" `
      /td SHA256 `
      $file | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "signtool sign failed for $file with exit code $LASTEXITCODE." }

    signtool verify /pa /v $file | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "signtool verify failed for $file with exit code $LASTEXITCODE." }
  }
}
finally
{
  Remove-Item -LiteralPath $certPath -Force -ErrorAction SilentlyContinue
}

return $true
