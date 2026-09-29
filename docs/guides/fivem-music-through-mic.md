# Play music in FiveM without holding push-to-talk

FiveM voice usually means a choice: hold the talk key all night to keep your
music audible, or let the music cut out every time you release it. MicMixer
removes that choice by giving your voice and your music **separate** rules.

The game listens to one microphone, a virtual cable, and MicMixer decides what
goes into it: your voice waits for your MicMixer hotkey, while the music flows
continuously. MicMixer then holds down FiveM's push-to-talk key for exactly as
long as it sends something, so FiveM transmits when there is voice or music on
the cable and stays silent otherwise.

## What you need

- [VB-CABLE](https://vb-audio.com/Cable/) (or another virtual audio cable).
- MicMixer, running.
- Your normal microphone.

## Step 1 — Route MicMixer into the game

1. Install VB-CABLE and reboot if the installer asks. The MicMixer installer can
   do this for you; if it also named the cable, its ends are *MicMixer Input* and
   *MicMixer Output* instead of *CABLE Input* and *CABLE Output* below.
2. In MicMixer's **Settings › Devices**, set **Normal mic** to your real microphone
   and **Send the mix to** to *CABLE Input (VB-Audio Virtual Cable)*, then click
   **Save**. If you don't use a voice changer, set **Voice changer** to **Off**.
3. In FiveM's voice settings, set **Input Device** to **CABLE Output**, not
   *Default*. Both this and the voice chat mode are listed in the official
   [FiveM profile-settings reference](https://docs.fivem.net/docs/game-references/profile-settings/).

## Step 2 — Turn on push-to-talk in MicMixer

1. Open **Settings › Hotkey** in MicMixer and set a **Hotkey**, for example a
   mouse side button or the key that feels natural for speaking.
2. Turn on **Use push-to-talk** on the same page.

While the hotkey is *not* held, MicMixer sends silence; while it is held, your
voice goes through. This is the only talk key you press from now on.

## Step 3 — Let MicMixer press FiveM's talk key

FiveM's push-to-talk gets its own key that you never touch: MicMixer holds it
down while it sends to the cable.

1. On the same **Settings › Hotkey** page, turn on **Hold a key while sending**
   and keep **F24** in the list next to it. F13–F24 exist to Windows but not on a
   normal keyboard, so they never collide with chat or game controls. Don't pick
   a key that is also one of your MicMixer hotkeys; MicMixer warns you if you do.
2. Click **Save**.
3. In FiveM, set **Voice Chat Mode** to **push-to-talk**.
4. Open the game's key bindings (**Settings › Key Bindings**) and select the
   **Push to Talk** binding. Back in MicMixer, click **Send key once**, switch to
   the game and start the binding there. MicMixer presses F24 after 5 seconds,
   and the binding should read **F24**. Clicking **Send key once** again cancels.

FiveM's push-to-talk listens for keyboard keys even while the game is in the
background, so this keeps working when you alt-tab to MicMixer or another
window.

## Step 4 — Let the music ignore push-to-talk

1. Add music: paste a YouTube link and click **Download MP3**, or point MicMixer
   at a folder of your own `.mp3` files.
2. In the music card, turn on **Music ignores push-to-talk**.
3. Click **Enable** in the main window and start a track.

Now the music plays into the game continuously, and your voice only goes
through while you hold the MicMixer hotkey. Release the key and the music keeps
going while your mic goes quiet. When the music stops and you are not talking,
MicMixer lets go of F24 a tenth of a second later and FiveM stops transmitting.

Test it with another player: they should hear the music without you pressing
anything, and your voice only while you hold the hotkey.

## Why not voice activation?

FiveM also offers a voice-activated mode, and it seems like the natural fit for
an always-open cable. It is less reliable here. In that mode FiveM, not
MicMixer, decides when you are talking, and a server's voice resource can
switch FiveM's input to a music mode in which it transmits all the time,
silence included. Other players may then see your character's mouth move while
you are quiet, even when there is nothing to hear. With push-to-talk on the key
MicMixer holds, FiveM transmits only while MicMixer sends something.

Server rules still apply. If the server blocks talking in some situations, for
example while your character is downed, it blocks what MicMixer sends as well.
Follow the server's rules about transmitting music.

## Checking what's live

The tray icon and the optional overlay show the state at a glance:

- Green mic + purple music circle: both are going into the game.
- Red crossed-out mic + purple music: your voice is gated, music still playing.
- Amber headphones on the music circle: **Monitor only** — you're previewing a
  track and it is *not* going into the game yet.

Use **Monitor only** to line up the next song and set its volume before anyone
else hears it, then turn it off to send it.

## Common problems

- **Others hear nothing at all.** Check that FiveM's **Input Device** is *CABLE
  Output*, that MicMixer sends to *CABLE Input*, and that routing is enabled.
  Then check that the game's **Push to Talk** binding reads **F24** (or the key
  you picked) and that **Hold a key while sending** is on and saved.
- **Your voice only gets through while the game window has focus.** The **Push to
  Talk** binding is on a mouse button. FiveM only follows the keyboard in the
  background; bind F24 as in step 3.
- **Your voice is live all the time.** **Use push-to-talk** is off in MicMixer.
  Without it MicMixer always sends your mic, and so always holds F24.
- **The music cuts out when you stop talking.** **Music ignores push-to-talk** is
  off in the music card.
- **The key does nothing while a program running as administrator has focus.**
  Windows keeps keys from a normal program away from programs running as
  administrator. MicMixer points this out in the main window and can restart
  itself as administrator.
- **Music sounds thin or filtered.** Noise suppression in FiveM or the voice
  resource strips out music. Turn it off if the server's voice menu offers it.
- **You hear yourself.** You've enabled local monitoring or a secondary output
  on a device you can hear. That's separate from the cable — see the main
  [README](../../README.md#local-monitoring).

## Next step

Want your microphone *and* music to reach Discord as a single input as well —
without the game and Discord fighting over devices? See
[Mic and music as one Discord input](discord-single-input.md).
