# What's changed

<!-- Update this list together with user-visible changes under src/. -->


- New **Noise gate** under push-to-talk: keeps the mic silent between phrases so
  the virtual cable carries true silence instead of room noise. Apps that use
  voice activation on the cable stop treating you as talking as soon as you
  stop speaking, even while the push-to-talk key is still held. Off by default.
- New **Volume** slider under the normal mic, for matching a quiet mic to a
  louder modified voice. 100% is the default and sends the mic exactly as
  before. The processed-voice volume moved next to it, under the modified-voice
  picker, and both use the same 0–200% scale.
