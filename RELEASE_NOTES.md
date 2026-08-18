# What's changed

<!-- Update this list together with user-visible changes under src/. -->



- Music downloads work again. YouTube started rejecting the media links MicMixer
  received, so every download failed with "HTTP Error 403: Forbidden". MicMixer now
  installs a small JavaScript runtime alongside its other tools and asks YouTube for
  the links in a way that is still served, falling back to the previous method for
  videos that need it. The runtime is a one-time download of about 40 MB.

