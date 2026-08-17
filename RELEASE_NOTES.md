# What's changed

<!-- Update this list together with user-visible changes under src/. -->

- The secondary output now absorbs clock drift by resampling instead of dropping
  or inserting audio. The two devices it sits between run on independent clocks,
  and the old correction — dumping the oldest audio or holding silence whenever
  they had drifted far enough apart — put a break in the stream that recording and
  streaming software compensated for by adding up to a second of audio buffering.
  Playback now runs a fraction of a percent fast or slow to hold the level steady,
  so what comes out stays continuous.
