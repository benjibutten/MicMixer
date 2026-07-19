# MicMixer OBS overlay

When OBS captures a game as an individual process (Game Capture), desktop
overlays such as MicMixer's status indicator are not part of the captured
image, so viewers never see them. The OBS overlay solves this: MicMixer serves
the same overlay as a local web page that is added to OBS as a **Browser
source** and layered on top of the game.

The page mirrors the desktop overlay exactly — the mic status dot, the music
circle with its equalizer bars, and the optional level rings, with the same
colors, glyphs, meter behavior, and 72 % opacity. It also mirrors the hidden
state: while routing is stopped, the page renders nothing, so the stream
behaves like the desktop.

## Enabling

Check **OBS-overlay** in MicMixer's routing settings. MicMixer then starts a
small web server bound to `127.0.0.1` (default port 4573, configurable next to
the checkbox). The settings UI shows the overlay address, and the link can be
opened in a normal browser to test it.

The OBS overlay works independently of the on-screen overlay indicator: either
one, both, or neither can be enabled. The **Volume meter** and
**Sensitivity** settings apply to both overlays.

## OBS setup

1. In OBS, add a source to the scene: **Sources → + → Browser**.
2. Paste the overlay address, e.g. `http://127.0.0.1:4573/`.
3. Set the source size to the overlay's 2:1 aspect. The overlay is vector
   graphics, so any size stays sharp; multiples of the native 116×58 keep the
   proportions exact:

   | Width × Height | Fits |
   | --- | --- |
   | `232 × 116` | Discreet at 1080p — about the size of the desktop overlay. |
   | `348 × 174` | Clearly visible at 1080p; discreet at 1440p. |
   | `464 × 232` | Large at 1080p; comfortable at 1440p. |
   | `580 × 290` | 4K streams, or when the overlay should really stand out. |

   The source can also be resized freely in the preview afterwards (hold
   <kbd>Shift</kbd> to break the aspect if ever needed) — the page re-scales
   without quality loss.
4. Place the source where the overlay should appear, typically a top corner,
   above the Game Capture source.

The page background is transparent; OBS composites only the drawn indicators.
When MicMixer is closed or restarting, the page hides itself and reconnects
automatically with backoff — no OBS interaction needed.

### URL parameters

| Parameter | Effect |
| --- | --- |
| `?opacity=1` | Fully opaque indicators (default mirrors the desktop overlay's `0.72`). Any value from `0.05` to `1` works. |
| `?debug=1` | Dark page background and a connection status line, for testing outside OBS. |
| `?demo=1` | Shows a representative state with moving meters without connecting to MicMixer — useful for sizing and placing the Browser source while routing is stopped, since the live page shows nothing then. Optional `&mic=live\|modded\|muted`, `&music=sending\|monitorOnly\|blocked\|hidden`, and `&meter=0` pick the shown state. Remove the parameter afterwards. |

## Protocol

The page connects to `ws://127.0.0.1:<port>/ws` and receives one compact JSON
object per message. There are no client-to-server commands; the socket is a
one-way status feed.

State messages are pushed on connect and on every change:

```json
{"type":"state","mic":"live","music":"sending","meter":true,"sensitivityDb":0}
```

- `mic`: `hidden`, `live`, `modded`, or `muted` — mirrors the tray icon and the
  desktop overlay's mic dot. `hidden` means routing is stopped and the page
  must render nothing.
- `music`: `hidden`, `sending`, `monitorOnly`, or `blocked` — mirrors the
  desktop overlay's music circle.
- `meter`: whether the level rings may be shown.
- `sensitivityDb`: the meter calibration offset, applied by the page before
  mapping loudness onto the ring.

While at least one page is connected and the volume meter is enabled, level
frames are pushed on the same ~50 ms cadence that feeds the desktop overlay:

```json
{"type":"levels","op":0.42,"or":0.18,"mp":0.3,"mr":0.12}
```

`op`/`or` are the outgoing mix's sample peak and block RMS; `mp`/`mr` are the
music branch's. The page runs the same decay, peak-hold, and color-ramp math
as the desktop overlay, so both meters move identically.

## Performance and security

- Without connected pages the server is idle: no level messages are produced,
  and the audio-thread level metering stays disabled unless the desktop
  overlay needs it.
- Level frames are coalesced per client — a stalled OBS instance simply skips
  frames and can never build a queue or block MicMixer.
- The server listens only on the loopback interface and carries only the
  status strings and level numbers shown above — no audio, no video, and no
  control commands. Other machines on the network cannot reach it.
- WebSocket handshakes that carry a foreign `Origin` header are refused.
  Browsers exempt WebSockets from the same-origin policy, so without this an
  arbitrary web page could read the status feed through the user's browser;
  only the overlay page's own origin (and clients without an `Origin`, such
  as native tools) may connect.
