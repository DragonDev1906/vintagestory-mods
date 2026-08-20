# VintageStory Mods

A collection of mods for [Vintage Story](https://vintagestory.at/).

## Mods

| Mod | Type | Description |
|-----|------|-------------|
| [Heatstone](heatstone/) | Code | Wearable, rechargeable heat source |
| [Map3D](map3d/) | Code | Block to display a 3D map of your world |
| [Translocator Direction Indicator](translocatordirectionindicator/) | Code | Adds a small indicator pointing towards the exit Translocator |
| [One Seed](oneseed/) | Content | Reduces seed drops to one per plant |
| [BetterRuins - Bricklayers Compat](betterruinsbricklayerscompat/) | Content | Fixes a copper duplication exploit when combining these two mods |

## Installing for Testing

### Option 1: Development scripts (recommended)

Run the setup script for your platform. It will compile the mod locally (if `dotnet` is available) or download the latest build from CI, then symlink it into your Vintage Story Mods folder.

**Windows:**
```bat
setup-dev.bat heatstone
```

**Linux / macOS:**
```bash
./setup-dev.sh heatstone
```

Run with no arguments to see the current status. Use `--all` to link every mod, or `--clean` to remove all symlinks.

See the script help (`--help`) for all options. The scripts detect your OS and Vintage Story data directory automatically.

### Option 2: Download from CI

1. Go to [Actions](../../actions) and open the latest successful build.
2. Download the zip artifact for the mod you want.
3. Extract it into your Mods folder:
   - **Windows:** `%APPDATA%\VintageStoryData\Mods\<modid>\`
   - **Linux:** `~/.config/VintageStoryData/Mods/<modid>/`

## Development

### Asset editing (no build tools required)

Edit JSON files in the mod's `assets/` directory. The game watches the Mods folder for changes, so restart Vintage Story to see updates. With the setup scripts, your repo files are symlinked directly, so edits appear immediately after restart.

### Building from source

**Requirements:**
- [.NET 10 SDK](https://dotnet.microsoft.com/download) (for code mods)
- [`jq`](https://jqlang.github.io/jq/) (required by the setup scripts for JSON parsing)
- Vintage Story installation (the SDK needs the game's DLLs)

**Setup:**

Set the `VINTAGE_STORY` environment variable to your Vintage Story installation directory:
```bash
# Linux (Steam)
export VINTAGE_STORY=~/.local/share/Steam/steamapps/common/Vintage Story

# Windows
set VINTAGE_STORY=C:\Program Files (x86)\Steam\steamapps\common\Vintage Story
```

**Build a single mod:**
```bash
./build.sh heatstone
```

**Build all mods:**
```bash
./build.sh
```

Output goes to `Releases/<mod>_<version>.zip`.

### CI/CD

Every push and pull request triggers the [build workflow](.github/workflows/build.yml). It detects which mods changed and builds only those. When a mod's version changes in `modinfo.json`, the zip is uploaded as a CI artifact.
