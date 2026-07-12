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
- Displays input levels for the normal and modified microphones.
- Plays MP3 files and mixes them into the virtual microphone channel.
- Captures audio directly from an external application such as Spotify.
- Downloads audio from supported video URLs and converts it to MP3.
- Provides optional local monitoring with independent output and volume controls.
- Includes playlist search, multiple music folders, a queue, and transport controls.
- Provides a click-through status overlay with mic state, music animation, and an
  optional outgoing level meter.
- Uses a responsive, resizable window layout and remembers its size and state.
- Runs in the system tray and redirects additional launches to the existing instance.
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
4. Select your physical microphone under **Vanlig mic**.
5. Select a modified microphone, such as **Voicemod Virtual Audio Device**, or
   choose **Ingen moddad mic**.
6. Select **CABLE Input (VB-Audio Virtual Cable)** under **Virtuell kabel ut**.
7. In Discord, OBS, FiveM, or your game, select **CABLE Output** as the microphone.
8. Click **Aktivera**.

If the selected output does not look like a virtual cable, MicMixer displays a
warning. Click **Aktivera** again to continue with another cable driver or an
intentional non-cable output.

## Hotkey and push-to-talk

Click **Ändra** under **Global hotkey**, then press the keyboard key or mouse
button that should select the modified mic while routing is active:

- Hotkey held: the modified mic is routed.
- Hotkey released: the normal mic is routed.
- Release delay above `0 ms`: the modified mic remains active until the delay ends.
- **Ingen moddad mic**: the hotkey is disabled unless push-to-talk is enabled.

Push-to-talk reverses the idle behavior: while the hotkey is not held, the virtual
cable receives silence. Neither microphone audio nor music is sent.

- It also works with **Ingen moddad mic**, in which case the hotkey gates the normal mic.
- With a modified mic selected, holding the hotkey both opens the gate and selects it.
- The release delay applies to push-to-talk as well.
- Local music monitoring is not muted by push-to-talk.
- The tray icon and overlay turn red with a crossed-out mic while the outgoing mix
  is muted.

## Music sources

MicMixer can use its built-in MP3 library or capture audio from another application.
In either mode, the audio is mixed into the same virtual microphone channel as your
voice.

### MP3 library

Add music by either:

- Pasting a supported video URL and clicking **Hämta MP3**.
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

## Overlay indicator

The optional click-through overlay remains visible above borderless/windowed games
without stealing focus or intercepting mouse input. It shows the same status colors
and glyphs as the tray icon:

- Green with a mic: the normal microphone is heard.
- Blue with a modified-mic glyph: the modified microphone is heard.
- Red with a crossed-out mic: push-to-talk is muted.
- Gray (tray icon only): routing is stopped.

Animated rings indicate that music is being routed. The optional meter shows the
level of the complete outgoing mix after the push-to-talk gate. Exclusive fullscreen
applications may prevent desktop overlays from being visible.

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

## License

See [LICENSE](LICENSE).
