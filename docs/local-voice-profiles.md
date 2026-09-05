# Local voice profiles

MicMixer loads voices from `%LOCALAPPDATA%\MicMixer\Voices\<UUID>.json`.
No personal voice ships with the application. A clean installation defaults to
no modified input; selecting Local voice profile without an installed profile
shows an explanation and refuses to start that route. Invalid files appear as
errors, and a missing selected ID is never replaced by the first available voice.
Restart the app after importing a file to refresh the profile list.

## Format and import

Format version 1 contains `formatVersion`, `id` (canonical UUID), `displayName`,
`parameters` (all VoiceDspParameters fields), and optional
`alternateBlockMilliseconds`. Filenames must match IDs. Display names can change
without changing identity. Records are immutable and suitable for a future editor.
`VoiceProfileStore.Import` validates, writes a temporary file, and moves it without
overwrite; existing profiles, including user edits, are preserved. Profiles are
validated at the standard 48 kHz rate on import and at the actual stream format
before DSP construction. A profile incompatible with that format produces an error.

Parameter limits are defined in `VoiceDspParameters.Validate`. Defaults describe
an unshifted test starting point, not a bundled personal voice. All parameter fields
are mandatory when loading a versioned profile. No recording or reference audio
belongs inside a profile or application package.

## Migration

The settings reader understands historical voice-mode, window and volume keys.
Their values migrate to `LocalProfile`, `LongerAnalysisWindow` and
`ProcessedVoiceVolume`. Numeric mode values keep their original meaning.
The selected ID is persisted as `SelectedVoiceProfileId`.

For an existing installation, first privately import its verified profile, then
place `legacy-voice-profile.json` beside settings.json, containing only
`{"profileId":"<imported UUID>"}`. This installation-specific binding supplies
identity, never DSP defaults. Existing selected IDs take precedence. Missing or
broken bindings leave the local mode selected and cannot produce an accidental
fallback voice. Saved settings use generic keys. The importer does not change
already imported profiles. Old executable versions do not understand the new keys;
keep a private settings backup when testing old and new builds.

The base and alternate windows belong to the profile. The migrated window flag
selects the corresponding value without retuning the profile. Volume defaults to
unity and clamps to [0,1]. It remains after DSP, ramps over 10 ms and affects only
the processed voice. DSP order remains pitch/formant, EQ, linked compression,
saturation and limiting. Dry latency compensation, continuous processing, hotkey
crossfade, music, PTT, meters, external microphones and secondary output are retained.

Files are read and validated before routing starts. The processor factory captures
an immutable parameter snapshot, including for restarts. No profile file access,
locks or new allocation takes place in the audio callback.

## Verification

Run `pwsh -File scripts/Test.ps1` to build and execute all public tests. Alternatively, execute the built
`tests/MicMixer.Tests/bin/Release/net10.0-windows10.0.19041.0/MicMixer.Tests.exe -noLogo`.
The current project uses the xUnit executable runner; the solution-level testing
platform command can report zero tests and must not be treated as success.
Public tests use synthetic signals and generic parameters.

Offline render: `MicMixer.DspTest.exe raw.wav output.wav --preset <profile.json>`.
The CLI flushes DSP, trims the reported pre-roll and retains the input timeline.

Explicit private regression: `MicMixer.DspTest.exe --verify-local <manifest.json>`.
Keep the manifest and all data outside the repository. It contains `profilePath`,
`profileSha256` and a nonempty `cases` array. Each case supplies `rawPath`,
`rawSha256`, `beforePath`, `beforeSha256`, `alternateWindow`, `chunkMilliseconds`,
and optional `approvedPath`/`approvedSha256`. Capture before audio using the old
renderer, identical parameters, callback size and latency trimming. Hashes freeze
inputs and baselines. Before/after samples must match exactly; optional approved
24-bit PCM references must match within one quantization step. Missing data, changed
hashes, empty cases, format/length differences or sample errors produce a nonzero exit.
This proves offline parity, not subjective listening or downstream device behavior.

## Privacy before committing

Run `pwsh -File scripts/Test-PublicContent.ps1` to inspect the current Git index.
It is read-only and fails on known private paths/content. This is a guard, not a
replacement for reviewing the complete staged diff. `.gitignore` cannot remove
already indexed files. A prior index can still contain private source values and
deleted documents until the owner explicitly updates it. Never commit such an index.
Do not remove original recordings during source cleanup. Keep them outside the
repository or ignored and verify `git ls-files recording` is empty.

## Next editor step

Offer pitch/formant, tonality, analysis/interval, EQ, compressor and saturation
controls using the same limits. Preview uses a temporary parameter snapshot;
Save as creates a new UUID and atomically imports it. Reset restores the opened
snapshot; Cancel discards the draft. Preserve the original profile until explicit
save. Reconfiguration and latency-changing edits require a control-thread rebuild
of DSP and dry delay, followed by a safe route restart; never rebuild inside the
callback. Keep output volume independent and adjustable live through its existing ramp.
