# MicMixer

MicMixer is a Windows application that routes microphone audio and music into a
single virtual microphone channel. It is intended for applications that only
allow one microphone input while you want to speak normally, temporarily switch
to a voice-modified microphone, and mix music into the same channel.

MicMixer is built with WPF and uses NAudio for audio capture, playback, and
routing.

## Features

- Routes a normal microphone to a virtual audio cable such as VB-CABLE.
- Switches to a voice-modified microphone while a global hotkey is held.
- Supports setups without a modified microphone.
- Supports a configurable release delay before switching back to the normal mic.
- Provides push-to-talk for the complete outgoing mix, including music.
- Optionally lets music bypass push-to-talk, so the music keeps playing into the
  virtual cable while the voice stays gated.
- Provides a monitor-only preview mode that keeps music out of the virtual cable
  entirely while you listen to it in local monitoring.
- Displays input levels for the normal and modified microphones.
- Plays MP3 files and mixes them into the virtual microphone channel.
- Captures audio directly from an external application such as Spotify.
- Downloads audio from YouTube links and converts it to MP3.
- Provides optional local monitoring with independent output and volume controls.
- Provides an optional secondary output that plays the complete mix (mic + music)
  on an extra device — before the push-to-talk gate — for recording or streaming.
- Includes playlist search, multiple music folders, a queue, and transport controls.
- Provides a click-through status overlay with mic state, music animation, and an
  optional outgoing level meter.
- Serves the same overlay as a local web page for streaming software with browser
  source support, so viewers see the mic and music status even when only the game is captured.
- Uses a responsive, resizable window layout and remembers its size and state.
- Runs in the system tray and redirects additional launches to the existing instance.
- Can start automatically with Windows, hidden in the system tray.
- Writes rotating logs for troubleshooting.

## Requirements

