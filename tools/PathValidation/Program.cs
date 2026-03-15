using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FSTService.Scraping;

// ─── Path Generation Validation Tool ────────────────────────
// Exercises the full flow: download .dat → decrypt → rename tracks → CHOpt → max scores
//
// Usage:  dotnet run --project tools/PathValidation -- <MIDI_KEY_HEX> [song_title_filter]
//
// Requires: CHOpt.exe in tools/chopt-cli/

var repoRoot = FindRepoRoot(Environment.CurrentDirectory);
var choptPath = Path.Combine(repoRoot, "tools", "CHOpt", "CHOpt.exe");

if (!File.Exists(choptPath))
{
    Console.Error.WriteLine($"ERROR: CHOpt not found at {choptPath}");
    Console.Error.WriteLine("Download from https://github.com/GenericMadScientist/CHOpt/releases");
    return 1;
}

if (args.Length < 1)
{
    Console.Error.WriteLine("Usage: PathValidation <MIDI_KEY_HEX> [song_title_filter]");
    Console.Error.WriteLine("  MIDI_KEY_HEX: 32-char hex string (AES-128 key for .dat decryption)");
    Console.Error.WriteLine("  song_title_filter: optional substring match on song title");
    return 1;
}

byte[] midiKey;
try
{
    midiKey = MidiCryptor.ParseHexKey(args[0]);
    Console.WriteLine($"✓ MIDI key parsed ({midiKey.Length} bytes)");
}
catch (Exception ex)
{
    Console.Error.WriteLine($"ERROR: Invalid MIDI key: {ex.Message}");
    return 1;
}

var songFilter = args.Length > 1 ? args[1] : null;

// ─── Phase 1: Fetch song catalog ────────────────────────────

Console.WriteLine("\n═══ Phase 1: Fetching song catalog from Epic ═══");

using var http = new HttpClient();
http.DefaultRequestHeaders.Add("User-Agent", "FSTPathValidation/1.0");

string catalogJson;
try
{
    catalogJson = await http.GetStringAsync(
        "https://fortnitecontent-website-prod07.ol.epicgames.com/content/api/pages/fortnite-game/spark-tracks");
    Console.WriteLine($"✓ Catalog fetched ({catalogJson.Length:N0} bytes)");
}
catch (Exception ex)
{
    Console.Error.WriteLine($"ERROR: Failed to fetch catalog: {ex.Message}");
    return 1;
}

// Parse out songs with .dat URLs
var songs = new List<(string Id, string Title, string Artist, string DatUrl)>();
using (var doc = JsonDocument.Parse(catalogJson))
{
    foreach (var prop in doc.RootElement.EnumerateObject())
    {
        if (prop.Value.ValueKind != JsonValueKind.Object) continue;
        if (!prop.Value.TryGetProperty("track", out var track)) continue;
        if (!track.TryGetProperty("su", out var suProp)) continue;
        if (!track.TryGetProperty("mu", out var muProp)) continue;
        if (!track.TryGetProperty("tt", out var ttProp)) continue;

        var su = suProp.GetString();
        var mu = muProp.GetString();
        var tt = ttProp.GetString();
        var an = track.TryGetProperty("an", out var anProp) ? anProp.GetString() : "Unknown";

        if (su != null && mu != null && tt != null)
            songs.Add((su, tt, an ?? "Unknown", mu));
    }
}

Console.WriteLine($"✓ Found {songs.Count} songs with .dat URLs");

// Filter
if (songFilter != null)
{
    songs = songs.Where(s =>
        s.Title.Contains(songFilter, StringComparison.OrdinalIgnoreCase) ||
        s.Id.Contains(songFilter, StringComparison.OrdinalIgnoreCase))
        .ToList();
    Console.WriteLine($"✓ Filter '{songFilter}' matched {songs.Count} song(s)");
}

if (songs.Count == 0)
{
    Console.Error.WriteLine("No songs found. Try a different filter.");
    return 1;
}

// Pick first match (or a well-known song if no filter)
var song = songs.First();
Console.WriteLine($"\n▸ Selected: \"{song.Title}\" by {song.Artist} (ID: {song.Id})");

