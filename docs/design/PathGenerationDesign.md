# Path Generation & Max Attainable Score Design

## Overview

This feature adds Star Power (Overdrive) path generation and max attainable score tracking to FSTService. It:

1. Downloads and decrypts Fortnite Festival MIDI files from Epic's CDN
2. Runs CHOpt (CLI) to compute optimal paths and max scores per song/instrument
3. Stores max scores in the Songs DB and path images on disk
4. Exposes path images and max score data via API
5. Enables filtering of "unattainable" scores (Season 2 cross-instrument bug)

## Background: The Season 2 Bug

In Season 2, a bug caused certain songs on certain instruments to submit scores to *other* instrument leaderboards. This means some leaderboard entries have scores that are physically impossible to achieve on the correct instrument. CHOpt computes the theoretical maximum score for each song/instrument combination, letting us identify these invalid entries.

## Architecture

### Component Overview

```
FSTService/
  Scraping/
    PathGenerator.cs        — Orchestrator: download → decrypt → rename → CHOpt → store
    MidiCryptor.cs          — AES-ECB decrypt/encrypt for .dat ↔ .mid
    MidiTrackRenamer.cs     — Produces instrument-specific MIDI variants for CHOpt
  data/
    midi/                   — Cached encrypted .dat files (keyed by SHA256 of content)
    paths/                  — Generated path images: {songId}/{instrument}.png
```

### Data Flow

```
Epic CDN (.dat URL from track.mu)
    │
    ▼
Download encrypted .dat file
    │
    ▼
Compare SHA256 hash with cached .dat
    │
    ├── Match → skip (paths already up to date)
    │
    └── Changed / New → Continue
            │
            ▼
        AES-ECB decrypt → .mid (in-memory or temp file)
            │
            ▼
        Produce 2 MIDI variants:
          ├── {songId}_pro.mid    (Pro Lead / Pro Bass)
          └── {songId}_og.mid     (Lead / Bass / Drums / Vocals)
            │
            ▼
        Run CHOpt CLI (6 invocations per song):
          ├── Pro Lead:  CHOpt -f _pro.mid     -i guitar  --engine fnf
          ├── Pro Bass:  CHOpt -f _pro.mid     -i bass    --engine fnf
          ├── Drums:     CHOpt -f _og.mid      -i drums   --engine fnf
          ├── Vocals:    CHOpt -f _og.mid      -i vocals  --engine fnf
          ├── Lead:      CHOpt -f _og.mid      -i guitar  --engine fnf
          └── Bass:      CHOpt -f _og.mid      -i bass    --engine fnf
            │
            ▼
        Parse CHOpt stdout for "Total score: NNNNN"
        Save PNG to data/paths/{songId}/{instrument}.png
        Store max score + metadata in Songs DB
            │
            ▼
        Cache .dat file hash for future comparison
```

### CHOpt Integration

**Binary**: Use the pre-built CHOpt CLI from GitHub releases (Linux x64 for Docker, Windows x64 for local dev). Do NOT fork the C++ source.

**Version**: Pin to a specific release (currently v1.10.3). Store version in config for reproducibility.

**Default Parameters** (matching FNFpaths convention):
```
--engine fnf --early-whammy 0 --squeeze 20
```

**Binary Location**: Configurable via `ScraperOptions.CHOptPath` (default: `tools/CHOpt`). Downloaded and placed during Docker build or manual setup.

### MIDI Decryption (MidiCryptor.cs)

Port of FNFpaths `fnf.py`. AES-128-ECB with a fixed key (provided via environment variable `FESTIVAL_MIDI_KEY` or config).

```csharp
// AES-128-ECB, no padding (manual handling of final partial block)
// Key: 16-byte hex string from environment
// IV: not used (ECB mode)
```

The key is a secret and must not be committed to source control.

### MIDI Track Renaming (MidiTrackRenamer.cs)

Port of FNFpaths `download.py`'s `replace_tracks_in_midi`. CHOpt expects standard Guitar Hero/Rock Band track naming for Pro instruments, but Festival uses different track names (`PLASTIC GUITAR`/`PLASTIC BASS`). For standard instruments (Lead, Bass, Drums, Vocals), CHOpt natively supports Fortnite Festival tracks via `--engine fnf`. We produce two MIDI variants:

| Variant | Renames Applied | Used For |
|---|---|---|
| `_pro.mid` | `PLASTIC GUITAR` → `PART GUITAR`, `PLASTIC BASS` → `PART BASS` | Pro Lead, Pro Bass |
| `_og.mid` | No renames (original MIDI with standard names) | Lead, Bass, Drums, Vocals |

Track renaming operates on raw MIDI bytes — rename `track_name` meta events by scanning for the byte pattern.

### Storage

#### Max Scores — Songs DB Columns

Add per-instrument max score columns to the existing `Songs` table in `fst-service.db` (managed by `SqlitePersistence.cs`):

```sql
ALTER TABLE Songs ADD COLUMN MaxLeadScore INTEGER;
ALTER TABLE Songs ADD COLUMN MaxBassScore INTEGER;
ALTER TABLE Songs ADD COLUMN MaxDrumsScore INTEGER;
ALTER TABLE Songs ADD COLUMN MaxVocalsScore INTEGER;
ALTER TABLE Songs ADD COLUMN MaxProLeadScore INTEGER;
ALTER TABLE Songs ADD COLUMN MaxProBassScore INTEGER;
ALTER TABLE Songs ADD COLUMN DatFileHash TEXT;       -- SHA256 of last processed .dat
ALTER TABLE Songs ADD COLUMN PathsGeneratedAt TEXT;  -- ISO timestamp of last generation
ALTER TABLE Songs ADD COLUMN CHOptVersion TEXT;       -- CHOpt version used
```

