# What's changed

<!-- Update this list together with user-visible changes under src/. -->

- Two built-in voice starting points, Feminine and Masculine, can be used immediately or customized as local copies. They contain generic effect settings only.
- Voice volume now gets a full-width slider. Alternate processing quality appears only for profiles that support it. Recording and comparison playback share a panel, with clear Play / Pause / Resume buttons and a separate Stop action.
- The voice designer now tunes by ear: adjusting a setting while the sample plays re-renders it and keeps playing from the same spot instead of stopping. A live input-level meter shows whether the microphone is picking you up, every slider shows its unit, and frequency and compressor timing sliders use a logarithmic scale so their low end is actually reachable.
- Your own voices can be edited and deleted. Saving back to a profile you started from replaces it, **Save as copy** keeps both, and **Delete** removes one from the main window. Cancelling a draft asks before discarding it.
- The modified-voice settings moved out of the device row into their own panel below it. The three device dropdowns stay aligned, the panel is simply absent when the modified voice is **None**, and switching mode no longer shoves the cards underneath up and down. The voice designer is disabled with a reason while routing runs rather than refusing the click afterwards, and a fresh install starts on a built-in starter instead of an empty profile box.

- Create custom local voices in the new voice designer. Record a short sample, compare original and processed playback with optional looping, adjust voice character and advanced effects, and save a new profile in local AppData without editing JSON. Test recordings stay in memory.

- Local voice profiles replace the fixed voice configuration. Profile identity, display name and DSP settings are stored privately per user. Existing local installations can migrate through an explicit local binding.
- Processed voice volume remains after DSP with smooth, saved level changes. Missing or invalid selected profiles produce an error.



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

- The music downloader was updated. MicMixer now installs yt-dlp 2026.08.19
  instead of 2026.07.04, which keeps downloads working when a site changes how
  its media is served. Existing installs replace the tool by themselves the next
  time a download starts, so there is nothing to do by hand.