// ─── Phase 2: Download .dat ─────────────────────────────────

Console.WriteLine("\n═══ Phase 2: Downloading encrypted .dat ═══");

byte[] datBytes;
try
{
    datBytes = await http.GetByteArrayAsync(song.DatUrl);
    Console.WriteLine($"✓ Downloaded {datBytes.Length:N0} bytes");
    Console.WriteLine($"  SHA256: {MidiCryptor.ComputeHash(datBytes)[..16]}...");
}
catch (Exception ex)
{
    Console.Error.WriteLine($"ERROR: Download failed: {ex.Message}");
    return 1;
}

// ─── Phase 3: Decrypt to MIDI ───────────────────────────────

Console.WriteLine("\n═══ Phase 3: AES-ECB Decryption ═══");

byte[] midiBytes;
try
{
    midiBytes = MidiCryptor.Decrypt(datBytes, midiKey);
    Console.WriteLine($"✓ Decrypted: {midiBytes.Length:N0} bytes");

    // Sanity check: does it look like a MIDI file?
    if (midiBytes.Length >= 4 && midiBytes[0] == 'M' && midiBytes[1] == 'T' &&
        midiBytes[2] == 'h' && midiBytes[3] == 'd')
    {
        Console.WriteLine("✓ Valid MIDI header (MThd) detected");
    }
    else
    {
        Console.Error.WriteLine("⚠ WARNING: Decrypted data does not start with MThd header!");
        Console.Error.WriteLine($"  First 8 bytes: {Convert.ToHexString(midiBytes[..Math.Min(8, midiBytes.Length)])}");
        Console.Error.WriteLine("  The MIDI encryption key may be wrong.");
        return 1;
    }
}
catch (Exception ex)
{
    Console.Error.WriteLine($"ERROR: Decryption failed: {ex.Message}");
    return 1;
}

// ─── Phase 4: Produce MIDI variants ─────────────────────────

Console.WriteLine("\n═══ Phase 4: Track Renaming → MIDI Variants ═══");

MidiTrackRenamer.MidiVariants variants;
try
{
    variants = MidiTrackRenamer.ProduceVariants(midiBytes);
    Console.WriteLine($"✓ _pro.mid:     {variants.ProMidi.Length:N0} bytes");
    Console.WriteLine($"  _og.mid:      {variants.OgMidi.Length:N0} bytes");
}
catch (Exception ex)
{
    Console.Error.WriteLine($"ERROR: Track renaming failed: {ex.Message}");
    return 1;
}

// ─── Phase 5: Run CHOpt ─────────────────────────────────────

Console.WriteLine("\n═══ Phase 5: Running CHOpt (6 instruments) ═══");

var tempDir = Path.Combine(Path.GetTempPath(), $"fst-pathval-{Guid.NewGuid():N}");
Directory.CreateDirectory(tempDir);

