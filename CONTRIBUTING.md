# Contributing

## Build and test

The app is in `src/MicMixer` and targets `net10.0-windows`.

```powershell
dotnet build .\MicMixer.slnx
pwsh -File .\scripts\Test.ps1
```

Local voice profiles are stored outside the repository. See
[local voice profiles](docs/local-voice-profiles.md) for configuration,
migration, verification and offline rendering.

To make a self-contained Windows x64 build:

```powershell
dotnet publish .\src\MicMixer\MicMixer.csproj `
  -c Release `
  -r win-x64 `
  --self-contained true `
  -o .\artifacts\publish\win-x64

# The launcher is compiled with NativeAOT, which needs the MSVC build tools.
dotnet publish .\src\MicMixer.Launcher\MicMixer.Launcher.csproj `
  -c Release `
  -r win-x64 `
  -o .\artifacts\publish\win-x64
```

## Release notes

Update [RELEASE_NOTES.md](RELEASE_NOTES.md) with user-visible changes in the
same change as the app code. The release workflow adds the version, commit,
archive hash, and verified signing status when it creates the GitHub Release,
and the app shows the notes once after updating.

## Branches and releases

Only `main` publishes a release. A push to `main` that changes `src/**` runs
`.github/workflows/release.yml`, which restores the solution, runs the full test
suite, publishes a self-contained `win-x64` build, optionally signs and verifies
the executable, and creates a ZIP with a SHA-256 checksum. The release tag is
created for the exact commit that was built. Pushes that only change
documentation, tests, the solution file, or workflows do not release.

Small fixes can go from a feature branch straight to `main`. Changes that need
testing together can be combined on the optional `next` branch before `next` is
merged to `main`.

To get a candidate build from any branch, run `.github/workflows/build.yml`
manually. It runs the same restore, test, and publish steps and produces a ZIP
and SHA-256 file, without creating a tag or a public release.
