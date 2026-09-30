# What's changed

<!-- Update this list together with user-visible changes under src/. -->

- **Downloading music works again on a new install.** The ffmpeg build that
  MicMixer fetches the first time you download had been removed from GitHub, so
  the download failed with "404 (Not Found)".
- **Lower the music while you talk.** A new option in **Settings › Music** turns
  the music down while your mic reaches the virtual cable and brings it back when
  you are quiet, so your voice is always on top. Choose how far down it goes; it
  needs the noise gate, or push-to-talk with **Music ignores push-to-talk**, to
  know when you talk. The settings page **Music folders** is now called
  **Music**.
- If MicMixer is running from another folder, such as an unpacked zip, Setup
  asks you to exit it before installing.
- **Starting MicMixer as administrator can no longer load a program other
  programs chose.** Any program could set environment variables that make .NET
  load its DLL into MicMixer, which then ran as administrator at sign-in without
  a prompt. MicMixer now starts as administrator through a small launcher that
  removes them first.
- A silent update on a standard Windows account, approved with an
  administrator's password, can close MicMixer and install. It used to fail
  every time.