try
{
    // Write MIDI variants to temp files
    var proPath = Path.Combine(tempDir, "song_pro.mid");
    var ogPath = Path.Combine(tempDir, "song_og.mid");

    await File.WriteAllBytesAsync(proPath, variants.ProMidi);
    await File.WriteAllBytesAsync(ogPath, variants.OgMidi);

    // Write song.ini so CHOpt renders the title/artist in the image header
    var songIni = Path.Combine(tempDir, "song.ini");
    await File.WriteAllTextAsync(songIni,
        $"[song]\nname = {song.Title}\nartist = {song.Artist}\ncharter = Harmonix, Rhythm Authors\n");

    // Instrument config: (DisplayName, InstrumentDbName, MidiFile, CHOptInstrument)
    var instruments = new[]
    {
        ("Pro Lead",  "Solo_PeripheralGuitar", proPath,     "guitar"),
        ("Pro Bass",  "Solo_PeripheralBass",   proPath,     "bass"),
        ("Drums",     "Solo_Drums",            ogPath,      "drums"),
        ("Vocals",    "Solo_Vocals",           ogPath,      "vocals"),
        ("Lead",      "Solo_Guitar",           ogPath,      "guitar"),
        ("Bass",      "Solo_Bass",             ogPath,      "bass"),
    };

    var difficulties = new[] { "easy", "medium", "hard", "expert" };

    var results = new List<(string Name, string DbInstrument, string Difficulty, int? Score, string? ImagePath)>();
    var outputDir = Path.Combine(tempDir, "paths");

    foreach (var (name, dbInstrument, midiFile, choptInstr) in instruments)
    {
        // Output: paths/{instrument}/{difficulty}.png
        var instrumentDir = Path.Combine(outputDir, dbInstrument);
        Directory.CreateDirectory(instrumentDir);

        foreach (var diff in difficulties)
        {
            var outputImage = Path.Combine(instrumentDir, $"{diff}.png");
            Console.Write($"  {name,-12} {diff,-8}: ");

            var (score, error) = await RunCHOptAsync(choptPath, midiFile, choptInstr, diff, outputImage);
            if (score.HasValue)
            {
                var imageSize = File.Exists(outputImage) ? new FileInfo(outputImage).Length : 0;
                Console.WriteLine($"{score.Value,8:N0}  (image: {imageSize:N0} bytes)");
                results.Add((name, dbInstrument, diff, score, File.Exists(outputImage) ? outputImage : null));
            }
            else
            {
                Console.WriteLine($"FAILED  ({error})");
                results.Add((name, dbInstrument, diff, null, null));
            }
        }
    }

    // ─── Summary ─────────────────────────────────────────────

    Console.WriteLine($"\n═══ Results: \"{song.Title}\" (Expert scores = max attainable) ═══");
    Console.WriteLine($"{"Instrument",-30} {"Easy",10} {"Medium",10} {"Hard",10} {"Expert",10}");
    Console.WriteLine(new string('─', 74));
    foreach (var group in results.GroupBy(r => r.Name))
    {
        var byDiff = group.ToDictionary(r => r.Difficulty);
        string Fmt(string d) => byDiff.TryGetValue(d, out var r) && r.Score.HasValue
            ? $"{r.Score.Value,10:N0}" : "    FAILED";
        Console.WriteLine($"{group.Key,-30} {Fmt("easy")} {Fmt("medium")} {Fmt("hard")} {Fmt("expert")}");
    }

    var total = results.Count;
    var succeeded = results.Count(r => r.Score.HasValue);
    Console.WriteLine(new string('─', 74));
    Console.WriteLine($"{succeeded}/{total} instrument/difficulty combos succeeded");

    if (succeeded > 0)
    {
        Console.WriteLine($"\nPath images saved to: {outputDir}");
        Console.WriteLine("(Temp directory — copy any images you want to keep)");
    }

    return succeeded == total ? 0 : 1;
}
finally
{
    // Don't auto-delete — let user inspect the output
    Console.WriteLine($"\nTemp directory: {tempDir}");
}

// ─── Helpers ─────────────────────────────────────────────────

static async Task<(int? Score, string? Error)> RunCHOptAsync(
    string choptPath, string midiFile, string instrument, string difficulty, string outputImage)
{
    var psi = new ProcessStartInfo
    {
        FileName = choptPath,
        ArgumentList =
        {
            "-f", midiFile,
            "-i", instrument,
            "-d", difficulty,
            "-o", outputImage,
            "--engine", "fnf",
            "--early-whammy", "0",
            "--squeeze", "20",
        },
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true,
    };

    using var process = Process.Start(psi);
    if (process is null)
        return (null, "Failed to start CHOpt process");

    var stdout = await process.StandardOutput.ReadToEndAsync();
    var stderr = await process.StandardError.ReadToEndAsync();

    await process.WaitForExitAsync();

    if (process.ExitCode != 0)
        return (null, $"Exit code {process.ExitCode}: {stderr.Trim()}");

    var score = PathGenerator.ParseTotalScore(stdout);
    return (score, score is null ? $"No 'Total score:' in output: {stdout.Trim()}" : null);
}

static string FindRepoRoot(string startDir)
{
    var dir = startDir;
    while (dir != null)
    {
        if (File.Exists(Path.Combine(dir, "FortniteFestivalLeaderboardScraper.sln")))
            return dir;
        dir = Path.GetDirectoryName(dir);
    }
    return startDir;
}
