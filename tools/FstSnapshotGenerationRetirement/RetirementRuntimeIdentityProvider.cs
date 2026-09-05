using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace FstSnapshotGenerationRetirement;

public interface IRetirementRuntimeIdentityProvider
{
    Task<RetirementCodeIdentity> CaptureAsync(
        bool requireCleanRepository,
        CancellationToken ct = default);
}

public sealed class RetirementRuntimeIdentityProvider
    : IRetirementRuntimeIdentityProvider
{
    private static readonly string[] SourceFiles =
    [
        "tools/FstSnapshotGenerationRetirement/FstSnapshotGenerationRetirement.csproj",
        "tools/FstSnapshotGenerationRetirement/Program.cs",
        "tools/FstSnapshotGenerationRetirement/RetirementContracts.cs",
        "tools/FstSnapshotGenerationRetirement/RetirementController.cs",
        "tools/FstSnapshotGenerationRetirement/RetirementDatabase.cs",
        "tools/FstSnapshotGenerationRetirement/RetirementRuntimeIdentityProvider.cs",
    ];

    private readonly string _repositoryRoot;
    private readonly string _binaryPath;

    public RetirementRuntimeIdentityProvider(
        string? repositoryRoot = null,
        string? binaryPath = null)
    {
        _repositoryRoot = repositoryRoot
            ?? FindRepositoryRoot();
        _binaryPath = binaryPath
            ?? ResolveExecutingBinaryPath();
    }

    public async Task<RetirementCodeIdentity> CaptureAsync(
        bool requireCleanRepository,
        CancellationToken ct = default)
    {
        var wrapperPath = FixedPath(
            SnapshotGenerationRetirementContract
                .WrapperRelativePath);
        ValidateRegularFile(_binaryPath);
        ValidateRegularFile(wrapperPath);
        foreach (var relativePath in SourceFiles)
            ValidateRegularFile(FixedPath(relativePath));

        var repositoryCommit = await RunGitAsync(
            ["rev-parse", "HEAD"],
            ct);
        var repositoryTree = await RunGitAsync(
            ["rev-parse", "HEAD^{tree}"],
            ct);
        if (requireCleanRepository)
        {
            var status = await RunGitAsync(
                [
                    "status",
                    "--porcelain",
                    "--untracked-files=all",
                ],
                ct,
                allowEmpty: true);
            if (!string.IsNullOrWhiteSpace(status))
            {
                throw new InvalidOperationException(
                    "Retirement authorization and planning require a clean committed repository.");
            }
        }

        var identity = new RetirementCodeIdentity(
            repositoryCommit,
            repositoryTree,
            RetirementJson.Sha256File(_binaryPath),
            ComputeSourceBundleSha256(),
            RetirementJson.Sha256File(wrapperPath));
        identity.Validate();
        return identity;
    }

    private string ComputeSourceBundleSha256()
    {
        using var hash =
            IncrementalHash.CreateHash(
                HashAlgorithmName.SHA256);
        foreach (var relativePath in
                 SourceFiles.Order(
                     StringComparer.Ordinal))
        {
            var normalized =
                relativePath.Replace('\\', '/');
            hash.AppendData(
                Encoding.UTF8.GetBytes(normalized));
            hash.AppendData([0]);
            using var stream = new FileStream(
                FixedPath(relativePath),
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read);
            var buffer = new byte[64 * 1024];
            int read;
            while ((read = stream.Read(
                       buffer,
                       0,
                       buffer.Length)) > 0)
            {
                hash.AppendData(buffer, 0, read);
            }
            hash.AppendData([0]);
        }
        return Convert.ToHexString(
                hash.GetHashAndReset())
            .ToLowerInvariant();
    }

    private string FixedPath(string relativePath)
    {
        var path = Path.GetFullPath(
            Path.Combine(
                _repositoryRoot,
                relativePath));
        var root = Path.GetFullPath(
            _repositoryRoot)
            .TrimEnd(Path.DirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        if (!path.StartsWith(
                root,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Retirement runtime path escaped the repository.");
        }
        return path;
    }

    private static void ValidateRegularFile(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists
            || info.LinkTarget is not null)
        {
            throw new InvalidOperationException(
                $"Retirement runtime file is missing or symbolic: {path}");
        }
    }

    private async Task<string> RunGitAsync(
        IReadOnlyList<string> arguments,
        CancellationToken ct,
        bool allowEmpty = false)
    {
        var start = new ProcessStartInfo
        {
            FileName = "git",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add("-C");
        start.ArgumentList.Add(_repositoryRoot);
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);

        using var process = new Process
        {
            StartInfo = start,
        };
        if (!process.Start())
        {
            throw new InvalidOperationException(
                "Failed to start git for retirement runtime identity.");
        }
        var stdoutTask =
            process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask =
            process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        var stdout = (await stdoutTask).Trim();
        var stderr = (await stderrTask).Trim();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Git retirement identity probe failed: {stderr}");
        }
        if (!allowEmpty
            && string.IsNullOrWhiteSpace(stdout))
        {
            throw new InvalidOperationException(
                "Git retirement identity probe returned no value.");
        }
        return stdout;
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(
            AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(
                    Path.Combine(
                        current.FullName,
                        "FortniteFestivalLeaderboardScraper.sln")))
            {
                return current.FullName;
            }
            current = current.Parent;
        }
        throw new DirectoryNotFoundException(
            "Repository root was not found.");
    }

    private static string ResolveExecutingBinaryPath()
    {
        var configured =
            Environment.GetEnvironmentVariable(
                SnapshotGenerationRetirementContract
                    .BinaryPathEnvironment);
        var processPath = Environment.ProcessPath
            ?? throw new InvalidOperationException(
                "The retirement process path is unavailable.");
        if (string.IsNullOrWhiteSpace(configured))
            return processPath;
        var expected = Path.GetFullPath(configured);
        var observed = Path.GetFullPath(processPath);
        if (!string.Equals(
                expected,
                observed,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The retirement executable path differs from the wrapper-pinned path.");
        }
        return observed;
    }
}
