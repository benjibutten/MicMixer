# What's changed

<!-- Update this list together with user-visible changes under src/. -->

- **MicMixer has an installer.** It installs into Program Files and adds MicMixer
  to the Start menu and to Apps in Windows Settings, where it can be uninstalled.
  An installed MicMixer updates through the installer. The zip is still in every
  release, and copies extracted from it keep updating as before.
- **The installer can install VB-CABLE** when Windows has no VB-CABLE yet, and
  name its two ends **MicMixer Input** and **MicMixer Output**. VB-CABLE is made by
  VB-Audio Software (www.vb-cable.com) and is donationware; untick it to use
  another cable or install one yourself.
- **Run as administrator**, in the installer and in **Settings → General**. While
  a game or program running as administrator has focus, Windows keeps the hotkey
  from reaching MicMixer and drops the keys it holds; running MicMixer as
  administrator too lets them through. Installed with the installer and set to
  start with Windows, it starts as administrator at sign-in without asking.
- When a program running as administrator takes focus, the main window says so,
  with a button that restarts MicMixer as administrator.
- **Several hotkeys.** **Add hotkey** on the Hotkey page lets more keys or mouse
  buttons do exactly what the first one does, for example a game's own radio key.
- **Hold a key while sending.** MicMixer can hold one of F13–F24 down for as long
  as mic or music reaches the virtual cable. Bind another app's push-to-talk to
  that key and it transmits exactly when MicMixer does. **Send key once** helps
  bind a key that is not on your keyboard.
- **Save** in the settings window keeps your changes and closes the window.
- After an update, MicMixer shows what's new once, like this.
