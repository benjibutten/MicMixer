# Mic and music as one Discord input

Discord only lets you pick **one** microphone. If you want your friends to hear
both your voice and your music, you normally can't, because the music player
and your mic are two different sources. MicMixer mixes them into a single
virtual microphone that Discord treats as an ordinary input.

This guide builds on [Play music in FiveM without holding
push-to-talk](fivem-music-through-mic.md); the routing is the same, only the
receiving app changes.

## What you need

- [VB-CABLE](https://vb-audio.com/Cable/) (or another virtual audio cable).
- MicMixer, installed and running. [Setup](../index.html#setup) covers the
  download and the Windows warnings you'll click past.
- Discord.

## Step 1: Route MicMixer into Discord

1. Install VB-CABLE and reboot if asked. The MicMixer installer can do this for
   you; if it also named the cable, its ends are *MicMixer Input* and
   *MicMixer Output* instead of *CABLE Input* and *CABLE Output* below.
2. In MicMixer's **Settings › Devices**, set **Normal mic** to your real
   microphone and **Send the mix to** to *CABLE Input*, then click **Save**.
   Set **Voice changer** to **Off** if you don't use one.
   If virtual cables are new to you, the setup guide in **Settings › General**
   explains which end goes where.
3. In Discord, open **Settings → Voice & Video** and set **Input Device** to
   **CABLE Output**.
4. Click **Enable** in MicMixer, say something, and watch Discord's input meter
   move.

## Step 2: Turn off Discord's audio processing

Discord's noise suppression, echo cancellation, and automatic gain control are
tuned for a bare voice. They muffle music, or duck it every time you speak.

In **Settings → Voice & Video**, turn **off**:

- Noise Suppression (including Krisp)
- Echo Cancellation
- Automatic Gain Control
- Advanced Voice Activity, if music keeps getting cut

Set MicMixer's own levels instead, using the input meters in the app.

## Step 3: Decide how voice and music are gated

Two setups work well:

- **Open mic + music:** leave push-to-talk off in both MicMixer and Discord.
  Your voice and music both flow continuously. Simple for hanging out.
- **Push-to-talk voice, continuous music:** enable **push-to-talk** in MicMixer
  with a global hotkey, and turn on **Music ignores push-to-talk** in the music
  card. Now your voice only goes through while you hold the key, but the music
  keeps playing. Leave Discord itself on Voice Activity so it passes through
  whatever MicMixer sends.

In the push-to-talk setup, keep Discord's own push-to-talk off. MicMixer does
the gating, so it can keep the music going while it gates only your voice.

## Streaming at the same time

If you also stream, don't capture the Discord/cable path for your stream audio.
Use MicMixer's **secondary output** instead: it plays the full mic-plus-music
mix on a separate device that OBS or Streamlabs can capture, independent of what
Discord receives. See the [README](../../README.md#secondary-output).

## Common problems

- **Friends hear music but not you (or vice versa).** Check the input meters in
  MicMixer. If only one source moves, that source's device or volume is the
  problem. If push-to-talk is on, hold the hotkey while testing your voice.
- **Music keeps dipping when you talk.** Discord's Automatic Gain Control or
  noise suppression is still on. Turn them off (Step 2).
- **Robotic or gated music.** Same cause: the suppression filters treat music
  as noise. Disable them on Discord's side.
- **No input at all in Discord.** Discord's input must be *CABLE Output*, not
  *CABLE Input*, and MicMixer routing must be enabled.
