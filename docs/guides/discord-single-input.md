# Mic and music as one Discord input

Discord only lets you pick **one** microphone. If you want your friends to hear
both your voice and your music, you normally can't — the music player and your
mic are two different sources. MicMixer solves this by mixing them into a single
virtual microphone that Discord treats as one ordinary input.

This guide builds on [Play music in FiveM without holding
push-to-talk](fivem-music-through-mic.md); the routing is the same, only the
receiving app changes.

## What you need

- [VB-CABLE](https://vb-audio.com/Cable/) (or another virtual audio cable).
- MicMixer, running.
- Discord.

## Step 1 — Route MicMixer into Discord

1. Install VB-CABLE and reboot if asked.
2. In MicMixer, set **Normal mic** to your real microphone and **Virtual cable
   output** to *CABLE Input*. Choose **No modded mic** if you don't use a voice
   changer.
3. In Discord, open **Settings → Voice & Video** and set **Input Device** to
   **CABLE Output**.
4. Click **Enable** in MicMixer, say something, and watch Discord's input meter
   move.

## Step 2 — Turn off Discord's audio processing

Discord's noise suppression, echo cancellation, and automatic gain control are
tuned for a bare human voice and will fight your music — muffling it or ducking
it every time you speak.

In **Settings → Voice & Video**, turn **off**:

- Noise Suppression (including Krisp)
- Echo Cancellation
- Automatic Gain Control
- Advanced Voice Activity, if music keeps getting cut

Set MicMixer's own levels instead, using the input meters in the app.

## Step 3 — Decide how voice and music are gated

You have two comfortable setups:

- **Open mic + music:** leave push-to-talk off in both MicMixer and Discord.
  Your voice and music both flow continuously. Simple for hanging out.
- **Push-to-talk voice, continuous music:** enable **push-to-talk** in MicMixer
  with a global hotkey, and turn on **Music ignores push-to-talk** in the music
  card. Now your voice only goes through while you hold the key, but the music
  keeps playing. Leave Discord itself on Voice Activity so it passes through
  whatever MicMixer sends.

Keep Discord's own push-to-talk off in the push-to-talk setup — let MicMixer do
the gating, so it can hold the music open while gating only your voice.

## Should you use this on a stream too?

If you also stream, don't capture the Discord/cable path for your stream audio.
Use MicMixer's **secondary output** instead: it plays the full mic-plus-music
mix on a separate device that OBS or Streamlabs can capture, independent of what
Discord receives. See the [README](../../README.md#secondary-output).

## Common problems

- **Friends hear music but not you (or vice versa).** Check the input meters in
  MicMixer — if only one source moves, that source's device or volume is the
  problem. If push-to-talk is on, hold the hotkey while testing your voice.
- **Music keeps dipping when you talk.** Discord's Automatic Gain Control or
  noise suppression is still on. Turn them off (Step 2).
- **Robotic or gated music.** Same cause — the suppression filters treat music
  as noise. Disable them on Discord's side.
- **No input at all in Discord.** Discord's input must be *CABLE Output*, not
  *CABLE Input*, and MicMixer routing must be enabled.
