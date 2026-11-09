# What's changed

<!-- Update this list together with user-visible changes under src/. -->

- **A newer audio library.** MicMixer now uses NAudio 3.3.0 instead of 3.0.0. It
  fixes a mic that could lose audio when it runs at another sample rate than the
  virtual cable, seeking in some MP3 files starting over from the beginning, and
  capture or playback that could fail to start again after a stop.
