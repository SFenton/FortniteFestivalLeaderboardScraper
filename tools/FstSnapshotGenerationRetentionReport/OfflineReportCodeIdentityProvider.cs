using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace FstSnapshotGenerationRetentionReport;

public interface IOfflineReportCodeIdentityProvider
{
    Task<OfflineReportCodeIdentity> CaptureAsync(CancellationToken ct);
}

public sealed class OfflineReportCodeIdentityProvider : IOfflineReportCodeIdentityProvider
{
    private static readonly string[] SourceRoots =
    [
        "FSTService", "FortniteFestival.Core",
        "tools/FstSnapshotGenerationRetentionReport",
    ];
    private readonly string _root;
    private readonly string _binary;

    public OfflineReportCodeIdentityProvider()
    {
        _binary = Environment.ProcessPath
            ?? throw new OfflineReportRefusal("binary_path_unavailable");
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(
                   directory.FullName, "FortniteFestivalLeaderboardScraper.sln")))
            directory = directory.Parent;
        _root = directory?.FullName ?? throw new OfflineReportRefusal("repository_root_unavailable");
    }

    public async Task<OfflineReportCodeIdentity> CaptureAsync(CancellationToken ct)
    {
        VerifyBinaryPin();
        var wrapper = Path.Combine(_root, OfflineReportContract.WrapperRelativePath);
        RequireRegularFile(wrapper);
        var commit = await GitAsync(["rev-parse", "HEAD"], ct);
        var tree = await GitAsync(["rev-parse", "HEAD^{tree}"], ct);
        var status = await GitAsync(["status", "--porcelain", "--untracked-files=all"], ct);
        using var source = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var files = SourceRoots.SelectMany(root => EnumerateSources(
                new DirectoryInfo(Path.Combine(_root, root))))
            .Concat(new[]
            {
                Path.Combine(_root, "Directory.Build.props"),
                Path.Combine(_root, "Directory.Build.targets"),
                Path.Combine(_root, "global.json"),
                Path.Combine(_root, "NuGet.Config"),
            }.Where(File.Exists))
            .Append(wrapper)
            .OrderBy(file => Path.GetRelativePath(_root, file), StringComparer.Ordinal);
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            RequireRegularFile(file);
            source.AppendData(Encoding.UTF8.GetBytes(Path.GetRelativePath(_root, file).Replace('\\', '/')));
            source.AppendData([0]);
            using var stream = File.OpenRead(file);
            var buffer = new byte[65536];
            int read;
            while ((read = stream.Read(buffer)) > 0)
                source.AppendData(buffer.AsSpan(0, read));
            source.AppendData([0]);
        }
        return new(commit, tree,
            Convert.ToHexString(source.GetHashAndReset()).ToLowerInvariant(),
            OfflineReportContract.FileHash(_binary),
            OfflineReportContract.FileHash(wrapper),
            string.IsNullOrWhiteSpace(status));
    }

    public static void VerifyBinaryPin()
    {
        var expected = Environment.GetEnvironmentVariable(OfflineReportContract.BinaryHashEnvironment);
        var configured = Environment.GetEnvironmentVariable(OfflineReportContract.BinaryPathEnvironment);
        var process = Environment.ProcessPath;
        if (!OfflineReportContract.IsHash(expected) || string.IsNullOrWhiteSpace(configured)
            || string.IsNullOrWhiteSpace(process)
            || Path.GetFullPath(configured) != Path.GetFullPath(process))
            throw new OfflineReportRefusal("binary_pin_missing_or_invalid");
        RequireRegularFile(process);
        if (OfflineReportContract.FileHash(process) != expected)
            throw new OfflineReportRefusal("binary_hash_mismatch");
    }

    private static IEnumerable<string> EnumerateSources(DirectoryInfo directory)
    {
        if (!directory.Exists || directory.LinkTarget is not null)
            throw new OfflineReportRefusal("runtime_source_directory_invalid");
        foreach (var item in directory.EnumerateFileSystemInfos())
        {
            if (item is DirectoryInfo child)
            {
                if (child.Name is "bin" or "obj" or "artifacts")
                    continue;
                foreach (var file in EnumerateSources(child))
                    yield return file;
            }
            else if (Path.GetExtension(item.Name) is ".cs" or ".csproj" or ".props" or ".targets")
            {
                RequireRegularFile(item.FullName);
                yield return item.FullName;
            }
        }
    }

    private static void RequireRegularFile(string path)
    {
        var file = new FileInfo(path);
        if (!file.Exists || file.LinkTarget is not null)
            throw new OfflineReportRefusal("runtime_source_file_invalid");
    }

    private async Task<string> GitAsync(string[] arguments, CancellationToken ct)
    {
        const string git = "/usr/bin/git";
        RequireRegularFile(git);
        if (!OperatingSystem.IsLinux()
            || (File.GetUnixFileMode(git) & (UnixFileMode.UserExecute
                | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) == 0)
            throw new OfflineReportRefusal("trusted_git_executable_unavailable");
        var start = new ProcessStartInfo(git)
        {
            WorkingDirectory = _root, UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        start.Environment.Clear();
        start.Environment["PATH"] = "/usr/bin:/bin";
        start.Environment["HOME"] = "/nonexistent";
        start.Environment["XDG_CONFIG_HOME"] = "/nonexistent";
        start.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        start.Environment["GIT_CONFIG_GLOBAL"] = "/dev/null";
        start.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        start.ArgumentList.Add("--no-optional-locks");
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add("core.fsmonitor=false");
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start)
            ?? throw new OfflineReportRefusal("repository_identity_unavailable");
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        try
        {
            await process.WaitForExitAsync(ct);
        }
        catch
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            throw;
        }
        await stderr;
        if (process.ExitCode != 0)
            throw new OfflineReportRefusal("repository_identity_unavailable");
        return (await stdout).Trim();
    }
}
