# What's changed

<!-- Update this list together with user-visible changes under src/. -->

- The secondary output now absorbs normal clock drift by resampling instead of
  periodically dropping audio or inserting silence. The primary and secondary
  devices run on independent clocks, but playback now runs a fraction of a percent
  fast or slow to keep their buffer level steady, so recordings and streams stay
  continuous. A genuine underflow still triggers one re-buffer while the cushion
  is restored.

- Per-app audio capture now gives Windows a bounded time to complete activation.
  If activation does not finish, MicMixer reports an error instead of remaining
  in a starting state indefinitely.
