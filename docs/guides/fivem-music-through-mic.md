# Play music in FiveM without holding push-to-talk

FiveM voice is usually proximity-based, but each server picks its own voice
resource and rules. If the server lets you use an open or voice-activated
microphone, playing music into it would normally keep your real mic open too.

MicMixer gives your voice and your music **separate** rules. The game listens to
one open microphone, a virtual cable, and MicMixer decides what goes into it:
your voice waits for a push-to-talk key, while the music keeps flowing.

> **Server limitation:** this setup only removes FiveM's push-to-talk when the
> server permits an open or voice-activated input. Some RP servers use a custom
> voice resource that forces its own push-to-talk. MicMixer cannot open that
> server-controlled gate; follow the server's rules and do not try to bypass it.

## What you need

- [VB-CABLE](https://vb-audio.com/Cable/) (or another virtual audio cable).
- MicMixer, installed and running. [Setup](../index.html#setup) covers the
  download and the Windows warnings you'll click past.
- Your normal microphone.

## Step 1: Route MicMixer into the game and open its voice gate

1. Install VB-CABLE and reboot if the installer asks. The MicMixer installer can
   do this for you; if it also named the cable, its ends are *MicMixer Input* and
   *MicMixer Output* instead of *CABLE Input* and *CABLE Output* below.
2. In MicMixer's **Settings › Devices**, set **Normal mic** to your real microphone
   and **Send the mix to** to *CABLE Input (VB-Audio Virtual Cable)*, then click
   **Save**. If you don't use a voice changer, set **Voice changer** to **Off**.
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

## Step 2: Turn on push-to-talk in MicMixer

The game listens to an always-open cable, so MicMixer has to gate your voice.
Otherwise your mic is live all the time.

1. Open **Settings › Hotkey** in MicMixer and set a **Hotkey** (for example a
   mouse side button or the key that feels natural for speaking).
2. Enable **push-to-talk** on the same page and click **Save**. While the hotkey
   is up, MicMixer sends silence for your voice; while you hold it, your voice
   goes through.

MicMixer is now the only push-to-talk gate, so you don't hold FiveM's talk key.
Keep the voice mode you verified in step 1, and follow any server rules about
voice activation and music.

## Step 3: Let the music ignore push-to-talk

1. Add music: paste a YouTube link and click **Download MP3**, or point MicMixer
   at a folder of your own `.mp3` files.
2. In the music card, enable **Music ignores push-to-talk**.
3. Start a track.

The music now plays into the game continuously, and your voice only goes
through while you hold the MicMixer hotkey. Release the key and your mic goes
quiet while the music keeps going.

## Checking what's live

The tray icon and the optional overlay show the state at a glance:

- Green mic + purple music circle: both are going into the game.
- Red crossed-out mic + purple music: your voice is gated, music still playing.
- Amber headphones on the music circle: **Monitor only**. You're previewing a
  track, and it is *not* going into the game yet.

Use **Monitor only** to line up the next song and set its volume before anyone
else hears it, then turn it off to send it.

## Common problems

- **Others can't hear the music.** Confirm the game's mic is set to *CABLE
  Output* and MicMixer's output is *CABLE Input*, and that routing is enabled.
  Also confirm that FiveM or the server's voice resource is not waiting for its
  own push-to-talk key; if that gate is mandatory, this setup is unsupported.
  Turn off noise suppression and echo cancellation in the game or voice
  resource, since those filters often strip out music.
- **Your character keeps "talking" after you stop.** FiveM decides who is
  talking from the signal on the cable, with no hold time of its own. While the
  MicMixer hotkey is held, room noise alone can keep its voice detection
  triggered. Enable the noise gate in **Settings › Noise gate** and set the
  threshold so the level bar passes the knob while you talk and stays below it
  while you are quiet. The cable then carries true silence between phrases.
  Keep **Release delay** at 0 when you use push-to-talk, and lower FiveM's
  **Microphone Sensitivity** if quiet sounds still register.
- **The music cuts out when you stop talking.** *Music ignores push-to-talk* is
  off, or push-to-talk isn't enabled. The ignore toggle only does something
  while push-to-talk is on.
- **Music sounds thin or filtered.** Same suppression filters as above; turn
  them off on the receiving side.
- **You hear yourself.** You've enabled local monitoring or a secondary output
  on a device you can hear. That's separate from the cable; see the
  [README](../../README.md#local-monitoring).

## Next step

To send your mic and your music to Discord as a single input too, see
[Mic and music as one Discord input](discord-single-input.md).
