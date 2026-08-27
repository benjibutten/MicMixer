# What's changed

<!-- Update this list together with user-visible changes under src/. -->



- Less delay through the whole audio path. Microphone capture, the virtual cable
  output, the secondary output and the music monitor now ask Windows for its
  smallest safe audio buffer and run their audio threads at the priority Windows
  reserves for pro audio. Devices that cannot do this keep working exactly as
  before: MicMixer falls back on its own and records why in the log.

- Logging can no longer disturb audio. The log file is written by a background
  worker instead of on the audio threads, so a slow disk cannot stall a buffer
  refill mid-playback. The log now also records the latency each device actually
  negotiated, which makes a real device problem easier to tell apart from a
  device that simply settled on a larger buffer, and repeated music-buffer trim
  messages are written at most once every few seconds instead of continuously.

- A music monitor device that fails while starting is now released properly,
  instead of leaving MicMixer holding a monitor that never plays.

- Temporary: starting MicMixer with MICMIXER_PRIMARY_LOW_LATENCY=0 runs the cable
  output the way it did before this change, so the two can be compared by ear.
  This switch will be removed once the new path is confirmed on real hardware.
