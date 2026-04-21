using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using CUE4Parse.Compression;
using CUE4Parse.Encryption.Aes;
using CUE4Parse.FileProvider;
using CUE4Parse.MappingsProvider;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.UE4.Objects.Core.Misc;
using CUE4Parse.UE4.Versions;
using CUE4Parse_Conversion.Textures;
using CUE4Parse_Conversion.Textures.BC;
using Serilog;
using Serilog.Events;
using ZstdSharp;

namespace FortnitePakExtractor;

internal static class Program
{
    // Defaults. All overridable via CLI.
    private const string DefaultPaks = @"D:\Epic Games\Fortnite\FortniteGame\Content\Paks";
    private const string DefaultOutput = @"D:\FModelOutput\ProIcons";
    private const string DefaultCache = @"D:\FModelOutput\_cache";
    // Broad: any UI-ish texture under Festival. Filter at pattern level, not just path.
    private const string DefaultPattern = @"^(T_Icon_|T_UI_|T_.*_Icon)";
    private const string DefaultPathFilter = "FortniteGame/Plugins/GameFeatures/FM/";
    private const EGame DefaultUeVersion = EGame.GAME_UE5_6;

    private const string AesEndpoint = "https://fortnite-api.com/v2/aes";
    private const string MappingsEndpoint = "https://fortnitecentral.genxgames.gg/api/v1/mappings";

    public static async Task<int> Main(string[] args)
    {
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Override("CUE4Parse", LogEventLevel.Warning)
            .WriteTo.Console()
            .CreateLogger();

        var opts = Options.Parse(args);
        Directory.CreateDirectory(opts.Output);
        Directory.CreateDirectory(opts.Cache);

        // --msdf-only: skip the pak pipeline and just render SDF/DF pngs already in the output dir.
        if (opts.MsdfOnly)
        {
            Log.Information("MSDF-only mode. Scanning {Dir}", opts.Output);
            MsdfRender.RenderAll(opts.Output, opts.MsdfSize);
            return 0;
        }

        Log.Information("Paks:     {Paks}", opts.Paks);
        Log.Information("Output:   {Output}", opts.Output);
        Log.Information("Cache:    {Cache}", opts.Cache);
        Log.Information("UE:       {Ue}", opts.Ue);
        Log.Information("Pattern:  {Pattern}", opts.Pattern);
        Log.Information("PathHas:  {PathHas}", opts.PathFilter);

        if (!Directory.Exists(opts.Paks))
        {
            Log.Error("Paks directory does not exist: {Path}", opts.Paks);
            return 2;
        }

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };

        // 1. Oodle: initialize the native decompressor. First call downloads the dll to the cache dir.
        var oodlePath = Path.Combine(opts.Cache, OodleHelper.OodleFileName);
        await OodleHelper.InitializeAsync(oodlePath);
        Log.Information("Oodle: {State} ({Path})",
            OodleHelper.Instance is null ? "UNAVAILABLE" : "ready", oodlePath);

        // 2. Detex: BC1/3/5/7 decoder. Extracted from embedded resource on first run.
        var detexPath = Path.Combine(opts.Cache, DetexHelper.DLL_NAME);
        if (DetexHelper.LoadDll(detexPath))
        {
            DetexHelper.Initialize(detexPath);
            Log.Information("Detex: ready ({Path})", detexPath);
        }
        else
        {
            Log.Warning("Detex: UNAVAILABLE — BC-compressed textures will fail");
        }

        var aes = await FetchAesAsync(http);
        Log.Information("AES build: {Build}  mainKey set={HasMain}  dynamicKeys={N}",
            aes.Data.Build, !string.IsNullOrEmpty(aes.Data.MainKey), aes.Data.DynamicKeys?.Count ?? 0);

        var mappingsPath = await EnsureMappingsAsync(http, opts.Cache);
        Log.Information("Mappings: {Path}", mappingsPath ?? "(none; loading without mappings)");

        var provider = new DefaultFileProvider(
            opts.Paks,
            SearchOption.TopDirectoryOnly,
            isCaseInsensitive: true,
            new VersionContainer(opts.Ue));

