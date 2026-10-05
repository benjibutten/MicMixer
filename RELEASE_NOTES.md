# What's changed

<!-- Update this list together with user-visible changes under src/. -->

- **The held key no longer lets go while music plays.** A game forgets the key
  when you switch to another window and could miss it when you came back, so its
  push-to-talk stayed off until the music stopped. While music plays, MicMixer
  now presses the key again every second.
- With push-to-talk, **Hold a key while sending** holds the key for as long as
  you hold the hotkey. With the noise gate on, it used to go down only once you
  had started speaking, so the other app missed the start of the first word.
- **Monitor only** is off each time MicMixer starts. Left on by mistake, it
  kept the music off the virtual cable at every start.
