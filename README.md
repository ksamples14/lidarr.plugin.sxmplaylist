# SatList - XM Playlist Importer for Lidarr

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
2. Paste `https://github.com/ksamples14/sat-list` into the GitHub URL box
3. Click **Install**
4. **Restart Lidarr**

### After Installation

1. Go to **Settings → Import Lists → Add (+)**
2. Select **XM Playlist** from the list
3. Configure:
   - **Result Count**: How many recent plays to fetch (1-1000, default 200)
   - **Channel Filter** (optional): Comma-separated channel IDs (e.g. `altnation, xmu, thespectrum`). Leave empty for all channels.
   - **Dedupe Artists**: Only add each artist once per fetch (recommended)
4. Click **Test** to verify, then **Save**

## Configuration

| Setting | Default | Description |
|---------|---------|-------------|
| Result Count | 200 | Number of recent plays to pull from the feed |
| Channel Filter | *(all)* | Comma-separated SiriusXM channel IDs to filter by |
| Dedupe Artists | on | Skip duplicate artists within a single fetch |

## Building From Source

```powershell
git clone --recursive https://github.com/ksamples14/sat-list.git
cd sat-list
dotnet restore SatList.sln
dotnet build SatList.sln -c Release
```

Output: `_plugins/net8.0/SatList/Lidarr.Plugin.SatList.dll`

## Project Structure

```
sat-list/
├── src/SatList/
│   ├── SatList.csproj
│   ├── Plugin.cs                          # IPlugin entry point
│   ├── PluginInfo.targets                 # Build-time metadata generation
│   ├── PreBuild.targets                   # Lidarr submodule init
│   └── ImportLists/
│       ├── SatListImport.cs              # HttpImportListBase<TSettings>
│       ├── SatListImportSettings.cs      # [FieldDefinition] + validation
│       ├── SatListRequestGenerator.cs    # /api/feed request builder
│       └── SatListParser.cs             # xmplaylist JSON → ImportListItemInfo
├── Submodules/Lidarr/                     # Lidarr source (git submodule)
└── SatList.sln
```

## API Usage Notes

- The xmplaylist `/api/feed` endpoint is **free and requires no authentication**
- A `User-Agent` header is required per the API guidelines
- The feed is cached for 2 minutes by xmplaylist's CDN
- Lidarr matches artists by name, so accuracy depends on consistent artist naming

## License

MIT