        if (mappingsPath is not null)
        {
            provider.MappingsContainer = new FileUsmapTypeMappingsProvider(mappingsPath);
        }

        provider.Initialize();
        Log.Information("Discovered {N} vfs readers", provider.UnloadedVfs.Count);

        // Submit main key (zero GUID) and all dynamic keys.
        var submitted = 0;
        if (!string.IsNullOrWhiteSpace(aes.Data.MainKey))
        {
            submitted += await provider.SubmitKeyAsync(new FGuid(), new FAesKey(aes.Data.MainKey!));
        }
        foreach (var dk in aes.Data.DynamicKeys ?? [])
        {
            if (string.IsNullOrWhiteSpace(dk.Guid) || string.IsNullOrWhiteSpace(dk.Key)) continue;
            try
            {
                var guid = new FGuid(dk.Guid!.Replace("-", "").ToUpperInvariant());
                submitted += await provider.SubmitKeyAsync(guid, new FAesKey(dk.Key!));
            }
            catch (Exception ex)
            {
                Log.Warning("Bad dynamic key entry (guid={Guid}): {Msg}", dk.Guid, ex.Message);
            }
        }
        Log.Information("Mounted {N} paks. Still unloaded: {U}", submitted, provider.UnloadedVfs.Count);

        // Localization isn't needed for icon extraction.
        var nameRegex = new Regex(opts.Pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled);
        var pathNeedle = opts.PathFilter;

