# Play music in FiveM without holding push-to-talk

FiveM voice is commonly proximity-based, but the exact voice resource and its
rules are chosen by the server. If the server lets you use an open or
voice-activated microphone input, music would normally make your physical mic
open too. MicMixer lets the game receive the music continuously while applying
push-to-talk to your voice before the two signals reach the game.

MicMixer fixes this by giving your voice and your music **separate** rules. The
game listens to one open microphone (a virtual cable). MicMixer decides what
goes into it: your voice waits for a push-to-talk key, while the music flows
continuously.

> **Server limitation:** this setup only removes FiveM's push-to-talk when the
> server permits an open or voice-activated input. Some RP servers use a custom
> voice resource that forces its own push-to-talk. MicMixer cannot open that
> server-controlled gate; follow the server's rules and do not try to bypass it.

## What you need

- [VB-CABLE](https://vb-audio.com/Cable/) (or another virtual audio cable).
- MicMixer, running.
- Your normal microphone.

## Step 1 — Route MicMixer into the game and open its voice gate

1. Install VB-CABLE and reboot if the installer asks.
2. In MicMixer, set **Normal mic** to your real microphone and **Virtual cable
   output** to *CABLE Input (VB-Audio Virtual Cable)*. If you don't use a voice
   changer, choose **No modded mic**.
3. In FiveM's voice settings, set **Input Device** to **CABLE Output**. FiveM
   exposes both an input-device setting and a voice-chat mode; a server resource
   may replace or override either one. Both settings are listed in the official
   [FiveM profile-settings reference](https://docs.fivem.net/docs/game-references/profile-settings/).
4. Set **Voice Chat Mode** to its voice-activated/open option if that option is
   available. If the server has its own voice menu, use the equivalent setting
   there. Adjust microphone sensitivity so normal music opens the input without
   clipping its quiet passages.
5. Click **Enable** in MicMixer and play a track briefly. Confirm with another
   player or the server's voice indicator that the cable is received without
   holding FiveM's push-to-talk key.

If step 4 is unavailable or the test in step 5 only works while FiveM's talk key
is held, the server is applying a second push-to-talk gate. Stop here: the
continuous-music setup is not supported on that server. You can still use
MicMixer while holding the server's required talk key, but MicMixer cannot
remove that requirement.

## Step 2 — Turn on push-to-talk in MicMixer

Because the game listens to an always-open cable, MicMixer has to be the thing
that gates your voice — otherwise your mic is live all the time.

1. Set a **Global hotkey** in MicMixer (for example a mouse side button or the
   key that feels natural for speaking).
2. Enable **push-to-talk**. Now, while the hotkey is *not* held, MicMixer sends
   silence for your voice; while it is held, your voice goes through.

At this point MicMixer is the only push-to-talk gate in the supported setup, so
you do not hold FiveM's talk key. Keep using the voice mode that you verified in
step 1, and follow any server rules about voice activation and transmitted
music.

## Step 3 — Let the music ignore push-to-talk

1. Add music: paste a YouTube link and click **Download MP3**, or point MicMixer
   at a folder of your own `.mp3` files.
2. In the music card, enable **Music ignores push-to-talk**.
3. Start a track.

Now the music plays into the game continuously, and your voice still only goes
through while you hold the MicMixer hotkey. Release the key and the music keeps
going while your mic goes quiet.

## Checking what's live

The tray icon and the optional overlay show the state at a glance:

- Green mic + purple music circle: both are going into the game.
- Red crossed-out mic + purple music: your voice is gated, music still playing.
- Amber headphones on the music circle: **Monitor only** — you're previewing a
  track and it is *not* going into the game yet.

Use **Monitor only** to line up the next song and set its volume before anyone
else hears it, then turn it off to send it.

## Common problems

- **Others can't hear the music.** Confirm the game's mic is set to *CABLE
  Output* and MicMixer's output is *CABLE Input*, and that routing is enabled.
  Also confirm that FiveM or the server's voice resource is not waiting for its
  own push-to-talk key; if that gate is mandatory, this setup is unsupported.
  Disable noise suppression / echo cancellation in the game or voice resource —
  those filters often strip out music.
- **The music cuts out when you stop talking.** *Music ignores push-to-talk* is
  off, or push-to-talk isn't enabled. The ignore toggle only does something
  while push-to-talk is on.
- **Music sounds thin or filtered.** Same suppression filters as above; turn
  them off on the receiving side.
- **You hear yourself.** You've enabled local monitoring or a secondary output
  on a device you can hear. That's separate from the cable — see the main
  [README](../../README.md#local-monitoring).

## Next step

Want your microphone *and* music to reach Discord as a single input as well —
without the game and Discord fighting over devices? See
[Mic and music as one Discord input](discord-single-input.md).