- Windows 10 or Windows 11, x64.
- A microphone.
- A virtual audio cable; [VB-CABLE](https://vb-audio.com/Cable/) is recommended.
- Optional: Voicemod or another voice modifier that exposes a microphone device.
- Internet access for the first media download. MicMixer downloads `yt-dlp` and
  `ffmpeg` to its local application-data directory and verifies the files using
  SHA-256.

Release archives are self-contained, so users normally do not need to install
.NET separately.

## Download

Download the latest `MicMixer-win-x64.zip` archive from
[GitHub Releases](https://github.com/benjibutten/MicMixer/releases/latest), extract
it, and run `MicMixer.exe`.

## Basic setup

1. Install [VB-CABLE](https://vb-audio.com/Cable/).
2. Restart Windows if requested by the VB-CABLE installer.
3. Start `MicMixer.exe`.
4. Select your physical microphone under **Normal mic**.
5. Select a modified microphone, such as **Voicemod Virtual Audio Device**, or
   choose **No modded mic**.
6. Select **CABLE Input (VB-Audio Virtual Cable)** under **Virtual cable output**.
7. In your voice chat, game, or streaming app, select **CABLE Output** as the microphone.
8. Click **Enable**.

If the selected output does not look like a virtual cable, MicMixer displays a
warning. Click **Enable** again to continue with another cable driver or an
intentional non-cable output.

## Hotkey and push-to-talk

Click **Change** under **Global hotkey**, then press the keyboard key or mouse
button that should select the modified mic while routing is active:

- Hotkey held: the modified mic is routed.
- Hotkey released: the normal mic is routed.
- Release delay above `0 ms`: the modified mic remains active until the delay ends.
- **No modded mic**: the hotkey is disabled unless push-to-talk is enabled.

Push-to-talk reverses the idle behavior: while the hotkey is not held, the virtual
cable receives silence. Neither microphone audio nor music is sent.

- It also works with **No modded mic**, in which case the hotkey gates the normal mic.
- With a modified mic selected, holding the hotkey both opens the gate and selects it.
- The release delay applies to push-to-talk as well.
- Local music monitoring is not muted by push-to-talk.
- The tray icon and overlay turn red with a crossed-out mic while the outgoing mix
  is muted.
- With **Music ignores push-to-talk** enabled (music card), push-to-talk gates
  only the microphone: the music keeps flowing into the virtual cable as long as
  it plays. See [Music routing](#music-routing).

## Music sources

MicMixer can use its built-in MP3 library or capture audio from another application.
In either mode, the audio is mixed into the same virtual microphone channel as your
voice.

### MP3 library

Add music by either:

- Pasting a YouTube link and clicking **Download MP3**.
- Opening a configured music folder and adding your own `.mp3` files.

The first download installs local copies of `yt-dlp` and `ffmpeg`. The ffmpeg
package is relatively large, so the first download may take longer. The tools are
reused for subsequent downloads.

The library supports search, playback controls, a queue, and multiple music folders.
When multiple folders are configured:

- Each track has a colored folder badge; hover over it to see the full path.
- Folder chips beside the search field filter the visible tracks.
- The download destination can be selected separately.
- Folders can be added or removed from the folder menu; at least one folder remains
  configured.

Music can play while routing or local monitoring provides an audio clock. If neither
is active, playback pauses instead of appearing to play without advancing.

### External application capture

Select the external-application source mode to capture audio from a running
application such as Spotify and route it through MicMixer. Refresh the application
list when a newly started app is not shown. Transport buttons send system media-key
commands to the active media application.

External capture availability depends on the Windows process-loopback APIs and the
selected application producing audio.

## Local monitoring

Monitoring plays library music through your own headphones or speakers. It has a
separate device and volume control and does not change the level sent to other
people through the virtual microphone. Select a physical playback device rather
than the virtual cable.

## Music routing

Two toggles in the music card control how the music relates to push-to-talk and
the virtual cable. Both apply immediately, also while routing runs, and both work
in external capture mode as well.

- **Music ignores push-to-talk**: the music keeps flowing into the virtual
  cable as long as it plays, while push-to-talk still gates the microphone.
  Typical use: the game should hear the music continuously, but your voice only
  while you hold the hotkey. The toggle is only active while push-to-talk is
  enabled — without push-to-talk, playing music is always sent.
- **Monitor only**: preview mode. The music is never sent to the virtual
  cable; you hear it through local monitoring and can check a track or set its
  volume before anyone else hears it. The secondary output still receives the
  music, so streaming or recording software capturing that device hears what you hear. An
  amber hint below the toggle states exactly where the music goes while the mode
  is active. Monitor-only overrides the ignore-push-to-talk toggle.

The status panel and the overlay always reflect the outcome: when push-to-talk
mutes the mic while music still flows, the status reads "Mic muted (push-to-talk)
— music transmitting", and the overlay's music circle shows the current destination.

## Secondary output

The **Secondary output** section routes the complete finished mix — microphone and
music — to one extra playback device while routing is active. The branch has its
own gates, independent of the cable's: with **Ignore push-to-talk** enabled
(default) the secondary device keeps receiving mic + music even while the
virtual cable receives silence. Disable that option to make the secondary
microphone follow the same push-to-talk gate as the cable; the music then still
flows whenever you can hear it — while it is sent to the cable or previewed in
monitor-only mode — so a stream capturing the secondary device always hears the
music you hear.

Typical use with streaming or recording software:

1. Enable **Secondary output** and select a playback device that you are not
   otherwise using (for example an unused HDMI output or a virtual device).
2. In your streaming software (for example, OBS Studio or Streamlabs Desktop),
   add an audio output capture source and select the same device.
3. Start routing in MicMixer. The software now records or streams the full mix, including
   everything you say while push-to-talk is released.

Warnings:

- **The secondary device plays your own microphone.** If you pick your normal
  speakers, everyone in the room hears your mic and you risk feedback into the
  microphone. Prefer a device you cannot hear, or headphones.
- **The capture source includes everything on that device.** Any other application playing
  audio to the same device ends up in the recording.
- The secondary output can never use the same device as **Virtual cable output**;
  MicMixer blocks that combination because the mix would play twice on the cable.
- If the saved secondary device is missing at startup, MicMixer leaves the
  selection empty and refuses to start the secondary output until you explicitly
  pick a device again — it never substitutes another output on its own.

The secondary volume only affects the secondary device. Failures on the
secondary device (for example unplugging it) close only the secondary branch;
routing to the virtual cable continues.

## Overlay indicator

The optional click-through overlay remains visible above borderless/windowed games
without stealing focus or intercepting mouse input. The mic circle shows the same
status colors and glyphs as the tray icon:

- Green with a mic: the normal microphone is heard.
- Blue with a modified-mic glyph: the modified microphone is heard.
- Red with a crossed-out mic: push-to-talk is muted.
- Gray (tray icon only): routing is stopped.

While music is active, a separate music circle appears to the left of the mic
circle and tells where the music goes:

- Purple with a note and dancing equalizer bars: the music is sent into the mic
  channel.
- Amber with headphones: monitor-only preview — only you (and the secondary
  output) hear it.
- Gray with a crossed-out note and frozen bars: the music plays but push-to-talk
  currently blocks it from the cable.

The optional level rings wrap each circle: the mic ring shows the complete
outgoing mix after the gates (exactly what the cable receives), and the music
ring shows the music branch alone after the music volume — also during a
monitor-only preview, so the level can be set before anyone else hears it.
Exclusive fullscreen applications may prevent desktop overlays from being visible.

## Stream overlay

When streaming software captures a game as an individual process, desktop
overlays may not be part of the captured image, so viewers never see the overlay
indicator. Enable **Stream overlay** in the routing settings and MicMixer serves
the same overlay as a local web page (`http://127.0.0.1:4573/` by default)
that can be added as a browser source and layered on top of the game. The
page mirrors the desktop overlay exactly — states, level rings, equalizer bars,
and the hidden state while routing is stopped — and reconnects automatically
when MicMixer restarts. See [docs/obs-overlay.md](docs/obs-overlay.md) for setup
steps, URL parameters, and the wire protocol.

## Local data

MicMixer stores settings, its default music folder, downloaded tools, and logs in:

```text
%LocalAppData%\MicMixer
```

Important files and directories:

- `settings.json`: devices, hotkey, volumes, folders, window state, and other settings.
- `Music\`: the default MP3 library folder.
- `tools\`: downloaded `yt-dlp` and `ffmpeg` binaries.
- `logs\micmixer-YYYYMMDD.log`: runtime logs.
- `startup-timeline.log`: a basic startup timeline.

Runtime logs roll at 5 MB, rotate daily, and are retained for 14 days.

## Troubleshooting

- If nobody can hear anything, confirm that MicMixer outputs to **CABLE Input** and
  that the receiving application uses **CABLE Output** as its microphone.
- If others cannot hear music, confirm that routing is active and disable noise
  suppression, echo cancellation, and automatic gain control in the receiving app.
- If local monitoring is silent, enable monitoring and select your headphones or
  speakers as the monitor device.
- If external application capture is silent, make sure the selected app is actively
  producing audio and refresh the application list.
- If a download fails, check the internet connection and inspect the logs under
  `%LocalAppData%\MicMixer\logs`.
- If an audio device was disconnected, refresh the device list and select it again.
- If the secondary output shows "device was not found", reconnect the device or
  select a new one explicitly; MicMixer intentionally never auto-picks a
  replacement secondary device.

## Development

The application project is located in `src/MicMixer` and targets `net10.0-windows`.

Build and test locally:

```powershell
dotnet build .\MicMixer.slnx
dotnet test .\MicMixer.slnx --no-build
```

Create a self-contained Windows x64 build:

```powershell
dotnet publish .\src\MicMixer\MicMixer.csproj `
  -c Release `
  -r win-x64 `
  --self-contained true `
  -o .\artifacts\publish\win-x64
```

## CI/CD

Pull requests targeting `main` run `.github/workflows/pr-build.yml`, which restores
dependencies and builds the application in Release configuration on Windows.

Pushes to `main` that change application source or project files run
`.github/workflows/release.yml`. The workflow publishes a self-contained `win-x64`
build, creates a zip archive, and creates or updates the latest GitHub Release.

Documentation- and workflow-only pushes do not trigger an application release. A
release can also be started manually through `workflow_dispatch`.

## Support and acknowledgements

MicMixer is built and maintained by BenjiButten and released free of charge as
open-source software. A special thank you goes to [Pixlexi](https://www.twitch.tv/pixlexi),
who has contributed the use cases behind the app, hands-on testing, and valuable
feedback throughout development.

If you enjoy MicMixer and would like to give something back, please consider
gifting a sub to [Pixlexi](https://www.twitch.tv/pixlexi) on Twitch.

Third-party components and their license information are listed in
[THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt). That file, the project
license, and the attribution [NOTICE](NOTICE) are included in published distributions.

## License

MicMixer is licensed under the [Apache License 2.0](LICENSE). Redistributions
must also preserve the applicable attribution information from [NOTICE](NOTICE)
as required by the license.

Published releases up to and including `v2026.7.3` remain available under the
MIT terms under which they were originally released. Source from the Apache-2.0
relicensing commit onward is licensed under Apache-2.0.
