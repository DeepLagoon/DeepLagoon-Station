# Jukebox TGUI

The compact horseshoe is painted by TGUI inside a frameless native host. Its wood grain is static CSS. The classic native menu remains a fallback if WebView fails or is not ready within eight seconds.

## Behavior

- Each machine owns its upcoming queue, volume and repeat mode (off / track / queue). Stop preserves the upcoming queue; Play restarts the selected track. An empty selection consumes the first queued track. Next overrides track repeat.
- Repeated tracks have distinct queue entry IDs. Move/remove/clear commands include a queue revision, so concurrent stale edits refresh the UI instead of editing the wrong entry. Maximum 256 upcoming entries.
- Favorites and named playlists belong to the authenticated account. They are saved on the server, not in the web document. Personal actions stay disabled until loading completes and while saving. Save errors leave existing data and editor drafts intact.
- Personal lists can be reordered. A complete playlist can be appended atomically when the queue has enough room. Missing track IDs are retained in personal lists and displayed as unavailable.
- Export/import uses a JSON object with `Favorites` (track IDs) and `Playlists` (name to array of IDs). Import merges favorites and adds uniquely named playlists. It rejects conflicting names, duplicate JSON keys, invalid shapes and limits before any write: 40 playlists, 35 characters per name, 256 tracks per playlist, 1024 favorites, 256 KiB JSON.
- The optional retro meter is decorative, not FFT audio analysis. It is off initially, pauses with playback, and honors reduced motion.

## Adapted MOLOT optimizations

The catalog and durations are cached and sent as static TGUI data. Open windows send only changed dynamic fields, sampled at 4 Hz; the page interpolates time locally. Slider drags commit on release instead of producing a message for each pointer event. Lists render at most 25 entries per page.

Audio stays on SS14's spatial PVS path. Its existing `AudioStart` handles catch-up for listeners entering range; there is no global BYOND-style sound-file broadcast. A reverse audio-owner index replaces scanning every jukebox for every terminating entity. Only active jukeboxes are checked for completion, at 5 Hz. Stop/power loss detach the stream before deletion so cleanup cannot restart playback.

## Migration and deployment

Both SQLite and PostgreSQL have additive migration `20261017120000_JukeboxLibrary`, adding `preference.jukebox_library_json` with `{}` for existing accounts. Back up the preferences database, then deploy the normal server/client/TGUI build together. The normal database startup applies migrations. No manual account-data conversion is required. Rollback of this migration removes personal jukebox lists; export them before a schema downgrade.

## In-game verification

Open the normal and wall-mounted jukebox; check the curved silhouette, drag/resize/close, Cyrillic text input and scrolling at minimum size. Add duplicate tracks and reorder from two clients; check natural completion, each repeat mode, skip, pause/resume, seek near the end, Stop, power loss and deletion. Approach a playing machine from outside PVS and verify catch-up and attenuation. Reconnect and open another machine to verify account lists; a second account must not see them. Test database failure/retry and WebView fallback. Browser and build checks do not establish these native/audio behaviors.
