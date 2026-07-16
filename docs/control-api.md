# MicMixer local control API

MicMixer exposes its built-in music player to local companion applications over the
Windows named pipe `MicMixer.Control.v1`. The server is part of `MicMixer.exe`; it is
not a Windows service and does not listen on a network port. `CurrentUserOnly` pipe
security restricts access to processes running as the same Windows user.

Protocol version 1 uses one compact JSON object per UTF-8 line. Requests have this
shape:

```json
{"id":"unique-request-id","command":"getState","protocolVersion":1}
```

Responses use the same request id. State changes are also pushed as `stateChanged`
events while a client is connected. Clients should begin with `hello` and reject an
unsupported protocol version.

## Commands

| Command | Payload |
| --- | --- |
| `hello`, `getState`, `getFolders`, `refreshLibrary` | none |
| `getTracks` | `search?`, `folderId?`, `offset?`, `limit?` (maximum 500) |
| `addMusicFolder` | absolute `path` to an existing folder |
| `removeMusicFolder`, `setDownloadFolder` | `folderId` |
| `resetMusicFolders` | none; restores the default MicMixer music folder |
| `switchToLibraryMode` | none |
| `playTrack`, `enqueueTrack` | `trackId` |
| `togglePlayPause`, `stop`, `previous`, `next` | none |
| `seek` | `positionSeconds` |
| `setMusicVolume`, `setMonitorVolume` | `volume` from 0 to 1 |
| `setVolumesLinked` | `linked` boolean; mirrors MicMixer's volume-sync toggle |
| `setMusicIgnoresPushToTalk` | `enabled` boolean; music keeps flowing to the cable while push-to-talk holds the mic silent |
| `setMusicMonitorOnly` | `enabled` boolean; preview mode — music is never sent to the cable, only local monitoring (and the secondary output) carry it |
| `removeQueueItem` | zero-based `index` |
| `moveQueueItem` | zero-based `fromIndex`, `toIndex` |
| `clearQueue` | none |
| `startDelayedPlay` | optional `trackId`; arms the countdown for that track instead of the resume/selected default |
| `cancelDelayedPlay` | none |
| `setDelayedStartSeconds` | `seconds` (clamped to MicMixer's supported range) |
| `setSingleTrackMode` | `mode`: `Off`, `Once`, or `Always` |
| `downloadFromUrl` | absolute HTTP(S) `url`, optional destination `folderId` |

State includes `volumesLinked`, the volume-sync toggle that makes one volume slider
follow the other. When it is enabled, a single `setMusicVolume` or `setMonitorVolume`
also moves the other volume, so companions should send only the volume the user
changed and read both back from state.

State also includes `musicIgnoresPushToTalk` and `musicMonitorOnly`, mirroring the
music-routing toggles in MicMixer's music card. Unlike the transport commands these
work in external capture mode as well, since they control routing rather than
playback. `musicMonitorOnly` overrides `musicIgnoresPushToTalk`: while it is on,
nothing from the music branch reaches the virtual cable.

The music-routing state fields and commands are advertised through the
`musicRouting` capability in the `hello` response. The protocol version stays 1;
companions must check the capability list and hide or disable their music-routing
controls when it is missing, since an older MicMixer rejects the commands as
unknown.

`getState` and `getFolders` expose every music folder configured in MicMixer,
including its full path, display name, default-folder flag, preferred download target,
and currently effective download target. `getTracks` searches the combined catalog
from all of those folders and each track identifies its source folder.

Track ids are opaque catalog ids. This only means that playback commands select an
entry from MicMixer's complete configured catalog instead of accepting an unchecked
path to an arbitrary MP3 file. Folder-management commands intentionally accept and
validate folder paths, so a StreamDecky UI can add and remove the same library folders
as MicMixer's own UI.

Direct transport is limited to MicMixer's built-in library. External capture mode can
be detected through state and changed with `switchToLibraryMode`; its media-key based
transport is intentionally not presented as direct control.

The server accepts one companion connection at a time and returns to its accept loop
after a disconnect. A companion should reconnect with backoff when MicMixer is not
running or is restarted.
