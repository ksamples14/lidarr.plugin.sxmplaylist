# Lidarr.Plugin.SXMPlaylist

**SXM Playlist** monitors SiriusXM channel play feeds via [xmplaylist.com](https://xmplaylist.com) and automatically adds the played artists to your Lidarr library. You create one import list per channel; the plugin handles discovery, album matching, and deduplication in the background.

> A Lidarr **nightly** branch is required — plugins are not supported on stable.

## Table of Contents

1. [Installation](#installation)
2. [Import List Specific Settings](#import-list-specific-settings)
3. [How It Works](#how-it-works)
4. [Album Resolution](#album-resolution)
5. [Channel List](#channel-list)
6. [Companion Plex Playlists](#companion-plex-playlists)
7. [Play History](#play-history)
8. [Multiple Lists & API Limiting](#multiple-lists--api-limiting)
9. [Troubleshooting](#troubleshooting)
10. [Building From Source](#building-from-source)
11. [API Usage Notes](#api-usage-notes)
12. [Roadmap](#roadmap)

## Installation

### Prerequisites

- Lidarr **nightly** branch (plugins not supported on stable)
- .NET 8.0 SDK (only if building from source)

### From GitHub Releases

1. In Lidarr, go to **Settings → Plugins**
2. Paste `https://github.com/ksamples14/lidarr.plugin.sxmplaylist` into the GitHub URL box
3. Click **Install**
4. **Restart Lidarr**

### After Installation

1. Go to **Settings → Import Lists → Add (+)**
2. Select **SXM Playlist** from the list
3. The import list modal shows three sections:
   - **General Import List Settings** — name, enable, etc. (provided by Lidarr)
   - **Added Artist Settings** — monitor, search, quality/metadata profile, root folder (provided by Lidarr)
   - **Import List Specific Settings** — the xmplaylist controls below
4. Click **Test** to verify, then **Save**

## Import List Specific Settings

| Setting | Default | Description |
|---------|---------|-------------|
| Channel | *(empty, required)* | A dropdown of every SiriusXM channel xmplaylist tracks (e.g. "36 - Alt Nation"). Multiple lists can target the same channel if each selects a different show. |
| Show | Channel | Optional show filter from SiriusXM EPG data. Leave as Channel to import the full channel. |
| Require MusicBrainz ID | No | Only import albums that already have a MusicBrainz album ID. Title-only fallbacks can still be retried later. |
| Minimum Plays | 1 | Minimum number of times a track must have aired in the selected history window before its album is imported. |
| Albums Per Day | 24 | Maximum new albums added per day, spread across 24 hours. Set to `0` for unlimited. |
| Release Priority | Singles | When both single and album releases exist for a recording, choose which release type to prefer. |
| Companion Plex Playlist | No | Create and maintain a Plex playlist mirroring this import list's recent plays. Requires a Plex connection in Lidarr's Settings > Connect. |
| Plex Playlist History Days | 1 | Number of days of play history to include in the companion Plex playlist, capped by the 1-year local retention window. |

The Channel dropdown populates itself automatically whenever you open the Add/Edit dialog — no separate button to press. It's backed by a small cache (see [Channel List](#channel-list) below) so opening the dialog doesn't hit xmplaylist.com every time.

> The background worker captures each channel roughly every **1 hour**, and the import lists present resolved tracks to Lidarr hourly (capped by the Albums Per Day budget). Resolution happens in the background, so imports arrive spread across the day rather than in bursts. Artist monitoring, quality profile, and root folder are configured in Lidarr's own **Added Artist Settings** section of the import list modal, not by this plugin. Whether a previously-unmonitored artist gets re-monitored when it shows up in the feed again is controlled by Lidarr's own **Monitor Existing** setting on the list — not by this plugin.

## How It Works

SXM Playlist runs a background worker that checks each configured channel about once an hour, resolves played songs to albums via Deezer/MusicBrainz (with an Apple Music fallback), and makes the resolved tracks available to Lidarr's import system. New artists typically appear in your library within a few hours of first airing.

1. **Capture.** The worker checks each channel's play feed every hour and records the plays it hasn't seen before.
2. **Resolve.** Each new track's album is looked up in the background, retried up to 3 times before giving up. Before resolution, the worker checks linkless fuzzy plays (no Deezer link) against the library and skips songs already owned; plays carrying a Deezer link are exact-capable and skip that check, resolving via the ISRC path to their exact album.
3. **Present.** Lidarr polls each import list hourly; the list returns resolved tracks (ordered by last-play time, capped by the Albums Per Day budget), which Lidarr adds to your library. Duplicate plays are skipped. A background pass later verifies each presented album actually landed in Lidarr monitored/on-disk, so unconfirmed albums are re-presented instead of silently dropped.
4. **Playlist.** If enabled, the worker periodically mirrors recent plays from that list into a companion Plex playlist.

<details>
<summary>How the feed is captured (technical detail)</summary>

The channel endpoint only returns a few minutes of history per page, so each capture pages backwards through the API to cover approximately the last 2 hours — slightly wider than the hourly cadence so a missed poll doesn't lose plays (capped at 50 pages).
</details>

## Album Resolution

Most tracks resolve to a specific album automatically. The plugin only imports artists it can resolve to a MusicBrainz ID — this prevents Lidarr from creating duplicates via name-only matching. A small number of tracks (those with no Deezer or Apple Music link in the play data) may not resolve and won't be imported.

xmplaylist itself only ever gives a song title, never a real album name. Each play does include links to the same track on other services, though, and some of those are free public catalog data the plugin can use to find the actual album — no API keys, no login, nothing to configure:

1. **Deezer → MusicBrainz (preferred).** If the play has a Deezer link, the plugin looks up that track on Deezer's public API, which returns the track's ISRC (a unique code identifying that specific recording) and Deezer's own album title in one call. The ISRC is looked up on MusicBrainz — an exact match, not a fuzzy text search. When that succeeds, the plugin hands Lidarr the real album title and its MusicBrainz IDs, so Lidarr can add the album directly without a separate search.
2. **MusicBrainz recording search (fallback within the same call).** If the ISRC lookup misses, the plugin searches MusicBrainz by artist + song title (`/ws/2/recording`), recovering recordings the exact ISRC path couldn't find. Artist names are pre-processed (year/edition suffix stripping, fragmented-name reconstruction) to improve the match.
3. **MusicBrainz release-group title search (fallback).** Using the album title Deezer (or Apple, below) already returned, the plugin searches MusicBrainz release-groups by title with an `official` status boost, then gates the result on a fuzzy artist-credit match — so a same-titled album by a different artist can't be attached. This returns a real MusicBrainz album ID when confident.
4. **Deezer's own title / Apple Music title (title floor).** If none of the MusicBrainz paths succeed, the plugin falls back to the album title Deezer or Apple already returned — no extra network call. Apple's iTunes Lookup API is used only when there's no Deezer link at all, or Deezer's response had no album title.
5. **Skip (final fallback).** If nothing resolves, the track is not imported. Each track gets up to 3 resolution attempts across worker passes, and unresolved-but-presentable tracks are re-attempted on a staggered retry schedule; if all fail it stops being retried (it only returns if the song airs again with new links).

Resolution runs per release priority (Single vs Album) and entirely in the background, at MusicBrainz's 1 request/second limit. MusicBrainz's API is rate-limited per their usage policy; the plugin throttles to that automatically and retries transient 503 "server busy" responses a couple of times with a short backoff. Results are stored per track and priority (stations replay the same songs frequently and a track's album never changes between plays), so only genuinely new tracks ever hit the catalogs.

## Channel List

xmplaylist's own frontend derives its channel picker from `/api/station` (distinct from the per-channel `/api/station/{channel}` play endpoint) — a free, unauthenticated list of every SiriusXM channel it tracks, with a display name and channel number alongside the deeplink used to build the play-fetching URL. This plugin uses the same endpoint to populate the Channel dropdown.

The fetched list is cached (in the same history database) rather than re-fetched every time the Add/Edit dialog opens. It's treated as stale after 24 hours — SiriusXM's lineup doesn't change often — at which point the next dialog open refreshes it automatically before serving the dropdown. If that refresh fails (network hiccup, xmplaylist down), the plugin falls back to whatever was cached rather than leaving the dropdown empty.

## Companion Plex Playlists

When **Companion Plex Playlist** is enabled on an import list, the background worker builds a Plex playlist from that list's recent SXM play events. The playlist uses newest plays first and collapses duplicate Plex rating keys so repeated airings do not produce repeated playlist entries.

The plugin finds Plex configuration through Lidarr's normal Connect settings. It talks to Plex through Plex HTTP APIs and does not read Plex's database directly. If Plex Home or shared users have access to the music library, the plugin can fan out playlist copies for those users. Plex.tv account discovery is cached for 24 hours, and companion playlist syncs run about every 6 hours.

**Album-aware matching.** When a track has multiple versions in Plex (e.g. the same song on different compilations or albums), the plugin uses the album title from the SXM resolution to prefer the correct version. If Plex has the track on the matching album, that version is selected over earlier text-only matches. When no Plex track carries the expected album, the plugin falls back to the first title+artist text match.

**Recording MBID verification.** When the SXM resolver found a MusicBrainz recording ID (via Deezer ISRC), the plugin fetches the Plex track's metadata to compare its `mbid://` GUID against the SXM recording. The comparison is recorded in the audit as `MbidMatchStatus` — `"verified"` when the recording IDs match, `"mismatch"` when they differ (the SXM play and the Plex library track are different recordings of the same song), or `"unavailable"` when no recording ID was available from the SXM resolution.

Each playlist sync records an audit row per SXM play considered in `PlexPlaylistTrackMatches`. Audit rows include SXM play identity, MusicBrainz recording/ISRC trace fields when known, Plex rating key, Plex artist/title/album/guid, match method, confidence, and MBID match status. These rows are retained for the same 1-year window as play history.

Plex search results are cached per import list in `PlexPlaylistState.TrackCacheJson` to avoid re-searching the same artist/title pair every sync. The cache stores the Plex rating key plus the Plex metadata used for audit rows. Older string-only cache rows are still readable and are upgraded naturally the next time a playlist is synced after a fresh Plex search.

## Play History

The plugin keeps a local SQLite database (`Lidarr/AppData/SXMPlaylist/history.db`) recording every play it sees, across all channel lists: artist, song, channel, and timestamp. Alongside the play history it keeps per-track resolution state (the album and MusicBrainz IDs once resolved, plus a 3-strike failure counter), per-channel "last captured" markers, companion Plex playlist state, and playlist match audit rows. This serves four purposes:

- **Dedup** — duplicate plays are skipped, so overlapping capture windows never duplicate an import.
- **Resolution queue** — each track is resolved by the background worker up to 3 times before it gives up; resolved tracks stay presentable to Lidarr until their album is verified in the library (no fixed expiry).
- **Plex playlists** — companion playlists use play history to mirror what aired on a channel or show.
- **Auditability** — playlist match rows explain which SXM play mapped to which Plex track, with match method and confidence.

Play rows older than **1 year** are pruned by the background worker, along with tracks, play events, show windows, and Plex playlist audit rows whose timestamps have fallen out of the window.

The database uses `System.Data.SQLite` — the same SQLite provider Lidarr itself ships with — referenced from `lib/` the same way as the other host DLLs, so no new native binary is bundled with the plugin (see [Building From Source](#building-from-source)).

The plugin does not assume Lidarr uses SQLite or Postgres internally, and it does not read Lidarr's application database directly. Its durable plugin state lives in its own SQLite database under `SXMPlaylist/history.db`.

## Multiple Lists & API Limiting

The background worker downloads `/api/station/{channel}` for each configured channel — one request per channel per capture, plus any backfill pages needed to cover the ~2-hour window (capped at 50 pages). Multiple lists for the same channel share an in-process response cache keyed by URL (3-minute lifetime), so overlapping captures don't double the request count. Album resolution runs at MusicBrainz's 1 request/second limit regardless of how many channels or lists are configured.

## Troubleshooting

- **No artists are appearing.** A brand-new channel populates gradually — album resolution runs in the background at MusicBrainz's 1 request/second limit, so give it a few hours. Make sure the list is enabled and "Automatic Add" is on.
- **Some tracks never import.** Tracks with no Deezer or Apple Music link in the play data can't be resolved to an album and are skipped by design.
- **The wrong artist or album shows up occasionally.** When a track can't be resolved to a MusicBrainz album ID, Lidarr falls back to its own name-based search, which can rarely pick a different artist sharing the same name.
- **The plugin isn't listed in Lidarr.** Plugins require the **nightly** branch; they aren't supported on stable.

## Building From Source

The plugin references the **exact Lidarr DLLs from your running Lidarr instance** (in `lib/`), not the source submodule. This ensures the compiled assembly versions match the host, which is required for the plugin's isolated load context to resolve them.

```powershell
git clone --recursive https://github.com/ksamples14/lidarr.plugin.sxmplaylist.git
cd lidarr.plugin.sxmplaylist
dotnet restore SXMPlaylist.sln
dotnet build SXMPlaylist.sln -c Release -p:EnableAnalyzers=false
dotnet run --project tests/SXMPlaylist.Tests/SXMPlaylist.Tests.csproj
```

> `-p:EnableAnalyzers=false` avoids StyleCop/TreatWarningsAsErrors issues from Lidarr's build props.
>
> **Important:** the `lib/*.dll` files are the Lidarr assemblies from the container you deploy to (`v3.1.3.4987`). If you update Lidarr to a new nightly, re-extract the DLLs from your container:
> `docker cp <container>:/app/lidarr/bin/Lidarr.Core.dll lib/` (also `Lidarr.Common.dll`, `Lidarr.Http.dll`) and rebuild.
>
> `lib/System.Data.SQLite.dll` is the one exception — it doesn't need to come from your container. Lidarr pins `System.Data.SQLite` version `2.0.3` (see `Submodules/Lidarr/src/NzbDrone.Common/Lidarr.Common.csproj`), a normal NuGet package, so you can pull the exact same file from your NuGet cache (`~/.nuget/packages/system.data.sqlite/2.0.3/lib/netstandard2.0/System.Data.SQLite.dll`) instead of extracting it from Docker. Only re-check this if Lidarr ever bumps that package version.

Output: `src/SXMPlaylist/bin/Release/net8.0/Lidarr.Plugin.SXMPlaylist.dll` (single merged DLL via ILRepack)

## Project Structure

```
lidarr.plugin.sxmplaylist/
├── lib/                                    # Lidarr DLLs from your running instance (version-matched)
├── src/SXMPlaylist/
│   ├── SXMPlaylist.csproj                   # References lib/*.dll (Reference, Private=false)
│   ├── ILRepack.targets                    # Merges plugin into single DLL
│   ├── Plugin.cs                            # IPlugin entry point; hosts the background worker
│   ├── PluginInfo.cs                        # Plugin metadata constants
│   ├── PluginInfo.targets                   # Version/build metadata properties
│   ├── PreBuild.targets                     # Lidarr submodule init (IDE only)
│   └── ImportLists/
│       ├── SXMPlaylistImport.cs                # thin HttpImportListBase; Fetch() queries the DB
│       ├── SXMPlaylistImportSettings.cs        # [FieldDefinition] + validation
│       ├── SXMPlaylistWorker.cs                # background worker: capture + resolve + prune
│       ├── SXMPlaylistRequestGenerator.cs      # /api/station/{channel} request builder
│       ├── SXMPlaylistRequestBuilder.cs        # shared HttpRequest construction (headers)
│       ├── SXMPlaylistStationBackfill.cs       # cursor pagination to cover the capture window
│       ├── SXMPlaylistFeed.cs                  # xmplaylist feed JSON models
│       ├── SXMPlaylistHistoryStore.cs          # SQLite: plays, tracks, Plex state/audit (WAL)
│       ├── SXMPlaylistAlbumResolver.cs         # Deezer/MusicBrainz/Apple album lookup
│       ├── SXMPlaylistPlexClient.cs            # Plex matching, playlist sync, fan-out
│       ├── SXMPlaylistTitleNormalizer.cs       # shared title suffix stripping
│       ├── SXMPlaylistRefreshScheduler.cs      # RefreshArtistCommand after newly-monitored albums
│       ├── SXMPlaylistChannelDirectory.cs      # /api/station channel list lookup
│       └── SXMPlaylistFeedCache.cs            # shared in-process HTTP cache (limits API hits)
├── tests/SXMPlaylist.Tests/                    # worker, history-store, backfill, resolver, and scheduler tests
├── Submodules/Lidarr/                         # Lidarr source (git submodule)
└── SXMPlaylist.sln
```

## API Usage Notes

- The xmplaylist `/api/station/{channel}` endpoint is **free and requires no authentication**
- A `User-Agent` header is required per the API guidelines
- Each page covers only a few minutes of history (~24 plays), which is why the plugin pages backwards to backfill the ~2-hour capture window instead of relying on a single page
- Lidarr matches artists (and unresolved albums) by name, so accuracy depends on consistent naming
- Deezer's API (`api.deezer.com`), Apple's iTunes Lookup API (`itunes.apple.com`), and MusicBrainz's web service (`musicbrainz.org/ws/2`) are all free and require no authentication either — see [Album Resolution](#album-resolution)
- `/api/station` (no channel suffix) is a separate endpoint from `/api/station/{channel}` — it lists every channel instead of that channel's plays — see [Channel List](#channel-list)
- Plex playlist sync uses the Plex server API and Plex.tv account APIs when fan-out to shared/Home users is needed. Plex.tv user discovery is cached for 24 hours; playlist track search results are cached per import list.

## Roadmap

- **Plex match confidence improvements.** Use the collected playlist audit data to improve matching where Plex exposes enough portable identity data. Current Plex GUIDs are generally opaque `plex://track/...` values, so MusicBrainz-aware matching remains a future enhancement rather than an assumption.
- **Additional metadata providers (Last.fm, Discogs).** Album resolution currently tries Deezer/MusicBrainz then Apple. Adding more sources would catch tracks neither of those has, especially lesser-known or non-mainstream artists.

## License

MIT
