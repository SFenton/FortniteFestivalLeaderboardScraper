# FortnitePakExtractor

.NET 9 console tool that extracts textures from Fortnite's IoStore `.utoc/.ucas/.pak` containers and writes them as PNG. Built around [CUE4Parse](https://github.com/FabianFG/CUE4Parse) (included as a git submodule under `extern/`).

**Not committed to the repo's main solution.** Standalone under `tools/`.

## What it does

1. Fetches the current Fortnite AES keys from `https://fortnite-api.com/v2/aes` (rotates every patch).
2. Downloads and decompresses the `.usmap` type mappings from `https://fortnitecentral.genxgames.gg/api/v1/mappings`.
3. Auto-downloads the native Oodle DLL (`oodle-data-shared.dll`) via CUE4Parse's `OodleHelper`.
4. Extracts the embedded `Detex.dll` (BC1/3/5/7 decoder) from CUE4Parse resources.
5. Mounts all chunks in the Fortnite Paks directory with matching dynamic keys.
6. Filters assets by filename regex + path substring and exports each `UTexture` as PNG.

Cache and output default to `D:\FModelOutput\` (outside the workspace on purpose).

## Festival / Pro instrument asset paths (April 2026, Fortnite v40)

Festival ships as the **`FM`** GameFeaturePlugin (not `Sparks`). Relevant subtrees:

| Path | Content |
|---|---|
| `FortniteGame/Plugins/GameFeatures/FM/SparksCosmetics/Content/UI/Icons/` | Per-item icons (guitars, basses, drums, mics, drum kits — hundreds per season) |
| `FortniteGame/Plugins/GameFeatures/FM/PilgrimCore/Content/` | Core gameplay: gem smashers, Overdrive, track UI |
| `FortniteGame/Plugins/GameFeatures/FM/FMJam/FMJamSystem/Content/Textures/Icons/` | Jam mode icons, incl. `T_Icon_Instruments_Type_SDF` (instrument type atlas) |
| `FortniteGame/Plugins/GameFeatures/FM/SparksUICommon/Content/Textures/` | Shared UI: `T_Icon_Instrument_Type01_SDF`, `T_Icon_Controls_SDF` |
| Widget textures (various) | `T_Icon_Instrument_ProDrums_DF`, `T_Icon_Instrument_ProSnare_DF` |

**Notable findings**
- Most UI icons are stored as raster `Texture2D` in BC-compressed formats — extract fine to PNG.
- SDF / MSDF "vector-ish" icons (`*_SDF`, `*_DF`) decode to raw signed-distance-field channel data. They need a shader to render, not direct viewing. The Pro Drums raw `_DF` looks like a clean white-on-black icon. Multi-channel SDF atlases like `T_Icon_Instruments_Type_SDF` look like colorful gradients — that's correct.
- Only explicit `Pro*` icons found are `ProDrums` and `ProSnare` (drums + cymbals). Pro Lead / Pro Bass / Pro Vocals are not under explicit `Pro` filenames — likely share the regular Lead/Bass/Vocals UI icons or are inside the SDF atlas textures.

## Usage

```powershell
cd tools\FortnitePakExtractor
dotnet build -c Release
dotnet run -c Release --no-build

# Or with overrides:
dotnet run -c Release --no-build -- --pattern "Icon_Instrument" --path-filter FM/ --output D:\MyIcons
```

CLI options:

| Flag | Default | Description |
|---|---|---|
| `--paks <dir>` | `D:\Epic Games\Fortnite\FortniteGame\Content\Paks` | Pak directory |
| `--output <dir>` | `D:\FModelOutput\ProIcons` | PNG output |
| `--cache <dir>` | `D:\FModelOutput\_cache` | Oodle + mappings + Detex cache |
| `--pattern <regex>` | `^(T_Icon_|T_UI_|T_.*_Icon)` | Filename regex |
| `--path-filter <str>` | `FortniteGame/Plugins/GameFeatures/FM/` | Path substring filter |
| `--ue <ver>` | `GAME_UE5_6` | UE version (accepts `5.6`, `5.5`, etc.) |

## First-run notes

- ~138 vfs readers (chunks) are discovered. ~46 mount with the public AES keys; the remainder are uondemand/streaming/private — expected.
- Pak format version 12 is the current Fortnite version. The pinned CUE4Parse supports it (unsupported-version warnings are spurious on this build but loads continue).
- Extraction runtime: ~10s cold, including all downloads.

## Regenerating for a new Fortnite patch

Just re-run. AES keys and mappings refresh automatically. If CUE4Parse falls behind on a new pak version, `cd extern\CUE4Parse && git pull origin master`.

## Notes on CUE4Parse submodule

- C++ natives (Oodle, ACL) are disabled in `extern/CUE4Parse/CUE4Parse/CUE4Parse.csproj` via a stub `<Target Name="Build-Natives" />` since we don't need CMake here. Oodle is still loaded at runtime from the downloaded DLL.
- If you `git submodule update --init --recursive` later, some nested C++ submodules may fail to clone. That's fine — we don't build them.

## Legal

Epic tolerates community datamining for non-commercial use. Extracted assets must not be redistributed. Use only as reference inside this companion tool's UI.
