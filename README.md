# Lidarr.Plugin.XmPlaylist

A Lidarr import list plugin that discovers artists from the [xmplaylist.com](https://xmplaylist.com) SiriusXM radio play feed and adds them to your Lidarr library.

## How It Works

1. Each list is scoped to one SiriusXM channel and polls `https://xmplaylist.com/api/station/{channel}` every 6 hours
2. The channel endpoint only returns a channel's most recent ~24 plays (a few minutes of history) per page, so each poll walks the API's `next` cursor backwards, merging pages until it has covered the full 6-hour window since the last poll (capped at 50 pages as a safety limit)
3. Every play is recorded in a local SQLite history database (artist, song, channel, timestamp) — see [Play History](#play-history) below
4. For plays not already in that history, the plugin tries to resolve the real album (see [Album Resolution](#album-resolution) below) and adds an artist import list entry to Lidarr
5. Lidarr resolves artists (and, when resolved, albums) by name and adds them to your library

## Album Resolution

xmplaylist itself only ever gives a song/track title, never a real album name. Each play does include links to the same track on other services, though, and some of those are free public catalog data the plugin can use to find the actual album — no API keys, no login, nothing to configure:

1. **Deezer → MusicBrainz (preferred).** If the play has a Deezer link, the plugin looks up that track on Deezer's public API, which returns both the track's ISRC (a unique code identifying that specific recording) and Deezer's own album title in one call. The ISRC is looked up on MusicBrainz — an exact match, not a fuzzy text search — and when that succeeds, the plugin hands Lidarr the real album title *and* its MusicBrainz IDs directly, so Lidarr trusts those outright and skips its own search.
2. **Deezer's own title (fallback within the same call).** If the MusicBrainz step doesn't pan out (no ISRC, no match, or nothing usable), the plugin falls back to the album title Deezer already returned — no extra network call, since that response was already fetched for the ISRC.
3. **Apple Music (last-resort fallback).** Only used when there's no Deezer link at all, or Deezer's response had no album title either. Uses Apple's iTunes Lookup API via the Apple Music link's album ID. Real album title, no MusicBrainz ID — Lidarr resolves it the normal way via its own built-in fuzzy album search.
4. **Artist-only (final fallback).** If nothing resolves, the play is imported as an artist only, same as before this feature existed.

Resolved albums are cached per song (in the same history database, keyed by xmplaylist's own track id) rather than per play, since a rotation-heavy station replays the same songs constantly and the album never changes between plays. A failed lookup is retried after 7 days rather than cached forever, in case the track shows up on Deezer/Apple later. MusicBrainz's API is rate-limited to 1 request/second per their usage policy; the plugin throttles to that automatically, and since lookups are cached per song, only genuinely new songs ever hit it.

## Installation

### Prerequisites

- Lidarr **nightly** branch (plugins not supported on stable)
- .NET 8.0 SDK (only if building from source)

### From GitHub Releases

1. In Lidarr, go to **Settings → Plugins**
2. Paste `https://github.com/ksamples14/lidarr.plugin.xmplaylist` into the GitHub URL box
3. Click **Install**
4. **Restart Lidarr**

### After Installation

1. Go to **Settings → Import Lists → Add (+)**
2. Select **XM Playlist** from the list
3. The import list modal shows three sections:
   - **General Import List Settings** — name, enable, etc. (provided by Lidarr)
   - **Added Artist Settings** — monitor, search, quality/metadata profile, root folder (provided by Lidarr)
   - **Import List Specific Settings** — the xmplaylist controls below
4. Click **Test** to verify, then **Save**

## Import List Specific Settings

| Setting | Default | Description |
|---------|---------|-------------|
| Channel | *(empty, required)* | A dropdown of every SiriusXM channel xmplaylist tracks (e.g. "36 - Alt Nation"). One list = one channel — add one import list per channel you want to monitor. |

The Channel dropdown populates itself automatically whenever you open the Add/Edit dialog — no separate button to press. Behind the scenes it's backed by a small cache (see [Channel List](#channel-list) below) so opening the dialog doesn't hit xmplaylist.com every time.

> The list refreshes every **6 hours** (matching Lidarr's Custom import list). Each poll backfills the full 6-hour window via cursor pagination rather than relying on a single snapshot, so plays aren't missed between polls. Artist monitoring, quality profile, and root folder are configured in Lidarr's own **Added Artist Settings** section of the import list modal, not by this plugin. Whether a previously-unmonitored artist gets re-monitored when it shows up in the feed again is controlled by Lidarr's own **Monitor Existing** setting on the list — not by this plugin.

## Channel List

xmplaylist's own frontend derives its channel picker from `/api/station` (distinct from the per-channel `/api/station/{channel}` play endpoint) — a free, unauthenticated list of every SiriusXM channel it tracks, with a display name and channel number alongside the deeplink used to build the play-fetching URL. This plugin uses the same endpoint to populate the Channel dropdown.

The fetched list is cached (in the same history database) rather than re-fetched every time the Add/Edit dialog opens. It's treated as stale after 24 hours — SiriusXM's lineup doesn't change often — at which point the next dialog open refreshes it automatically before serving the dropdown. If that refresh fails (network hiccup, xmplaylist down), the plugin falls back to whatever was cached rather than leaving the dropdown empty.

> Lidarr's plugin settings framework doesn't support a standalone clickable button for this kind of thing — `FieldType.Action` exists in Lidarr's own code but isn't wired to anything in its frontend. The dropdown-refreshes-on-open behavior above is the closest available mechanism.

## Play History

The plugin keeps a local SQLite database (`Lidarr/AppData/XmPlaylist/history.db`) recording every play it sees, across all channel lists: artist, song, channel, and timestamp (plus a small cache of resolved albums, see [Album Resolution](#album-resolution)). This does two things:

- **Dedup** — a play is only sent to Lidarr the first time it's recorded. Since each poll backfills the full 6-hour window, two consecutive polls can legitimately overlap by a few minutes; the history table (keyed by the API's own play ID + artist) makes sure that overlap never produces a repeat import.
- **Future feature** — this is also the data source for a planned "build a Plex playlist per station" feature, which needs the actual song-level play history (not just "have we seen this artist"), so it's kept independently of whatever Lidarr decides to do with the artist.

Play rows older than 90 days are pruned automatically on each poll. Successful album resolutions are kept for the same 90 days; failed ones are retried after 7 days.

The database uses `System.Data.SQLite` — the same SQLite provider Lidarr itself ships with — referenced from `lib/` the same way as the other host DLLs, so no new native binary is bundled with the plugin (see [Building From Source](#building-from-source)).

## Multiple Lists & API Limiting

Each list hits `/api/station/{channel}` for its own channel — one request per poll, plus any backfill pages needed to cover the 6-hour window (capped at 50 pages). If you run multiple lists against the same channel, they share an in-process response cache keyed by URL (3-minute lifetime), so overlapping polls don't double the request count.

## Building From Source

The plugin references the **exact Lidarr DLLs from your running Lidarr instance** (in `lib/`), not the source submodule. This ensures the compiled assembly versions match the host, which is required for the plugin's isolated load context to resolve them.

```powershell
git clone --recursive https://github.com/ksamples14/lidarr.plugin.xmplaylist.git
cd lidarr.plugin.xmplaylist
dotnet restore XmPlaylist.sln
dotnet build XmPlaylist.sln -c Release -p:EnableAnalyzers=false
dotnet run --project tests/XmPlaylist.Tests/XmPlaylist.Tests.csproj
```

> `-p:EnableAnalyzers=false` avoids StyleCop/TreatWarningsAsErrors issues from Lidarr's build props.
>
> **Important:** the `lib/*.dll` files are the Lidarr assemblies from the container you deploy to (`v3.1.3.4987`). If you update Lidarr to a new nightly, re-extract the DLLs from your container:
> `docker cp <container>:/app/lidarr/bin/Lidarr.Core.dll lib/` (also `Lidarr.Common.dll`, `Lidarr.Http.dll`) and rebuild.
>
> `lib/System.Data.SQLite.dll` is the one exception — it doesn't need to come from your container. Lidarr pins `System.Data.SQLite` version `2.0.3` (see `Submodules/Lidarr/src/NzbDrone.Common/Lidarr.Common.csproj`), a normal NuGet package, so you can pull the exact same file from your NuGet cache (`~/.nuget/packages/system.data.sqlite/2.0.3/lib/netstandard2.0/System.Data.SQLite.dll`) instead of extracting it from Docker. Only re-check this if Lidarr ever bumps that package version.

Output: `src/XmPlaylist/bin/Release/net8.0/Lidarr.Plugin.XmPlaylist.dll` (single merged DLL via ILRepack)

## Project Structure

```
lidarr.plugin.xmplaylist/
├── lib/                                    # Lidarr DLLs from your running instance (version-matched)
├── src/XmPlaylist/
│   ├── XmPlaylist.csproj                   # References lib/*.dll (Reference, Private=false)
│   ├── ILRepack.targets                    # Merges plugin into single DLL
│   ├── Plugin.cs                            # IPlugin entry point
│   ├── PluginInfo.cs                        # Plugin metadata constants
│   ├── PluginInfo.targets                   # Version/build metadata properties
│   ├── PreBuild.targets                     # Lidarr submodule init (IDE only)
│   └── ImportLists/
│       ├── XmPlaylistImport.cs                # HttpImportListBase<TSettings>
│       ├── XmPlaylistImportSettings.cs        # [FieldDefinition] + validation
│       ├── XmPlaylistRequestGenerator.cs      # /api/station/{channel} request builder
│       ├── XmPlaylistRequestBuilder.cs        # shared HttpRequest construction (headers)
│       ├── XmPlaylistStationBackfill.cs       # cursor pagination to cover the 6h poll window
│       ├── XmPlaylistParser.cs               # xmplaylist JSON → ImportListItemInfo
│       ├── XmPlaylistHistoryStore.cs          # SQLite play history + dedup + album/channel cache
│       ├── XmPlaylistAlbumResolver.cs         # Deezer/MusicBrainz/Apple album lookup
│       ├── XmPlaylistChannelDirectory.cs      # /api/station channel list lookup
│       └── XmPlaylistFeedCache.cs            # shared in-process HTTP cache (limits API hits)
├── tests/XmPlaylist.Tests/                    # parser, backfill-cursor, history-store, and album-resolver tests
├── Submodules/Lidarr/                         # Lidarr source (git submodule)
└── XmPlaylist.sln
```

## API Usage Notes

- The xmplaylist `/api/station/{channel}` endpoint is **free and requires no authentication**
- A `User-Agent` header is required per the API guidelines
- Each page covers only a few minutes of history (~24 plays), which is why the plugin walks the `next` cursor to backfill the full 6-hour poll window instead of relying on a single page
- Lidarr matches artists (and unresolved albums) by name, so accuracy depends on consistent naming
- Deezer's API (`api.deezer.com`), Apple's iTunes Lookup API (`itunes.apple.com`), and MusicBrainz's web service (`musicbrainz.org/ws/2`) are all free and require no authentication either — see [Album Resolution](#album-resolution)
- `/api/station` (no channel suffix) is a separate endpoint from `/api/station/{channel}` — it lists every channel instead of that channel's plays — see [Channel List](#channel-list)

## Roadmap

- **Plex playlist per station.** The play-history database already records every song, artist, channel, and timestamp specifically to support this — build a playlist per SiriusXM channel from that history so what's playing on a station in Lidarr's library can be listened to as a Plex playlist.
- **Additional metadata providers, following Tubifarry's pattern.** Tubifarry (the other Lidarr plugin already running alongside this one, see `Metadata/Proxy/MetadataProvider/`) has working, precedented integrations with Last.fm, Discogs, and a "mixed" proxy that layers multiple sources together. Album resolution here currently only tries Deezer/MusicBrainz then Apple; adding more sources the same way would catch tracks neither of those has, especially lesser-known or non-mainstream artists.

## License

MIT
