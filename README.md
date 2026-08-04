# Lidarr.Plugin.XmPlaylist

A Lidarr import list plugin that discovers artists from the [xmplaylist.com](https://xmplaylist.com) SiriusXM radio play feed and adds them to your Lidarr library.

## How It Works

1. Polls `https://xmplaylist.com/api/feed` every 30 minutes
2. Extracts artists from recent SiriusXM radio plays
3. Adds discovered artists to Lidarr as import list entries
4. Lidarr resolves artists by name and adds them to your library

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
| List Mode | Recent Plays (All Channels) | How the list is built. `Recent Plays` scans the global `/api/feed` across all channels; `Specific Channel` pulls one channel's plays from `/api/station/{channel}`. |
| Channel | *(empty)* | SiriusXM channel ID (e.g. `altnation`, `xmu`, `thespectrum`). Required when List Mode is `Specific Channel`. |
| Channel Filter | *(all)* | Optional comma-separated channel IDs to restrict results when List Mode is `Recent Plays` (e.g. `altnation, xmu`). |
| Import Type | Artists | What to import per play: `Artists` (artist name only), `Albums` (artist + track as album), or `Artists and Albums`. |
| Result Count | 200 | Number of recent plays to fetch (1-1000). |
| Dedupe Artists | on | Only add each unique artist once per fetch (recommended). |
| Only New Artists | on | Only emit artists not seen in a previous refresh. Each list tracks its own seen-state on disk, so artists are added once and not re-imported on subsequent polls. |

> The list refreshes every **6 hours** (matching Lidarr's Custom import list). The feed is cached server-side for 2 minutes, so more frequent polling yields no new data.

## Multiple Lists & API Limiting

If you add several import lists (e.g. one per channel), the plugin keeps API usage to a minimum:

- **Shared response cache** — all lists in the same Lidarr instance share an in-process cache keyed by URL. If two lists hit the same endpoint (e.g. both use the global feed), only **one** HTTP request is made, and results are reused for up to 3 minutes.
- **Feed mode is 1 request total** — to monitor N channels, prefer `List Mode: Recent Plays` + `Channel Filter` over N separate `Specific Channel` lists. The single `/api/feed` request is fetched once (shared cache) and filtered client-side for each list — N lists = 1 API hit.
- **`Specific Channel` mode = 1 request per channel** — each channel list hits `/api/station/{channel}` independently. Use sparingly (e.g. only for channels you want strongly scoped), since it scales linearly with the number of lists.
- **Only New Artists diffing** — each list persists a seen-artist set to `Lidarr/AppData/XmPlaylist/list-{id}.json`. On the 6-hour poll it only emits artists not seen before, so high-rotation artists are added once and not re-processed every cycle.

**Recommended setup for many channels:** one global list (`Recent Plays`, no filter) for broad discovery, plus a small number of `Specific Channel` lists only for channels where you want precise tracking.

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
│       ├── XmPlaylistRequestGenerator.cs      # /api/feed + /api/station request builder
│       ├── XmPlaylistParser.cs               # xmplaylist JSON → ImportListItemInfo
│       ├── XmPlaylistFeedCache.cs            # shared in-process HTTP cache (limits API hits)
│       └── XmPlaylistStateStore.cs           # per-list seen-artist state on disk
├── tests/XmPlaylist.Tests/                    # parser/diffing/channel-filter tests
├── Submodules/Lidarr/                         # Lidarr source (git submodule)
└── XmPlaylist.sln
```

## API Usage Notes

- The xmplaylist `/api/feed` endpoint is **free and requires no authentication**
- A `User-Agent` header is required per the API guidelines
- The feed is cached for 2 minutes by xmplaylist's CDN
- Lidarr matches artists by name, so accuracy depends on consistent artist naming

## License

MIT
