# SatList - Lidarr Import List Plugin

A minimal Lidarr import list plugin that fetches artist/album entries from any JSON HTTP endpoint.

## How It Works

1. The plugin polls your JSON endpoint on a schedule (default: every 6 hours)
2. Your endpoint returns a JSON array of entries
3. Each entry is parsed into Lidarr's import list system
4. Lidarr resolves artists/albums by name and/or MusicBrainz ID and adds them to your library

## Expected API Response Format

Your endpoint must return a JSON array:

```json
[
  {
    "artist": "Radiohead",
    "artistMusicBrainzId": "a74b1b7f-71a5-4011-9441-d0b5e4122711",
    "album": "OK Computer",
    "albumMusicBrainzId": "b1392450-e666-3926-a536-22c65f834433",
    "releaseDate": "1997-05-21T00:00:00"
  },
  {
    "artist": "The Beatles",
    "artistMusicBrainzId": null,
    "album": "Abbey Road",
    "albumMusicBrainzId": null,
    "releaseDate": null
  }
]
```

All fields are optional. At least one identifying field (artist, album, artistMusicBrainzId, albumMusicBrainzId) must be present. MusicBrainz IDs provide the most accurate matching.

## Prerequisites

- Lidarr **nightly** branch (plugins not supported on stable)
- .NET 8.0 SDK (to build from source)

## Installation

### From GitHub Releases

1. In Lidarr, go to **Settings → Plugins**
2. Paste `https://github.com/YOUR_USER/sat-list` into the GitHub URL box
3. Click **Install**
4. **Restart Lidarr**

### After Installation

1. Go to **Settings → Import Lists → Add (+)** 
2. Select **SatList Import** from the list
3. Configure:
   - **API URL**: Your JSON endpoint
   - **API Key**: Optional authentication key
   - **API Key Location**: Query parameter or HTTP header
4. Click **Test** to verify, then **Save**

## Building From Source

```powershell
# Clone with Lidarr submodule
git clone --recursive https://github.com/YOUR_USER/sat-list.git
cd sat-list

# Restore and build
dotnet restore SatList.sln
dotnet build SatList.sln -c Release

# Output: _plugins/net8.0/SatList/Lidarr.Plugin.SatList.dll
```

## Project Structure

```
sat-list/
├── src/SatList/
│   ├── SatList.csproj           # Project file (ILRepack, Lidarr refs)
│   ├── Plugin.cs                # IPlugin entry point
│   ├── PluginInfo.targets       # Generates PluginInfo.cs at build time
│   ├── PreBuild.targets         # Inits Lidarr git submodule
│   └── ImportLists/
│       ├── SatListImport.cs            # Main import list class
│       ├── SatListImportSettings.cs    # UI settings with [FieldDefinition]
│       ├── SatListRequestGenerator.cs  # HTTP request builder
│       └── SatListParser.cs            # JSON response parser
├── Submodules/Lidarr/           # Lidarr source (git submodule)
├── Directory.Build.props        # Shared build settings
└── SatList.sln                  # Solution file
```

## Customizing

To create your own import list from a different API:

1. **Settings** (`SatListImportSettings.cs`): Add your API-specific fields with `[FieldDefinition]`
2. **Request Generator** (`SatListRequestGenerator.cs`): Build the API request (URL, auth, pagination)
3. **Parser** (`SatListParser.cs`): Convert the API response to `List<ImportListItemInfo>`
4. **Import List** (`SatListImport.cs`): Wire everything together, set `Name`, `ListType`, `MinRefreshInterval`

## License

MIT