        var candidates = provider.Files
            .Where(kv => kv.Key.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase))
            .Where(kv => pathNeedle.Length == 0 || kv.Key.Contains(pathNeedle, StringComparison.OrdinalIgnoreCase))
            .Where(kv => nameRegex.IsMatch(Path.GetFileNameWithoutExtension(kv.Key)))
            .Select(kv => kv.Key)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
            .ToList();

        Log.Information("Matched {N} asset paths to inspect", candidates.Count);
        foreach (var p in candidates.Take(30)) Log.Information("  - {Path}", p);
        if (candidates.Count > 30) Log.Information("  ... (+{N} more)", candidates.Count - 30);

        var exported = 0;
        var skipped = 0;
        foreach (var assetPath in candidates)
        {
            try
            {
                var pkg = provider.LoadPackage(assetPath);
                foreach (var export in pkg.GetExports())
                {
                    if (export is not UTexture tex) continue;

                    var ct = tex.Decode();
                    if (ct is null)
                    {
                        Log.Warning("Decode returned null: {Path}.{Name}", assetPath, export.Name);
                        skipped++;
                        continue;
                    }

                    var png = ct.Encode(ETextureFormat.Png, saveHdrAsHdr: false, out var ext);
                    if (png.Length == 0)
                    {
                        Log.Warning("Encode produced 0 bytes: {Path}.{Name}", assetPath, export.Name);
                        skipped++;
                        continue;
                    }

                    var outFile = BuildOutputPath(opts.Output, assetPath, export.Name, ext);
                    Directory.CreateDirectory(Path.GetDirectoryName(outFile)!);
                    await File.WriteAllBytesAsync(outFile, png);
                    exported++;
                    Log.Information("  [{W}x{H}] -> {Out}", ct.Width, ct.Height, outFile);
                }
            }
            catch (Exception ex)
            {
                Log.Warning("Failed {Path}: {Msg}", assetPath, ex.Message);
                skipped++;
            }
        }

        Log.Information("Done. Exported={Ex}  Skipped={Sk}  Output={Out}", exported, skipped, opts.Output);

        if (!opts.SkipMsdf)
        {
            MsdfRender.RenderAll(opts.Output, opts.MsdfSize);
        }
        return exported > 0 ? 0 : 3;
    }

    private static string BuildOutputPath(string root, string assetPath, string exportName, string ext)
    {
        // assetPath like: "FortniteGame/Plugins/GameFeatures/Sparks/Content/UI/Icons/T_Foo.uasset"
        var noExt = Path.ChangeExtension(assetPath, null);
        var safe = string.Join('_',
            noExt.Split('/', '\\', StringSplitOptions.RemoveEmptyEntries).TakeLast(3));
        var fileName = $"{safe}__{exportName}.{ext}";
        // Normalize invalid chars for filesystem.
        foreach (var c in Path.GetInvalidFileNameChars()) fileName = fileName.Replace(c, '_');
        return Path.Combine(root, fileName);
    }

    // ---- AES ----

    private sealed class AesResponse
    {
        [JsonPropertyName("status")] public int Status { get; set; }
        [JsonPropertyName("data")] public AesData Data { get; set; } = new();
    }
    private sealed class AesData
    {
        [JsonPropertyName("build")] public string? Build { get; set; }
        [JsonPropertyName("mainKey")] public string? MainKey { get; set; }
        [JsonPropertyName("dynamicKeys")] public List<DynamicKey>? DynamicKeys { get; set; }
    }
    private sealed class DynamicKey
    {
        [JsonPropertyName("pakFilename")] public string? PakFilename { get; set; }
        [JsonPropertyName("pakGuid")] public string? Guid { get; set; }
        [JsonPropertyName("key")] public string? Key { get; set; }
    }

    private static async Task<AesResponse> FetchAesAsync(HttpClient http)
    {
        Log.Information("Fetching AES keys from {Url}", AesEndpoint);
        var resp = await http.GetFromJsonAsync<AesResponse>(AesEndpoint)
                   ?? throw new InvalidOperationException("AES endpoint returned null");
        if (resp.Status != 200) throw new InvalidOperationException($"AES endpoint status={resp.Status}");
        return resp;
    }

    // ---- Mappings (.usmap) ----

    private sealed class MappingsResponse
    {
        [JsonPropertyName("version")] public string? Version { get; set; }
        [JsonPropertyName("mappings")] public Dictionary<string, string>? Mappings { get; set; }
    }

    private static async Task<string?> EnsureMappingsAsync(HttpClient http, string cacheDir)
    {
        try
        {
            Log.Information("Fetching mappings manifest from {Url}", MappingsEndpoint);
            var resp = await http.GetFromJsonAsync<MappingsResponse>(MappingsEndpoint);
            if (resp?.Mappings is null || resp.Mappings.Count == 0)
            {
                Log.Warning("No mappings available");
                return null;
            }

            // Prefer ZStandard (we have ZstdSharp); fall back to Brotli (System.IO.Compression).
            string? url = null; string? algo = null;
            if (resp.Mappings.TryGetValue("ZStandard", out var z)) { url = z; algo = "zstd"; }
            else if (resp.Mappings.TryGetValue("Brotli", out var b)) { url = b; algo = "brotli"; }
            else { url = resp.Mappings.Values.FirstOrDefault(); algo = "raw"; }
            if (string.IsNullOrEmpty(url)) return null;

            var version = resp.Version ?? "mappings";
            var dest = Path.Combine(cacheDir, $"{version}.usmap");
            if (!File.Exists(dest))
            {
                Log.Information("Downloading mappings ({Algo}): {Url}", algo, url);
                var compressed = await http.GetByteArrayAsync(url);

                // Try in order: raw (CUE4Parse handles internal compression), then zstd, then brotli.
                byte[] raw = compressed;
                if (algo == "zstd")
                {
                    try { raw = DecompressZstd(compressed); Log.Information("Zstd decompressed: {N} bytes", raw.Length); }
                    catch (Exception ex)
                    {
                        Log.Information("Zstd wrapping not present ({Msg}); using raw bytes", ex.Message);
                        raw = compressed;
                    }
                }
                else if (algo == "brotli")
                {
                    try { raw = DecompressBrotli(compressed); Log.Information("Brotli decompressed: {N} bytes", raw.Length); }
                    catch (Exception ex)
                    {
                        Log.Information("Brotli wrapping not present ({Msg}); using raw bytes", ex.Message);
                        raw = compressed;
                    }
                }
                await File.WriteAllBytesAsync(dest, raw);
            }
            return dest;
        }
        catch (Exception ex)
        {
            Log.Warning("Mappings fetch failed, continuing without: {Msg}", ex.Message);
            return null;
        }
    }

    private static byte[] DecompressZstd(byte[] data)
    {
        using var input = new MemoryStream(data);
        using var ds = new DecompressionStream(input);
        using var output = new MemoryStream();
        ds.CopyTo(output);
        return output.ToArray();
    }

    private static byte[] DecompressBrotli(byte[] data)
    {
        using var input = new MemoryStream(data);
        using var bs = new System.IO.Compression.BrotliStream(input, System.IO.Compression.CompressionMode.Decompress);
        using var output = new MemoryStream();
        bs.CopyTo(output);
        return output.ToArray();
    }

    // ---- CLI ----

    private sealed record Options(
        string Paks,
        string Output,
        string Cache,
        string Pattern,
        string PathFilter,
        EGame Ue,
        bool MsdfOnly,
        bool SkipMsdf,
        int MsdfSize)
    {
        public static Options Parse(string[] args)
        {
            string paks = DefaultPaks, output = DefaultOutput, cache = DefaultCache,
                   pattern = DefaultPattern, pathFilter = DefaultPathFilter;
            EGame ue = DefaultUeVersion;
            bool msdfOnly = false, skipMsdf = false;
            int msdfSize = 512;

            for (var i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--paks": paks = args[++i]; break;
                    case "--output": output = args[++i]; break;
                    case "--cache": cache = args[++i]; break;
                    case "--pattern": pattern = args[++i]; break;
                    case "--path-filter": pathFilter = args[++i]; break;
                    case "--msdf-only": msdfOnly = true; break;
                    case "--skip-msdf": skipMsdf = true; break;
                    case "--msdf-size":
                        if (!int.TryParse(args[++i], NumberStyles.Integer, CultureInfo.InvariantCulture, out msdfSize))
                            throw new ArgumentException("--msdf-size expects an integer");
                        break;
                    case "--ue":
                        var v = args[++i];
                        if (!Enum.TryParse<EGame>(v, true, out ue))
                        {
                            // allow "5.6" shorthand
                            if (v.StartsWith("5.", StringComparison.Ordinal)
                                && int.TryParse(v.AsSpan(2), NumberStyles.Integer, CultureInfo.InvariantCulture, out var minor)
                                && Enum.TryParse<EGame>($"GAME_UE5_{minor}", true, out ue)) { /* ok */ }
                            else throw new ArgumentException($"Unknown UE version: {v}");
                        }
                        break;
                    case "-h":
                    case "--help":
                        PrintHelp();
                        Environment.Exit(0);
                        break;
                    default:
                        throw new ArgumentException($"Unknown arg: {args[i]}");
                }
            }
            return new Options(paks, output, cache, pattern, pathFilter, ue, msdfOnly, skipMsdf, msdfSize);
        }

        private static void PrintHelp()
        {
            Console.WriteLine("""
                FortnitePakExtractor — extract Fortnite Festival asset textures to PNG.

                Options:
                  --paks <dir>         Fortnite Paks directory (default: D:\Epic Games\Fortnite\FortniteGame\Content\Paks)
                  --output <dir>       Output directory for PNGs (default: D:\FModelOutput\ProIcons)
                  --cache <dir>        Cache dir for mappings (default: D:\FModelOutput\_cache)
                  --pattern <regex>    Asset filename regex (default: Pro instrument icons)
                  --path-filter <str>  Substring that must appear in the asset path (default: 'Sparks')
                  --ue <ver>           UE version enum (default: GAME_UE5_6). Accepts '5.6' shorthand.
                  -h, --help           Show this help.

                Examples:
                  FortnitePakExtractor
                  FortnitePakExtractor --pattern "Icon_" --path-filter "Sparks" --output D:\festival-icons
                  FortnitePakExtractor --ue 5.5
                """);
        }
    }

    // Silence a warning about unused JsonSerializerOptions; keep placeholder for future use.
    private static readonly JsonSerializerOptions _json = new() { PropertyNameCaseInsensitive = true };
}