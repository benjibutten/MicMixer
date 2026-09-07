# Local voice profiles

MicMixer loads voices from `%LOCALAPPDATA%\MicMixer\Voices\<UUID>.json`.
Two generic built-in starting points, **Feminine (starter)** and **Masculine
(starter)**, are available alongside local files. No personal voice or recording
ships with the application. A clean installation defaults to no modified input;
choose a profile explicitly before enabling the local route. Invalid files appear
as errors, and a missing selected ID is never replaced by the first available voice.
Profiles saved in the voice designer appear immediately. Restart after adding files manually.

## Format and import

Format version 1 contains `formatVersion`, `id` (canonical UUID), `displayName`,
`parameters`, and optional `alternateBlockMilliseconds`. All
`VoiceDspParameters` fields are required except that format 1 may omit the
format-2 engine fields `pitchEngine`, `timeDomainWindowMilliseconds`, and
`timeDomainSearchMilliseconds`; omitted values select Signalsmith and its default
time-domain settings. Format 2 requires every parameter field. Filenames must
match IDs. Display names can change
without changing identity. Records are immutable snapshots shared by routing and the voice designer.
`VoiceProfileStore.Import` validates, writes a temporary file, and moves it without
overwrite; existing profiles, including user edits, are preserved. Profiles are
validated at the standard 48 kHz rate on import and at the actual stream format
before DSP construction. A profile incompatible with that format produces an error.

Parameter limits are defined in `VoiceDspParameters.Validate`. Defaults describe
an unshifted test starting point, not a bundled personal voice. No recording or
reference audio belongs inside a profile or application package.

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

## Create a voice in the app

Stop routing, select **Local voice profile**, then choose **Create a voice…**.
Start from neutral settings, a built-in starter, or an installed profile. Give the new
voice a name, record a short phrase from the selected normal microphone, and
choose headphones under **Listen on**. The selected virtual cable is excluded
from preview outputs. A sample is limited to 15 seconds, kept only in memory,
and discarded when the dialog closes.

Recording and playback share the panel above the voice controls. Use **Play original**
and **Play voice** to audition the same phrase, with optional looping. The active
button becomes **Pause**, then **Resume**; resuming continues from the same position.
**Stop** returns to the start. The buttons return to Play when the sample ends.
Edits while the sample is playing re-render it in the background and swap it in
at the same position, so a slider can be dialled in by ear without restarting.
Rendering runs in the background using the live DSP, flushes its tail and removes
its reported latency to preserve the sample timeline. Preview listening volume is
independent of the profile and the main processed-voice volume.

Pitch and formant are the main voice-character controls. Warmth, clarity and air
shape its tone, and texture adds saturation. Advanced controls expose EQ frequencies,
compression, tonality and analysis timing. A larger analysis window can trade more
live latency for smoother processing. The processing interval cannot exceed half
the analysis window; breaking that rule blocks saving and says so next to the Save
button. Frequency and compressor time sliders use a logarithmic scale so their
useful low end stays reachable; every row shows its unit. Numeric fields accept
exact values and snap out-of-range entries to the nearest legal one; sliders also
support the keyboard. All settings use the existing DSP validation limits.

**Reset** restores the starting DSP settings. **Cancel** discards the draft and
asks first when there is something to lose. Starting from a built-in or the neutral
point, **Save voice** writes a new UUID profile atomically and selects it in the
main window. Starting from one of your own profiles, **Save changes** writes back
to that profile and **Save as copy** keeps both, so editing a voice no longer
accumulates "(copy) (copy)" duplicates. Built-in starters can never be overwritten.
A legacy alternate window is carried over when a profile is saved back to itself.
Save contains parameters only, never the test recording. **Delete** in the main
window removes the selected profile after confirming; built-ins cannot be deleted.
Start routing to use the new profile live.

## Built-in starter tuning

