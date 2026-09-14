# What's changed

<!-- Update this list together with user-visible changes under src/. -->


- **A calmer main window.** It now shows a status card (whether you are heard,
  what the hotkey does, where the mix goes), the music player, and orange cards
  that appear only when something is wrong, each with a button to the fix.
- **Settings moved to their own window**, opened from **Settings** in the top
  corner: Devices, Hotkey, Noise gate, Overlay, Secondary output, Music folders
  and General.
- **Settings are saved with a Save button.** Changes still apply immediately so
  you can hear them, and **Discard changes** goes back to what you saved. The
  main window compares against the saved setup: an unplugged headset or cable
  now shows a card saying which device is used until it is back, instead of
  being silently replaced. Starting routing no longer overwrites your saved
  devices.
- **New setup guide** for first-time users. It opens on the first start,
  explains what MicMixer does and how a virtual cable works, recognizes an
  installed VB-CABLE, Virtual Audio Cable or Voicemeeter (and only shows install
  steps when none is found), and walks through the microphone, the cable, the
  game's microphone setting and the hotkey. Run it again from
  **Settings › General**.
- **Virtual cable output** is now called **Send the mix to**, with a short
  explanation of which end of the cable MicMixer uses and which one the game
  uses.
- New **Noise gate** (Settings › Noise gate): keeps the mic silent between
  phrases so the virtual cable carries true silence instead of room noise. Apps
  that use voice activation on the cable stop treating you as talking as soon as
  you stop speaking, even while the push-to-talk key is still held. Off by
  default.
- New **Volume** slider for the normal mic (Settings › Devices), for matching a
  quiet mic to a louder modified voice, with a level bar next to it. 100% is the
  default and sends the mic exactly as before. The processed-voice volume sits
  under the modified-voice picker on the same 0–200% scale.
- The microphone is no longer guessed as the virtual cable's own output when
  MicMixer picks devices for you.
