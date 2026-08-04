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
| Import Type | Artists | What to import per play: `Artists` (artist name only), `Albums` (artist + track as album), or `Artists and Albums`. |
| Result Count | 200 | Number of recent plays to fetch (1-1000). |
| Dedupe Artists | on | Only add each unique artist once per fetch (recommended). |

> The list refreshes every **6 hours** (matching Lidarr's Custom import list). The feed is cached server-side for 2 minutes, so more frequent polling yields no new data.

## Building From Source

```powershell
git clone --recursive https://github.com/ksamples14/lidarr.plugin.xmplaylist.git
cd lidarr.plugin.xmplaylist
dotnet restore XmPlaylist.sln
dotnet build XmPlaylist.sln -c Release -p:EnableAnalyzers=false
```

> `-p:EnableAnalyzers=false` is required because Lidarr's own build props enable StyleCop with `TreatWarningsAsErrors`, which fails on Lidarr's submodule source.

Output: `src/XmPlaylist/bin/Release/net8.0/Lidarr.Plugin.XmPlaylist.dll` (single merged DLL via ILRepack)

## Project Structure

```
lidarr.plugin.xmplaylist/
├── src/XmPlaylist/
│   ├── XmPlaylist.csproj
│   ├── ILRepack.targets                      # Merges Lidarr deps into single DLL
│   ├── Plugin.cs                              # IPlugin entry point
│   ├── PluginInfo.cs                          # Plugin metadata constants
│   ├── PluginInfo.targets                     # Version/build metadata properties
│   ├── PreBuild.targets                       # Lidarr submodule init
│   └── ImportLists/
│       ├── XmPlaylistImport.cs                # HttpImportListBase<TSettings>
│       ├── XmPlaylistImportSettings.cs        # [FieldDefinition] + validation
│       ├── XmPlaylistRequestGenerator.cs      # /api/feed request builder
│       └── XmPlaylistParser.cs               # xmplaylist JSON → ImportListItemInfo
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