These are editable starting points, not guaranteed voice conversions. Perceived
voice gender involves pitch, resonance and other speech characteristics; pitch
alone is insufficient ([ASHA overview](https://www.asha.org/practice-portal/professional-issues/gender-affirming-voice-and-communication/)).
The numeric choices below are engineering estimates, not prescribed or listening-validated settings.

| Starter | Pitch | Extra formant adjustment | Tone |
| --- | --- | --- | --- |
| Feminine | +5 semitones (~1.33×) | −2 semitones | Less low-mid body, gently brighter presence/air |
| Masculine | −5 semitones (~0.75×) | +2 semitones | More low-mid body, gently softer presence/air |

The existing backend uses `compensatePitch: false`. Pitch shifts therefore also
move the spectral envelope. The extra formant adjustments temper that shift,
giving roughly +3/−3 semitones of net resonance movement in the tonal range,
rather than exaggerating it. This follows the engine's
[formant compensation behavior](https://github.com/Signalsmith-Audio/signalsmith-stretch#formant-compensation).
Both starters use gentle 2:1 compression, no saturation and the existing 40 ms
analysis window. Adjust Pitch to your input first, then Resonance adjustment.

Starters have stable reserved UUIDs and are loaded from the application, without
creating files in AppData. Save voice always creates a new local UUID copy; imports
cannot overwrite or shadow a starter. The original starters remain available.

The device row at the top of the routing column holds three peer dropdowns: normal
mic, modified voice and virtual cable output. The modified-voice dropdown only names
the *kind* of source. Its settings live in a full-width panel directly below, which is
absent for **None**, holds the device picker for **External microphone / Voicemod**,
and holds the profile picker, **Create a voice**, **Delete** and the voice volume for
**Local voice profile**. Keeping the settings out of the device row is what stops one
column from growing several rows taller than the two beside it.

The main voice volume slider occupies its own full-width row. The old alternate
analysis-window checkbox is hidden unless the selected profile actually supplies
an alternate. For a longer alternate it reads **Smoother processing (more delay)**;
its tooltip explains the quality/latency tradeoff. The two starters do not need this
option, and nothing in the app writes an alternate, so in practice the checkbox only
appears for profiles migrated from an earlier layout.

On a first run no profile has been chosen yet, so the main window selects the first
starter rather than leaving an empty box that only reports the problem once Enable
has already failed. While routing is active the profile list, **Create a voice** and
**Delete** are disabled with a tooltip that says why, instead of accepting the click
and refusing afterwards.

## Time-domain pitch engine (format 2)

Profiles can choose the streaming resampling/WSOLA engine with `pitchEngine:
"TimeDomain"`, `timeDomainWindowMilliseconds` (10–50, default 24), and
`timeDomainSearchMilliseconds` (0–20, default 8). Use `formatVersion: 2` and
include all DSP fields. Format-1 files missing these three fields still load with
the original Signalsmith engine and defaults. Unknown engines and malformed
versions are rejected; old app versions reject format 2 rather than silently
substituting another pitch algorithm.

The time-domain engine uses a rational polyphase FIR resampler, a linked-channel
waveform alignment search, 50% overlap and squared-sine weights. Absolute input
positions avoid accumulating timing drift. Constructor-prepared buffers bound
memory use; Process and Flush do not allocate or perform file I/O. Callback sizes
may vary down to one frame, and Reset clears all history. The end-of-file flush
uses zero padding, trims the reported delay and preserves the original length.

Independent formant adjustment is not implemented for this engine: it must be
zero. The designer disables unsupported resonance/Signalsmith controls and shows
the waveform window/search controls for time-domain profiles. Copies preserve the
engine and save as format 2. The two original starter profiles remain unchanged;
quality at their larger shifts should be auditioned before changing defaults.

Segment length is not total algorithmic latency. The processor reports the
bounded window/search/FIR lookahead at the actual sample rate and pitch. This
may exceed 50 ms for larger windows or shifts; callers must inspect LatencySamples.
Preview and routing use the same processor and post-EQ/compression implementation.
The output limiter, dry delay compensation and routing/hotkey semantics are unchanged.
All personal profiles remain local JSON files; no private voice parameters or
reference audio are embedded in the application.
