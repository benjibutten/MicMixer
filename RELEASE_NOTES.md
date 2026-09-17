# What's changed

<!-- Update this list together with user-visible changes under src/. -->


- **Reconnected devices are picked up by themselves.** While routing is off,
  MicMixer notices a device appearing or disappearing and updates the device
  lists and the cards, so a card about an unplugged device clears itself once the
  device is back — no **Refresh devices** click needed. While routing is on the
  card stays, because the route keeps using the device it started with.
- Music that paused because the monitoring device was unplugged resumes when
  that device is back. It never moves to another device by itself.
- **Enable** no longer has to be clicked again when it lands while MicMixer is
  reading the audio devices; routing starts as soon as they are read.