Migration via the existing `AddColumn` pattern in `SqlitePersistence.EnsureDatabase()`.

#### Path Images — Filesystem

Stored at `data/paths/{songId}/{instrument}.png`.

Instrument names in filenames follow the DB convention: `Solo_Guitar`, `Solo_Bass`, `Solo_Drums`, `Solo_Vocals`, `Solo_PeripheralGuitar`, `Solo_PeripheralBass`.

Example: `data/paths/sparks_fly/Solo_Guitar.png`

#### Cached .dat Files

Stored at `data/midi/{songId}.dat` — the encrypted file as downloaded from Epic. Used for SHA256 comparison on subsequent runs (if the .dat matches, skip regeneration).

### Trigger: When to Run

1. **During scrape lifecycle**: After song catalog sync in `BackgroundSongSyncLoopAsync`, running in parallel with score scraping. When new songs are detected or a song's `.dat` file changes, path generation runs for those songs.

2. **First setup**: On initial startup, if `Songs` rows have `NULL` max score columns, generate paths for all songs (backfill).

3. **On demand**: `POST /api/admin/regenerate-paths` (API key required). Optional `?songId=xxx` query param to regenerate a single song.

### Lifecycle Integration

Path generation runs as a fire-and-forget task launched from `BackgroundSongSyncLoopAsync` after detecting new/changed songs. It does NOT block the scrape pass — leaderboard scraping continues in parallel.

```
BackgroundSongSyncLoopAsync:
  1. service.SyncSongsAsync()         ← existing
  2. if (new or changed songs detected)
       _ = pathGenerator.GeneratePathsAsync(changedSongs, ct)  ← new, fire-and-forget
```

The `PathGenerator` uses its own semaphore to limit concurrent CHOpt processes (default: 4).

### Configuration

New fields in `ScraperOptions`:

```csharp
/// <summary>Path to CHOpt CLI binary.</summary>
public string CHOptPath { get; set; } = "tools/CHOpt";

/// <summary>Hex-encoded AES key for MIDI decryption. Set via FESTIVAL_MIDI_KEY env var.</summary>
public string? MidiEncryptionKey { get; set; }

/// <summary>Max concurrent CHOpt processes during path generation.</summary>
public int PathGenerationParallelism { get; set; } = 4;

/// <summary>Enable/disable automatic path generation on song sync.</summary>
public bool EnablePathGeneration { get; set; } = true;
```

### API Endpoints

#### `GET /api/paths/{songId}/{instrument}` (Public)
Returns the path image PNG for a specific song/instrument. Returns 404 if not yet generated.

#### `POST /api/admin/regenerate-paths` (API Key)
Triggers path regeneration. Optional query params:
- `songId` — regenerate for a single song
- `force=true` — ignore .dat hash cache, regenerate everything
Returns 202 Accepted with a job ID.

#### `GET /api/songs` Enhancement
The existing `/api/songs` response will include `maxScores` per song:
```json
{
  "songId": "sparks_fly",
  "title": "Sparks Fly",
  "maxScores": {
    "Solo_Guitar": 234567,
    "Solo_Bass": 198432,
    "Solo_Drums": 287654,
    "Solo_Vocals": 176543,
    "Solo_PeripheralGuitar": 245678,
    "Solo_PeripheralBass": 201234
  },
  "pathsGeneratedAt": "2026-03-14T12:00:00Z"
}
```

### Docker Integration

Add CHOpt CLI download to the Dockerfile runtime stage:

```dockerfile
# Download CHOpt CLI (Linux x64)
ADD https://github.com/GenericMadScientist/CHOpt/releases/download/v1.10.3/CHOpt.CLI.1.10.3.x64.Linux.zip /tmp/chopt.zip
RUN apt-get update && apt-get install -y --no-install-recommends unzip \
    && unzip /tmp/chopt.zip -d /app/tools \
    && chmod +x /app/tools/CHOpt \
    && rm /tmp/chopt.zip \
    && apt-get remove -y unzip && apt-get autoremove -y && rm -rf /var/lib/apt/lists/*
```

### Unattainable Score Leeway

CHOpt's max score is the theoretical optimum. We store it as-is in the DB. Leeway/tolerance is applied at the application layer (web/mobile) — users can control how strict the filter is. The API returns raw max scores; clients decide the threshold.

### Error Handling

- **CHOpt not found**: Log warning, disable path generation gracefully. Service continues without path data.
- **MIDI key not configured**: Log warning, skip path generation. All other scraping continues.
- **CHOpt crashes on a song**: Log error for that song/instrument, continue with remaining instruments/songs. Store NULL for that instrument's max score.
- **Download failure**: Retry with exponential backoff (3 attempts). On persistent failure, skip song and log.

### Testing

- `MidiCryptorTests.cs` — Round-trip encrypt/decrypt with known test vectors
- `MidiTrackRenamerTests.cs` — Verify track name substitution in MIDI bytes
- `PathGeneratorTests.cs` — Mock CHOpt process invocation, verify output parsing and DB updates
- Integration test: end-to-end with a small test MIDI file
