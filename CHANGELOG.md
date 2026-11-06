# Sink 1.0.67

- Keep temporary downloads outside Music and only save completed tracks in the format selected in Settings. With **Keep high quality** enabled, Deezer FLAC originals go in the configured Originals folder.

# Sink 1.0.66

- Added **Storage breakdown** in Settings to compare library files, everything in Music, kept originals, and the connected iPod catalog.
- Folder imports and organization leave the configured Originals folder in place. The library size label counts files it could actually read, and the iPod tooltip shows device free space separately from catalog media.

# Sink 1.0.65

- Rank Download Search by relevance. Put the exact song on the requested artist's album first, followed by the rest of that album, exact song-title matches, other results from the requested artist, and partial matches.
- Expand the matching album into its tracks, and keep album editions distinct when combining catalog and video results.

# Sink 1.0.64

- Added **Sync with another PC** in Settings. Open it on both PCs to discover Sink installs on the local network and pair them over an encrypted connection.
- Preview and approve a two-way sync of the music library, playlists, podcasts, and Reflect history. Resolve concurrent edits or leave either copy as it is for now.
- Remember a shared baseline for each pair. Media paths are translated to each PC's configured folders, and syncing the databases leaves the audio files on disk.

# Sink 1.0.63

- Export and import listening history directly from Reflect. Imports merge with current history and accept older `reflect.json` files.

# Sink 1.0.62

- Reconnect podcasts recovered from a download folder to their feeds and restore show cover art when you open Podcasts. Use downloaded episode titles to distinguish shows with the same name.
- Keep the downloaded files, playback positions, and iPod preferences when reconnecting. The restored feed and artwork are saved with the portable podcast catalog.

# Sink 1.0.61

- Restore downloaded podcasts when moving the podcast folder to another computer. Sink saves the catalog beside the downloads, reattaches moved files, and recovers older downloads from their filenames.
- Sync Podcasts and Sync All remove iPod episodes that are no longer selected by the local podcast library. Sink checks that the podcast folder and device library are readable before reconciling them.
- Open a show under iPod Podcasts with View episodes and remove individual or selected episodes. Manually removed episodes stay off during future syncs until explicitly added back.

# Sink 1.0.60

- Fix Deezer downloads on fresh installs that saved Settings before downloading. Sink now initializes Streamrip's full configuration and repairs older incomplete files while preserving the saved Deezer login.

# Sink 1.0.59

- Right-click an album in download search to open its full track list, with individual include checkboxes and Download selected.
- Right-click an artist to browse its albums. Each album has an include checkbox and Download button; clicking its title or right-clicking opens its track selection popup.
- Choices survive closing/reopening popups for the current search, and existing search queue buttons respect those choices. Downloads use an independent selection snapshot so queue pruning cannot erase browsing selections; original playlist positions and unchecked tracks are retained for partial album downloads.
- Popup downloads start the selected set when idle, or queue it for the next set while another download runs. Lists load asynchronously with retry support.
- Corrected the parent checkbox state when an artist contains a partially selected album.

# Sink 1.0.58

- Added an optional "Sync when done" checkbox to Download Options, remembered between launches and off by default.
- After a download set finishes and library imports/playlists are saved, sync music and playlists to the connected iPod using the existing additive automatic sync. A busy device is allowed to finish first; stopped downloads, a disconnected device, or switching the option off skip the automatic sync.

# Sink 1.0.57

- Shuffle context menus read the current selection on every click and update their label immediately. The same menu item correctly toggles in both directions and follows external preference changes.
- Track icons and album/artist indicators update immediately even while an iPod operation is active. Connected-device writes are queued and coalesced; finishing an older write cannot discard a newer edit.
- Local, adapted iPod, and cached device models stay aligned, including when a device read finishes during a pending toggle.
- Added WPF interaction regression checks for real menu clicks, icon colors and tooltips, reopening menus, mixed selections, and toggling while device I/O is pending.

# Sink 1.0.56

- Podcast show cards display their downloaded episode count as `↓ 3` alongside the existing unplayed and total episode counts. The count includes only episode files that are present on disk.

# Sink 1.0.55

- Track lists and library home track cards show a small shuffle icon: purple when included, grey when excluded. Tooltips explain the state.
- Album and artist cards show a grey shuffle icon only when every track in the group is excluded, including compact list mode and library home cards. Included and mixed groups show no icon.
- Indicators refresh as soon as the shuffle setting changes. iPod album/artist actions use the device's own tracks.

# Sink 1.0.54

Based on GitHub `v1.0.53` (`c239d72`), including its idempotent Deezer short-link/recursion repair. This remains the Windows WPF application; the experimental iPhone and Avalonia/Linux work is not included.

## Podcasts

- Podcast episodes remain in the global iPod database and special Podcasts playlist, and are removed from the master music playlist and ordinary playlists. This keeps them out of the iPod's Songs, Artists, Albums, Genres and music playlists.
- Existing episodes are repaired when Sink loads the connected iPod, including episodes with no remaining local download. Sync changes also reloads and repairs the device. New podcast and playlist syncs preserve this separation.
- Repair preserves track IDs, media files, play counts and bookmarks. It backs up the database before saving, verifies the saved classification and membership, and restores the backup if repair fails.
- Episodes always skip music shuffle and remember playback position.

## Shuffle exclusion

- Exclude/include in shuffle works from the local library and the iPod's own music views. With a connected iPod, the preference is written immediately and checked against a fresh database read. With no connected iPod, the local preference is saved for the next sync.
- Flag-only changes explicitly mark the Clickwheel track dirty. Unsupported private fields now produce an error rather than silently dropping the edit.
- Sink reads the saved device setting for its menu labels, and its random unplayed-song picker respects exclusions.

## Build and verification

```powershell
dotnet build Sink.csproj -c Release
dotnet run --project Tests/Sink.RegressionTests
dotnet run --project Tests/Sink.UiRegressionTests
```

The regression runner creates isolated iPod 5-era databases and uses the real Clickwheel serialization code. It covers new and existing podcast separation, missing local files, idempotent repair, track/file/playback preservation, playlist sync, and shuffle flags surviving database reopening. An optional argument can point to an existing database backup for read-only source replay into an isolated copy.

Physical iPod menu and shuffle playback still need a device check after safe eject.
