# What's changed

<!-- Update this list together with user-visible changes under src/. -->

- **The held key reaches the game when MicMixer runs as administrator.** While
  MicMixer's own window has focus, Windows keeps the key MicMixer holds from
  programs that are not running as administrator. MicMixer now presses or
  releases the key again as soon as another window takes focus, so starting or
  stopping music from the MicMixer window takes effect when you switch back to
  the game.
- **Push-to-talk stays silent when routing starts.** Enabling routing, or the
  voice changer restarting it, could let a short burst of mic and music through
  before the push-to-talk key was pressed.
- Playing a track whose file has been deleted says so in the status line and
  refreshes the playlist, instead of failing with a Media Foundation error.
- In app mode, MicMixer picks the app you used last time, otherwise one that is
  playing, instead of always preferring Spotify.
- **Change** on a hotkey row works again.
- The settings window opens centered over the main window every time, and the
  main window stays in front when the settings window closes.
- A settings file that cannot be read is kept as settings.json.bad before
  MicMixer starts over with defaults, and saving settings survives a power loss.
- Downloads of music and tools stop when MicMixer closes.
- Screen readers announce icon-only buttons, sliders and fields, and the last
  Swedish words in the interface are in English.
- **About** credits Signalsmith Stretch, which the MicMixer voices use.
