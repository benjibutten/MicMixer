# What's changed

<!-- Update this list together with user-visible changes under src/. -->

- Local voice profiles replace the fixed voice configuration. Profile identity, display name and DSP settings are stored privately per user. Existing local installations migrate automatically through an explicit local binding.
- Two built-in voice starting points, Feminine and Masculine, can be used immediately or customized as local copies. They contain generic effect settings only.
- Create custom local voices in the new voice designer. Record a short sample, compare original and processed playback with optional looping, adjust voice character and advanced effects, and save a new profile in local AppData without editing JSON. Test recordings stay in memory.
- The voice designer now tunes by ear: adjusting a setting while the sample plays re-renders it and keeps playing from the same spot instead of stopping. A live input-level meter shows whether the microphone is picking you up, every slider shows its unit, and frequency and compressor timing sliders use a logarithmic scale so their low end is actually reachable.
- Voice volume now gets a full-width slider. Alternate processing quality appears only for profiles that support it. Recording and comparison playback share a panel, with clear Play / Pause / Resume buttons and a separate Stop action.
- Your own voices can be edited and deleted. Saving back to a profile you started from replaces it, **Save as copy** keeps both, and **Delete** removes one from the main window. Cancelling a draft asks before discarding it.
- The modified-voice settings moved out of the device row into their own panel below it. The three device dropdowns stay aligned, the panel is simply absent when the modified voice is **None**, and switching mode no longer shoves the cards underneath up and down. The voice designer is disabled with a reason while routing runs rather than refusing the click afterwards, and a fresh install starts on a built-in starter instead of an empty profile box.
- Processed voice volume remains after DSP with smooth, saved level changes. Missing or invalid selected profiles produce an error.
- Advanced: the voice designer now has a Pitch engine picker to switch a voice between the default spectral engine and a second, time-domain one (format 2). Choosing time-domain shows its waveform window/search controls and disables independent resonance adjustment, which this engine does not support; resetting to the starting point restores its original engine.

- The StreamDecky remote-control connection no longer logs a spurious error
  when a client disconnects abruptly instead of closing cleanly.
