using System.Text.Json;
using System.Text.Json.Serialization;

namespace FSTService.Scraping;

/// <summary>Immutable supplemental metadata for generations created before chart inspection.</summary>
internal static class PathDoubleBassMetadataStore
{
    internal static string GetPath(string dataDirectory, string songId, string generationId)
    {
        // Reuse the artifact resolver's safe-segment and containment validation.
        _ = PathArtifactResolver.GetGenerationDirectory(dataDirectory, songId, generationId);
        return Path.Combine(Path.GetFullPath(dataDirectory), "path-metadata", songId, generationId + ".json");
    }

    internal static bool? Read(string dataDirectory, PathArtifactManifest manifest, string manifestHash)
    {
        var path = GetPath(dataDirectory, manifest.SongId, manifest.GenerationId);
        if (!File.Exists(path))
            return null;
        EnsureSafePath(dataDirectory, path);
        if (new FileInfo(path).Length > 4096)
            throw new InvalidDataException("Supplemental chart metadata is oversized.");
        var metadata = JsonSerializer.Deserialize<PathDoubleBassMetadata>(
            File.ReadAllText(path), PathArtifactManifest.JsonOptions)
            ?? throw new InvalidDataException("Supplemental chart metadata is empty.");
        if (metadata.SongId != manifest.SongId
            || metadata.GenerationId != manifest.GenerationId
            || metadata.DatFileHash != manifest.DatFileHash
            || metadata.ManifestSha256 != manifestHash)
            throw new InvalidDataException("Supplemental chart metadata identity does not match.");
        return metadata.DoubleBassSupported;
    }

    internal static void Write(string dataDirectory, PathArtifactManifest manifest,
        string manifestHash, bool support)
    {
        var existing = Read(dataDirectory, manifest, manifestHash);
        if (existing.HasValue)
        {
            if (existing.Value != support)
                throw new InvalidDataException("Existing immutable chart metadata disagrees.");
            return;
        }
        var path = GetPath(dataDirectory, manifest.SongId, manifest.GenerationId);
        EnsureSafePath(dataDirectory, path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        EnsureSafePath(dataDirectory, path);
        var metadata = new PathDoubleBassMetadata(manifest.SongId, manifest.GenerationId,
            manifest.DatFileHash, manifestHash, support, DateTime.UtcNow);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write))
                JsonSerializer.Serialize(stream, metadata, PathArtifactManifest.JsonOptions);
            File.Move(temporary, path, overwrite: false);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    internal static void EnsureSafePath(string dataDirectory, string path)
    {
        var root = Path.GetFullPath(dataDirectory);
        var full = Path.GetFullPath(path);
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidDataException("Chart metadata path escapes the data directory.");
        for (var current = full; current != root; current = Path.GetDirectoryName(current)!)
        {
            if ((File.Exists(current) || Directory.Exists(current))
                && File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidDataException("Chart metadata path contains a symbolic link.");
        }
        if (File.GetAttributes(root).HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidDataException("Chart metadata root is a symbolic link.");
    }
}

internal sealed record PathDoubleBassMetadata(
    [property: JsonRequired] string SongId,
    [property: JsonRequired] string GenerationId,
    [property: JsonRequired] string DatFileHash,
    [property: JsonRequired] string ManifestSha256,
    [property: JsonRequired] bool DoubleBassSupported,
    [property: JsonRequired] DateTime InspectedAtUtc);
