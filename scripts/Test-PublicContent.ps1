param([string]$Repository = (Split-Path $PSScriptRoot))
$ErrorActionPreference = 'Stop'
# Read-only inspection of the actual proposed commit, not just the worktree.
$paths = & git -c "safe.directory=$Repository" -C $Repository ls-files
if ($LASTEXITCODE) { throw 'Cannot inspect Git index.' }
$privatePaths = @($paths | Where-Object { $_ -match '(?i)(^recording/|^presets/|PrivateVoiceArchive/|(^|/)Voices/|girly|legacy-voice-profile\.json|private-manifest)' })
$matches = & git -c "safe.directory=$Repository" -C $Repository grep --cached -n -I -i -E 'girly|gf-pitching' -- . ':!src/MicMixer/Settings/SettingsStore.cs' ':!tests/MicMixer.Tests/VoiceProfileTests.cs' ':!scripts/Test-PublicContent.ps1'
if ($LASTEXITCODE -gt 1) { throw 'Cannot inspect indexed content.' }
if ($privatePaths.Count -gt 0 -or $matches) {
    $privatePaths | Write-Output
    $matches | Write-Output
    throw 'Private material remains in the Git index. Review and explicitly correct the index before committing. This script makes no changes.'
}
Write-Output 'Known private content checks passed; review the full staged diff before committing.'
