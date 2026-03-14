# MicMixer

MicMixer is a Windows WPF app that routes one of two microphone inputs to a virtual output device and switches to the modded input while a global hotkey is held.

## Requirements

- Windows 10 or Windows 11
- VB-CABLE or another virtual cable
- A normal microphone device
- A modded microphone device such as VoiceMod Virtual Audio Device

## Download

The newest downloadable build is published on the latest GitHub release:

https://github.com/benjibutten/MicMixer/releases/latest

## Usage

1. Download and extract the latest release zip.
2. Start `MicMixer.exe`.
3. Select the normal microphone, the modded microphone, and the virtual cable output device.
4. Choose a global hotkey.
5. In Discord, OBS, or the target app, select the recording side of the virtual cable as the microphone input.

## Development

```powershell
dotnet build .\src\MicMixer\MicMixer.csproj
```

## Automatic Releases

Every push to `main` runs GitHub Actions on Windows, publishes a self-contained `win-x64` build, zips it, and updates one rolling GitHub release tagged `latest`.

## Pull Request Validation

Pull requests that target `main` run a separate GitHub Actions workflow that restores and builds the app without publishing a release.
