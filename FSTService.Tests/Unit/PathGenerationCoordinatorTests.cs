using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FortniteFestival.Core;
using FSTService.Api;
using FSTService.Persistence;
using FSTService.Scraping;
using FSTService.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace FSTService.Tests.Unit;

public sealed class PathGenerationCoordinatorTests : IDisposable
{
    private const string ValidPngBase64 =
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=";
    private readonly string _dataDirectory;
    private readonly byte[] _midiKey = new byte[32];
    private readonly byte[] _encryptedDat;

    public PathGenerationCoordinatorTests()
    {
        _dataDirectory = Path.Combine(
            Directory.GetCurrentDirectory(),
            ".test-temp",
            $"path-generation-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dataDirectory);
        RandomNumberGenerator.Fill(_midiKey);
        _encryptedDat = EncryptMidi(BuildMinimalMidi(), _midiKey);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dataDirectory))
                Directory.Delete(_dataDirectory, recursive: true);
        }
        catch
        {
        }
    }

    [Fact]
    public void Song_request_treats_present_difficulty_zero_as_expected()
    {
        var song = CreateSong("difficulty-zero", new In { gr = 9, ba = 9 });
        using var provider = JsonDocument.Parse(
            """
            {
              "track": {
                "su": "difficulty-zero",
                "mu": "https://example.invalid/difficulty-zero.dat",
                "in": { "gr": 0 }
              }
            }
            """);
        song.providerJson = provider.RootElement.Clone();

        var request = SongPathRequest.FromSong(song);

        Assert.NotNull(request);
        Assert.Equal(["Solo_Guitar"], request!.ExpectedInstruments);
    }

    [Fact]
    public void Song_request_maps_plastic_drums_to_both_path_modes()
    {
        var request = SongPathRequest.FromSong(
            CreateSong("plastic-drums", new In { pd = 0 }));

        Assert.NotNull(request);
        Assert.Equal(
            ["Solo_PeripheralCymbals", "Solo_PeripheralDrums"],
            request!.ExpectedInstruments);
    }

    [Fact]
    public void Plastic_drum_path_definitions_use_distinct_scoring_modes()
    {
        var cymbals = PathGenerationInstruments.GetDefinition(
            "Solo_PeripheralCymbals");
        var drums = PathGenerationInstruments.GetDefinition(
            "Solo_PeripheralDrums");

        Assert.Equal("og", cymbals.MidiVariant);
        Assert.Equal("prodrums", cymbals.ChoptInstrument);
        Assert.False(cymbals.DisableProDrums);
        Assert.Equal("og", drums.MidiVariant);
        Assert.Equal("prodrums", drums.ChoptInstrument);
        Assert.True(drums.DisableProDrums);
    }

    [Fact]
    public async Task Plastic_drum_modes_pass_only_the_pad_mode_disable_flag()
    {
        var logPath = Path.Combine(
            _dataDirectory,
            "plastic-drum-invocations.log");
        var chopt = CreateChoptScript(
            new ChoptBehavior(InvocationLog: logPath));
        var store = new FakePathDataStore();
        store.EnsureSong("plastic-drums-flags");
        var coordinator = CreateCoordinator(
            chopt,
            store,
            new StaticDatHandler(_encryptedDat));

        var result = await coordinator.GeneratePathsAsync(
            [CreateSong("plastic-drums-flags", new In { pd = 0 })],
            force: false,
            CancellationToken.None);

        Assert.Equal(1, result.Promoted);
        var invocations = File.ReadAllLines(logPath);
        Assert.Equal(8, invocations.Length);
        Assert.All(
            invocations,
            line => Assert.Contains("-i prodrums", line));
        Assert.Equal(
            4,
            invocations.Count(line => line.Contains(
                "--no-pro-drums=true",
                StringComparison.Ordinal)));
        Assert.Equal(
            4,
            invocations.Count(line => line.Contains(
                "--no-pro-drums=false",
                StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Automatic_generation_processes_only_durable_pending_rows()
    {
        var chopt = CreateChoptScript();
        var store = new FakePathDataStore();
        store.EnsureSong("pending");
        store.Seed(new PathGenerationState(
            "legacy",
            0,
            "legacy-hash",
            UtcDate(1).ToString("o"),
            DateTime.UtcNow,
            "1.15.1",
            null,
            null,
            null,
            ["Solo_Guitar"],
            new SongMaxScores { MaxLeadScore = 77_777 }));
        var coordinator = CreateCoordinator(
            chopt,
            store,
            new StaticDatHandler(_encryptedDat));

        var result = await coordinator.GenerateAutomaticPathsAsync(
            [
                CreateSong("pending", new In { gr = 0 }, UtcDate(2)),
                CreateSong("legacy", new In { gr = 0 }, UtcDate(2)),
            ],
            CancellationToken.None);

        Assert.Equal(1, result.Requested);
        Assert.Equal(1, result.Promoted);
        Assert.NotNull(
            store.GetPathGenerationState("pending")!
                .ArtifactGenerationId);
        Assert.Null(
            store.GetPathGenerationState("legacy")!
                .ArtifactGenerationId);
        Assert.Empty(store.GetPendingPathGenerationSongIds());
    }

    [Fact]
    public async Task Unsupported_instruments_are_not_invoked()
    {
        var logPath = Path.Combine(_dataDirectory, "invocations.log");
        var chopt = CreateChoptScript(new ChoptBehavior(InvocationLog: logPath));
        var store = new FakePathDataStore();
        store.EnsureSong("supported-only");
        var handler = new StaticDatHandler(_encryptedDat);
        var coordinator = CreateCoordinator(chopt, store, handler);

        var result = await coordinator.GeneratePathsAsync(
            [CreateSong("supported-only", new In { gr = 0 })],
            force: false,
            CancellationToken.None);

        Assert.Equal(1, result.Promoted);
        var invocations = File.ReadAllLines(logPath);
        Assert.Equal(4, invocations.Length);
        Assert.All(invocations, line => Assert.Contains("-i guitar", line));
        Assert.All(invocations, line => Assert.Contains(".path-work", line));
        Assert.DoesNotContain(invocations, line => line.Contains("-i bass", StringComparison.Ordinal));
        Assert.DoesNotContain(
            invocations,
            line => line.Contains(
                Path.Combine("paths", "supported-only"),
                StringComparison.Ordinal));
    }

    [Fact]
    public async Task Partial_expected_failure_preserves_old_pointer_scores_and_files()
    {
        var chopt = CreateChoptScript(new ChoptBehavior(
            FailInstrument: "bass",
            FailDifficulty: "hard"));
        var store = new FakePathDataStore();
        var runtime = await CreateCoordinator(chopt, store).DetectRuntimeIdentityAsync(
            chopt,
            CancellationToken.None);
        var old = SeedCompleteGeneration(
            store,
            "partial",
            "old-generation",
            runtime,
            ["Solo_Guitar", "Solo_Bass"],
            MidiCryptor.ComputeHash(_encryptedDat),
            "2026-08-01T00:00:00.0000000Z");
        var oldImage = Path.Combine(
            old.GenerationDirectory,
            "Solo_Guitar",
            "expert.png");
        var oldBytes = File.ReadAllBytes(oldImage);
        var coordinator = CreateCoordinator(
            chopt,
            store,
            new StaticDatHandler(_encryptedDat));

        var result = await coordinator.GeneratePathsAsync(
            [
                CreateSong(
                    "partial",
                    new In { gr = 0, ba = 0 },
                    UtcDate(2)),
            ],
            force: false,
            CancellationToken.None);

        Assert.Equal(1, result.Failed);
        var state = store.GetPathGenerationState("partial");
        Assert.NotNull(state);
        Assert.Equal("old-generation", state!.ArtifactGenerationId);
        Assert.Equal(111_111, state.MaxScores.MaxLeadScore);
        Assert.Equal(111_111, state.MaxScores.MaxBassScore);
        Assert.Equal(oldBytes, File.ReadAllBytes(oldImage));
        Assert.Single(store.Errors);
    }

    [Theory]
    [InlineData("malformed-json")]
    [InlineData("missing-png")]
    [InlineData("empty-png")]
    [InlineData("bad-png")]
    [InlineData("signature-only-png")]
    [InlineData("missing-notes")]
    [InlineData("zero-expert")]
    public async Task Invalid_artifacts_fail_without_promotion(string mode)
    {
        var chopt = CreateChoptScript(new ChoptBehavior(Mode: mode));
        var store = new FakePathDataStore();
        store.EnsureSong("invalid");
        var coordinator = CreateCoordinator(
            chopt,
            store,
            new StaticDatHandler(_encryptedDat));

        var result = await coordinator.GeneratePathsAsync(
            [CreateSong("invalid", new In { gr = 0 })],
            force: false,
            CancellationToken.None);

        Assert.Equal(1, result.Failed);
        var state = store.GetPathGenerationState("invalid");
        Assert.NotNull(state);
        Assert.Null(state!.ArtifactGenerationId);
        Assert.Null(state.MaxScores.MaxLeadScore);
        Assert.Single(store.Errors);
        Assert.Equal("artifact_validation", store.Errors[0].FailureStage);
        AssertNoStagingAttempts();
    }

    [Fact]
    public async Task Stale_legacy_files_are_not_used_to_rescue_failed_generation()
    {
        var legacyImage = Path.Combine(
            _dataDirectory,
            "paths",
            "stale",
            "Solo_Guitar",
            "expert.png");
        Directory.CreateDirectory(Path.GetDirectoryName(legacyImage)!);
        WriteValidPng(legacyImage);
        var legacyBytes = File.ReadAllBytes(legacyImage);

        var chopt = CreateChoptScript(new ChoptBehavior(Mode: "missing-png"));
        var store = new FakePathDataStore();
        store.Seed(new PathGenerationState(
            "stale",
            3,
            "old-hash",
            null,
            DateTime.UtcNow.AddDays(-1),
            "1.0.0",
            "old-binary",
            "old-profile",
            null,
            ["Solo_Guitar"],
            new SongMaxScores { MaxLeadScore = 77_777 }));
        var coordinator = CreateCoordinator(
            chopt,
            store,
            new StaticDatHandler(_encryptedDat));

        var result = await coordinator.GeneratePathsAsync(
            [CreateSong("stale", new In { gr = 0 })],
            force: false,
            CancellationToken.None);

        Assert.Equal(1, result.Failed);
        Assert.Null(store.GetPathGenerationState("stale")!.ArtifactGenerationId);
        Assert.Equal(77_777, store.GetPathGenerationState("stale")!.MaxScores.MaxLeadScore);
        Assert.Equal(legacyBytes, File.ReadAllBytes(legacyImage));
    }

    [Fact]
    public async Task Complete_generation_moves_immutably_then_promotes_all_metadata()
    {
        var chopt = CreateChoptScript();
        var store = new FakePathDataStore();
        var runtime = await CreateCoordinator(chopt, store).DetectRuntimeIdentityAsync(
            chopt,
            CancellationToken.None);
        var old = SeedCompleteGeneration(
            store,
            "success",
            "old-generation",
            runtime with { Profile = "old-profile" },
            ["Solo_Guitar"],
            "old-hash",
            "2026-07-01T00:00:00.0000000Z");
        var oldImage = Path.Combine(
            old.GenerationDirectory,
            "Solo_Guitar",
            "expert.png");
        var oldBytes = File.ReadAllBytes(oldImage);
        var handler = new StaticDatHandler(_encryptedDat);
        var cache = new SongsCacheService();
        cache.Set("""{"stale":true}"""u8.ToArray());
        var coordinator = CreateCoordinator(chopt, store, handler, cache: cache);

        var result = await coordinator.GeneratePathsAsync(
            [
                CreateSong(
                    "success",
                    new In { gr = 0 },
                    UtcDate(1)),
            ],
            force: false,
            CancellationToken.None);

        Assert.Equal(1, result.Promoted);
        var state = store.GetPathGenerationState("success")!;
        Assert.NotEqual("old-generation", state.ArtifactGenerationId);
        Assert.Equal(MidiCryptor.ComputeHash(_encryptedDat), state.DatFileHash);
        Assert.Equal("2026-08-01T00:00:00.0000000Z", state.SongLastModified);
        Assert.Equal("1.10.3", state.ChoptVersion);
        Assert.Equal(runtime.BinarySha256, state.ChoptBinarySha256);
        Assert.Equal(
            "chopt-fnf-ew0-s20-json-png-prodrums-v3",
            state.GenerationProfile);
        Assert.Equal(["Solo_Guitar"], state.ExpectedInstruments);
        Assert.Equal(123_456, state.MaxScores.MaxLeadScore);
        Assert.True(PathArtifactResolver.IsGenerationComplete(_dataDirectory, state));
        Assert.Equal(oldBytes, File.ReadAllBytes(oldImage));
        Assert.Null(cache.Get());
    }

    [Fact]
    public async Task Database_failure_after_move_leaves_orphan_unreachable()
    {
        var chopt = CreateChoptScript();
        var store = new FakePathDataStore();
        var runtime = await CreateCoordinator(chopt, store).DetectRuntimeIdentityAsync(
            chopt,
            CancellationToken.None);
        SeedCompleteGeneration(
            store,
            "db-failure",
            "old-generation",
            runtime with { Profile = "old-profile" },
            ["Solo_Guitar"],
            "old-hash",
            null);
        store.ThrowOnPromotion = true;
        var coordinator = CreateCoordinator(
            chopt,
            store,
            new StaticDatHandler(_encryptedDat));

        var result = await coordinator.GeneratePathsAsync(
            [CreateSong("db-failure", new In { gr = 0 })],
            force: true,
            CancellationToken.None);

        Assert.Equal(1, result.Failed);
        Assert.Equal(
            "old-generation",
            store.GetPathGenerationState("db-failure")!.ArtifactGenerationId);
        var generationsRoot = Path.Combine(
            _dataDirectory,
            "paths",
            "db-failure",
            "generations");
        Assert.True(Directory.EnumerateDirectories(generationsRoot).Count() >= 2);
        Assert.Contains(store.Errors, error => error.FailureStage == "persistence");
    }

    [Fact]
    public async Task Cancellation_kills_process_tree_and_preserves_old_generation()
    {
        if (OperatingSystem.IsWindows())
            return;

        var childPidPath = Path.Combine(_dataDirectory, "child.pid");
        var startedPath = Path.Combine(_dataDirectory, "child.started");
        var chopt = CreateChoptScript(new ChoptBehavior(
            Mode: "process-tree",
            ChildPidPath: childPidPath,
            StartedPath: startedPath));
        var store = new FakePathDataStore();
        var runtime = await CreateCoordinator(chopt, store).DetectRuntimeIdentityAsync(
            chopt,
            CancellationToken.None);
        SeedCompleteGeneration(
            store,
            "cancel",
            "old-generation",
            runtime with { Profile = "old-profile" },
            ["Solo_Guitar"],
            "old-hash",
            null);
        var coordinator = CreateCoordinator(
            chopt,
            store,
            new StaticDatHandler(_encryptedDat));
        using var cts = new CancellationTokenSource();

        var generation = coordinator.GeneratePathsAsync(
            [CreateSong("cancel", new In { gr = 0 })],
            force: true,
            cts.Token);
        await WaitForFileAsync(startedPath, TimeSpan.FromSeconds(10));
        var childPid = int.Parse(await File.ReadAllTextAsync(childPidPath));
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => generation);
        await WaitForProcessExitAsync(childPid, TimeSpan.FromSeconds(10));
        Assert.Equal(
            "old-generation",
            store.GetPathGenerationState("cancel")!.ArtifactGenerationId);
        Assert.Contains(store.Errors, error => error.FailureStage == "cancelled");
        AssertNoStagingAttempts();
    }

    [Fact]
    public async Task Concurrent_calls_on_one_coordinator_serialize_and_second_skips()
    {
        var logPath = Path.Combine(_dataDirectory, "serialized.log");
        var chopt = CreateChoptScript(new ChoptBehavior(InvocationLog: logPath));
        var store = new FakePathDataStore();
        store.EnsureSong("serialized");
        var handler = new StaticDatHandler(_encryptedDat);
        var coordinator = CreateCoordinator(chopt, store, handler);
        var song = CreateSong(
            "serialized",
            new In { gr = 0 },
            UtcDate(1));

        var first = coordinator.GeneratePathsAsync(
            [song],
            force: false,
            CancellationToken.None);
        var second = coordinator.GeneratePathsAsync(
            [song],
            force: false,
            CancellationToken.None);
        var results = await Task.WhenAll(first, second);

        Assert.Equal(1, results.Sum(result => result.Promoted));
        Assert.Equal(1, results.Sum(result => result.Skipped));
        Assert.Equal(2, handler.RequestCount);
        Assert.Equal(4, File.ReadAllLines(logPath).Length);
    }

    [Fact]
    public async Task Distributed_admission_serializes_cross_coordinator_generation()
    {
        using var database = new InMemoryMetaDatabase();
        var logPath = Path.Combine(_dataDirectory, "distributed-admission.log");
        var chopt = CreateChoptScript(
            new ChoptBehavior(InvocationLog: logPath));
        var store = new FakePathDataStore();
        store.EnsureSong("distributed");
        var admission = new PostgresPathGenerationAdmissionLeaseProvider(
            CreateAdmissionConnectionString(database),
            NullLogger<PostgresPathGenerationAdmissionLeaseProvider>.Instance);
        var firstHandler = new StaticDatHandler(_encryptedDat);
        var secondHandler = new StaticDatHandler(_encryptedDat);
        var first = CreateCoordinator(
            chopt,
            store,
            firstHandler,
            admissionLeaseProvider: admission);
        var second = CreateCoordinator(
            chopt,
            store,
            secondHandler,
            admissionLeaseProvider: admission);
        var song = CreateSong(
            "distributed",
            new In { gr = 0 },
            UtcDate(1));

        var results = await Task.WhenAll(
            first.GeneratePathsAsync([song], false, CancellationToken.None),
            second.GeneratePathsAsync([song], false, CancellationToken.None));

        Assert.Equal(1, results.Sum(result => result.Promoted));
        Assert.Equal(1, results.Sum(result => result.Skipped));
        Assert.Equal(2, firstHandler.RequestCount + secondHandler.RequestCount);
        Assert.Equal(4, File.ReadAllLines(logPath).Length);
    }

    [Fact]
    public async Task Admission_cancellation_is_recorded_before_path_work()
    {
        var chopt = CreateChoptScript();
        var store = new FakePathDataStore();
        store.EnsureSong("admission-cancel");
        var handler = new StaticDatHandler(_encryptedDat);
        var coordinator = CreateCoordinator(
            chopt,
            store,
            handler,
            admissionLeaseProvider:
                BlockingPathGenerationAdmissionLeaseProvider.Instance);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            coordinator.GeneratePathsAsync(
                [CreateSong("admission-cancel", new In { gr = 0 })],
                false,
                cancellation.Token));

        Assert.Equal(0, handler.RequestCount);
        Assert.Contains(
            store.Errors,
            error => error.FailureStage == "cancelled"
                     && error.Detail.Contains(
                         "distributed admission",
                         StringComparison.Ordinal));
    }

    [Fact]
    public async Task Admission_failure_is_recorded_before_path_work()
    {
        var chopt = CreateChoptScript();
        var store = new FakePathDataStore();
        store.EnsureSong("admission-failure");
        var handler = new StaticDatHandler(_encryptedDat);
        var coordinator = CreateCoordinator(
            chopt,
            store,
            handler,
            admissionLeaseProvider:
                FailingPathGenerationAdmissionLeaseProvider.Instance);

        var result = await coordinator.GeneratePathsAsync(
            [CreateSong("admission-failure", new In { gr = 0 })],
            false,
            CancellationToken.None);

        Assert.Equal(1, result.Failed);
        Assert.Equal(0, handler.RequestCount);
        Assert.Contains(
            store.Errors,
            error => error.FailureStage == "admission"
                     && error.Detail.Contains(
                         "injected admission failure",
                         StringComparison.Ordinal));
    }

    [Fact]
    public async Task Preprocessing_failure_releases_real_admission_before_error_recording()
    {
        using var database = new InMemoryMetaDatabase();
        var chopt = CreateChoptScript();
        var store = new FakePathDataStore
        {
            ThrowOnStateRead = true,
            BlockErrorWrites = true,
        };
        var songs = Enumerable.Range(0, 5)
            .Select(index =>
                CreateSong(
                    $"release-before-errors-{index}",
                    new In { gr = 0 }))
            .ToArray();
        foreach (var song in songs)
            store.EnsureSong(song.track!.su!);

        var admission = new PostgresPathGenerationAdmissionLeaseProvider(
            CreateAdmissionConnectionString(database),
            NullLogger<PostgresPathGenerationAdmissionLeaseProvider>.Instance);
        var coordinator = CreateCoordinator(
            chopt,
            store,
            admissionLeaseProvider: admission);
        var generation = coordinator.GeneratePathsAsync(
            songs,
            false,
            CancellationToken.None);
        await store.ErrorWriteStarted.WaitAsync(TimeSpan.FromSeconds(5));

        IAsyncDisposable? probeLease = null;
        Exception? probeFailure = null;
        try
        {
            using var admissionTimeout = new CancellationTokenSource(
                TimeSpan.FromSeconds(2));
            probeLease = await admission.AcquireAsync(
                admissionTimeout.Token);
        }
        catch (Exception ex)
        {
            probeFailure = ex;
        }
        finally
        {
            if (probeLease is not null)
                await probeLease.DisposeAsync();
            store.ReleaseErrorWrites();
        }

        var result = await generation.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Null(probeFailure);
        Assert.Equal(songs.Length, result.Failed);
        Assert.Equal(songs.Length, store.Errors.Count);
    }

    [Fact]
    public async Task Batch_failure_error_recording_uses_one_bounded_budget()
    {
        const int songCount = 100;
        var chopt = CreateChoptScript();
        var store = new FakePathDataStore
        {
            ThrowOnStateRead = true,
            BlockErrorWrites = true,
        };
        var songs = Enumerable.Range(0, songCount)
            .Select(index =>
                CreateSong(
                    $"bounded-errors-{index}",
                    new In { gr = 0 }))
            .ToArray();
        foreach (var song in songs)
            store.EnsureSong(song.track!.su!);
        var progress = new ScrapeProgressTracker();
        var coordinator = CreateCoordinator(
            chopt,
            store,
            progress: progress);

        var stopwatch = Stopwatch.StartNew();
        var result = await coordinator
            .GeneratePathsAsync(
                songs,
                false,
                CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(10));
        stopwatch.Stop();

        Assert.Equal(songCount, result.Failed);
        Assert.Equal(1, store.ErrorWriteAttempts);
        Assert.Empty(store.Errors);
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(9),
            $"Batch error recording took {stopwatch.Elapsed}.");
        var pathProgress = progress.GetProgressResponse().PathGeneration;
        Assert.NotNull(pathProgress);
        Assert.False(pathProgress.Running);
        Assert.Equal(songCount, pathProgress.Failed);
        Assert.Equal(100d, pathProgress.ProgressPercent);
    }

    [Fact]
    public async Task Cross_coordinator_race_uses_compare_and_swap()
    {
        var chopt = CreateChoptScript();
        var store = new FakePathDataStore { PromotionBarrierCount = 2 };
        store.EnsureSong("cas");
        var first = CreateCoordinator(
            chopt,
            store,
            new StaticDatHandler(_encryptedDat));
        var second = CreateCoordinator(
            chopt,
            store,
            new StaticDatHandler(_encryptedDat));
        var song = CreateSong("cas", new In { gr = 0 });

        var results = await Task.WhenAll(
            first.GeneratePathsAsync([song], true, CancellationToken.None),
            second.GeneratePathsAsync([song], true, CancellationToken.None));

        Assert.Equal(1, results.Sum(result => result.Promoted));
        Assert.Equal(1, results.Sum(result => result.Conflicted));
        Assert.Contains(store.Errors, error => error.FailureStage == "concurrency");

        var currentGenerationId = store
            .GetPathGenerationState("cas")!
            .ArtifactGenerationId;
        Assert.False(string.IsNullOrWhiteSpace(currentGenerationId));
        var winningDirectory = PathArtifactResolver.GetGenerationDirectory(
            _dataDirectory,
            "cas",
            currentGenerationId!);
        Assert.True(Directory.Exists(winningDirectory));

        var rejectedPromotion = Assert.Single(
            store.Promotions,
            promotion => !string.Equals(
                promotion.ArtifactGenerationId,
                currentGenerationId,
                StringComparison.Ordinal));
        Assert.False(Directory.Exists(
            PathArtifactResolver.GetGenerationDirectory(
                _dataDirectory,
                "cas",
                rejectedPromotion.ArtifactGenerationId)));
        Assert.Single(
            Directory.EnumerateDirectories(
                Path.GetDirectoryName(winningDirectory)!));
    }

    [Fact]
    public async Task Rejected_cas_result_preserves_generation_reported_current()
    {
        var chopt = CreateChoptScript();
        var store = new FakePathDataStore
        {
            FailPromotionCall = 1,
            FailedPromotionOutcome = PathGenerationPromotionOutcome.Conflict,
        };
        store.EnsureSong("current-after-conflict");
        store.OnPromotion = store.ForceCurrentGeneration;
        var coordinator = CreateCoordinator(
            chopt,
            store,
            new StaticDatHandler(_encryptedDat));
        var song = CreateSong(
            "current-after-conflict",
            new In { gr = 0 });

        var result = await coordinator.GeneratePathsAsync(
            [song],
            true,
            CancellationToken.None);

        Assert.Equal(1, result.Conflicted);
        var promotion = Assert.Single(store.Promotions);
        Assert.Equal(
            promotion.ArtifactGenerationId,
            store.GetPathGenerationState(song.track!.su!)!.ArtifactGenerationId);
        Assert.True(Directory.Exists(
            PathArtifactResolver.GetGenerationDirectory(
                _dataDirectory,
                song.track.su!,
                promotion.ArtifactGenerationId)));
    }

    [Theory]
    [InlineData("version")]
    [InlineData("binary")]
    [InlineData("profile")]
    public async Task Same_dat_regenerates_when_runtime_identity_changes(string changedField)
    {
        var chopt = CreateChoptScript();
        var store = new FakePathDataStore();
        var coordinator = CreateCoordinator(
            chopt,
            store,
            new StaticDatHandler(_encryptedDat));
        var runtime = await coordinator.DetectRuntimeIdentityAsync(
            chopt,
            CancellationToken.None);
        var storedRuntime = changedField switch
        {
            "version" => runtime with { Version = "0.9.0" },
            "binary" => runtime with { BinarySha256 = new string('0', 64) },
            "profile" => runtime with { Profile = "old-profile" },
            _ => runtime,
        };
        var lastModified = "2026-08-01T00:00:00.0000000Z";
        SeedCompleteGeneration(
            store,
            $"identity-{changedField}",
            "old-generation",
            storedRuntime,
            ["Solo_Guitar"],
            MidiCryptor.ComputeHash(_encryptedDat),
            lastModified);

        var result = await coordinator.GeneratePathsAsync(
            [
                CreateSong(
                    $"identity-{changedField}",
                    new In { gr = 0 },
                    UtcDate(1)),
            ],
            force: false,
            CancellationToken.None);

        Assert.Equal(1, result.Promoted);
        Assert.NotEqual(
            "old-generation",
            store.GetPathGenerationState($"identity-{changedField}")!.ArtifactGenerationId);
    }

    [Fact]
    public async Task Same_dat_regenerates_when_expected_set_changes()
    {
        var chopt = CreateChoptScript();
        var store = new FakePathDataStore();
        var coordinator = CreateCoordinator(
            chopt,
            store,
            new StaticDatHandler(_encryptedDat));
        var runtime = await coordinator.DetectRuntimeIdentityAsync(
            chopt,
            CancellationToken.None);
        var lastModified = "2026-08-01T00:00:00.0000000Z";
        SeedCompleteGeneration(
            store,
            "expected-change",
            "old-generation",
            runtime,
            ["Solo_Guitar"],
            MidiCryptor.ComputeHash(_encryptedDat),
            lastModified);

        var result = await coordinator.GeneratePathsAsync(
            [
                CreateSong(
                    "expected-change",
                    new In { gr = 0, ba = 0 },
                    UtcDate(1)),
            ],
            false,
            CancellationToken.None);

        Assert.Equal(1, result.Promoted);
        var state = store.GetPathGenerationState("expected-change")!;
        Assert.Equal(["Solo_Guitar", "Solo_Bass"], state.ExpectedInstruments);
        Assert.Equal(123_456, state.MaxScores.MaxBassScore);
    }

    [Fact]
    public async Task Same_dat_regenerates_when_current_artifact_set_is_incomplete()
    {
        var chopt = CreateChoptScript();
        var store = new FakePathDataStore();
        var coordinator = CreateCoordinator(
            chopt,
            store,
            new StaticDatHandler(_encryptedDat));
        var runtime = await coordinator.DetectRuntimeIdentityAsync(
            chopt,
            CancellationToken.None);
        var seeded = SeedCompleteGeneration(
            store,
            "incomplete-generation",
            "old-generation",
            runtime,
            ["Solo_Guitar"],
            MidiCryptor.ComputeHash(_encryptedDat),
            "2026-08-01T00:00:00.0000000Z");
        File.Delete(Path.Combine(
            seeded.GenerationDirectory,
            "Solo_Guitar",
            "hard.json"));

        var result = await coordinator.GeneratePathsAsync(
            [
                CreateSong(
                    "incomplete-generation",
                    new In { gr = 0 },
                    UtcDate(1)),
            ],
            false,
            CancellationToken.None);

        Assert.Equal(1, result.Promoted);
        Assert.NotEqual(
            "old-generation",
            store.GetPathGenerationState("incomplete-generation")!.ArtifactGenerationId);
    }

    [Fact]
    public async Task Actual_version_is_parsed_and_persisted()
    {
        var chopt = CreateChoptScript(new ChoptBehavior(Version: "7.8.9-beta.2"));
        var store = new FakePathDataStore();
        store.EnsureSong("version");
        var coordinator = CreateCoordinator(
            chopt,
            store,
            new StaticDatHandler(_encryptedDat));

        var result = await coordinator.GeneratePathsAsync(
            [CreateSong("version", new In { gr = 0 })],
            false,
            CancellationToken.None);

        Assert.Equal(1, result.Promoted);
        Assert.Equal(
            "7.8.9-beta.2",
            store.GetPathGenerationState("version")!.ChoptVersion);
    }

    [Theory]
    [InlineData("chopt-fnf-ew0-s20-json-png-v1", true)]
    [InlineData("chopt-fnf-ew0-s20-json-png-v2", false)]
    [InlineData("chopt-fnf-ew0-s20-json-png-prodrums-v3", false)]
    public async Task Json_schema_validation_follows_generation_profile(
        string profile,
        bool expectedPromotion)
    {
        var chopt = CreateChoptScript(
            new ChoptBehavior(Mode: "legacy-json"));
        var store = new FakePathDataStore();
        store.EnsureSong($"schema-{expectedPromotion}");
        var coordinator = CreateCoordinator(
            chopt,
            store,
            new StaticDatHandler(_encryptedDat),
            profile);

        var result = await coordinator.GeneratePathsAsync(
            [CreateSong($"schema-{expectedPromotion}", new In { gr = 0 })],
            false,
            CancellationToken.None);

        Assert.Equal(expectedPromotion ? 1 : 0, result.Promoted);
        Assert.Equal(expectedPromotion ? 0 : 1, result.Failed);
        Assert.Equal(
            expectedPromotion,
            store.GetPathGenerationState($"schema-{expectedPromotion}")!
                .ArtifactGenerationId is not null);
    }

    [Fact]
    public async Task Unparseable_runtime_version_blocks_download_and_promotion()
    {
        var chopt = CreateChoptScript(new ChoptBehavior(Version: "unknown"));
        var store = new FakePathDataStore();
        store.EnsureSong("bad-version");
        var handler = new StaticDatHandler(_encryptedDat);
        var coordinator = CreateCoordinator(chopt, store, handler);

        var result = await coordinator.GeneratePathsAsync(
            [CreateSong("bad-version", new In { gr = 0 })],
            false,
            CancellationToken.None);

        Assert.Equal(1, result.Failed);
        Assert.Equal(0, handler.RequestCount);
        Assert.Null(store.GetPathGenerationState("bad-version")!.ArtifactGenerationId);
        Assert.Contains(store.Errors, error => error.FailureStage == "runtime_version");
    }

    [Fact]
    public async Task Repeated_failures_append_distinct_error_rows()
    {
        var chopt = CreateChoptScript(new ChoptBehavior(Mode: "malformed-json"));
        var store = new FakePathDataStore();
        store.EnsureSong("errors");
        var coordinator = CreateCoordinator(
            chopt,
            store,
            new StaticDatHandler(_encryptedDat));
        var song = CreateSong("errors", new In { gr = 0 });

        await coordinator.GeneratePathsAsync([song], true, CancellationToken.None);
        await coordinator.GeneratePathsAsync([song], true, CancellationToken.None);

        Assert.Equal(2, store.Errors.Count);
        Assert.Equal(2, store.Errors.Select(error => error.AttemptId).Distinct().Count());
        Assert.All(store.Errors, error => Assert.True(error.Detail.Length <= 2048));
    }

    private PathGenerationCoordinator CreateCoordinator(
        string choptPath,
        FakePathDataStore store,
        HttpMessageHandler? handler = null,
        string profile =
            "chopt-fnf-ew0-s20-json-png-prodrums-v3",
        SongsCacheService? cache = null,
        IOptions<ScraperOptions>? configuredOptions = null,
        IPathGenerationAdmissionLeaseProvider? admissionLeaseProvider = null,
        ScrapeProgressTracker? progress = null)
    {
        var options = configuredOptions ?? CreateOptions(choptPath, profile);
        return new PathGenerationCoordinator(
            new HttpClient(handler ?? new StaticDatHandler(_encryptedDat)),
            store,
            cache ?? new SongsCacheService(),
            options,
            progress ?? new ScrapeProgressTracker(),
            NullLogger<PathGenerationCoordinator>.Instance,
            admissionLeaseProvider
                ?? UncontendedPathGenerationAdmissionLeaseProvider.Instance);
    }

    private static string CreateAdmissionConnectionString(
        InMemoryMetaDatabase database)
    {
        var databaseSettings = new NpgsqlConnectionStringBuilder(
            database.DataSource.ConnectionString);
        var connectionString = new NpgsqlConnectionStringBuilder(
            SharedPostgresContainer.ConnectionString)
        {
            Database = databaseSettings.Database,
        };
        return connectionString.ConnectionString;
    }

    private IOptions<ScraperOptions> CreateOptions(
        string choptPath,
        string profile =
            "chopt-fnf-ew0-s20-json-png-prodrums-v3",
        bool automaticPathGeneration = true)
        => Options.Create(new ScraperOptions
        {
            DataDirectory = _dataDirectory,
            CHOptPath = choptPath,
            MidiEncryptionKey = Convert.ToHexString(_midiKey),
            EnablePathGeneration = true,
            EnableAutomaticPathGeneration = automaticPathGeneration,
            PathGenerationParallelism = 2,
            PathGenerationProfile = profile,
        });

    private Song CreateSong(
        string songId,
        In intensity,
        DateTime? lastModified = null)
        => new()
        {
            track = new Track
            {
                su = songId,
                tt = $"Song {songId}",
                an = "Artist",
                mu = $"https://example.invalid/{songId}.dat",
                @in = intensity,
            },
            lastModified = lastModified ?? DateTime.MinValue,
        };

    private SeededGeneration SeedCompleteGeneration(
        FakePathDataStore store,
        string songId,
        string generationId,
        PathGenerationRuntimeIdentity runtime,
        string[] expected,
        string datHash,
        string? lastModified)
    {
        var generationDirectory = PathArtifactResolver.GetGenerationDirectory(
            _dataDirectory,
            songId,
            generationId);
        Directory.CreateDirectory(generationDirectory);
        var scores = new SongMaxScores
        {
            GeneratedAt = DateTime.UtcNow.AddDays(-1).ToString("o"),
            CHOptVersion = runtime.Version,
            CHOptBinarySha256 = runtime.BinarySha256,
            GenerationProfile = runtime.Profile,
            ArtifactGenerationId = generationId,
            ExpectedInstruments = expected,
        };
        var expertScores = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var instrument in expected)
        {
            scores.SetByInstrument(instrument, 111_111);
            expertScores[instrument] = 111_111;
            var instrumentDirectory = Path.Combine(
                generationDirectory,
                instrument);
            Directory.CreateDirectory(instrumentDirectory);
            foreach (var difficulty in PathGenerationInstruments.Difficulties)
            {
                WriteValidPng(Path.Combine(
                    instrumentDirectory,
                    $"{difficulty}.png"));
                File.WriteAllText(
                    Path.Combine(instrumentDirectory, $"{difficulty}.json"),
                    BuildValidPathJson(
                        difficulty == "expert"
                            ? 111_111
                            : 100,
                        difficulty));
            }
        }

        File.WriteAllText(
            Path.Combine(
                generationDirectory,
                PathArtifactResolver.ManifestFileName),
            System.Text.Json.JsonSerializer.Serialize(
                new PathArtifactManifest(
                    generationId,
                    songId,
                    datHash,
                    lastModified,
                    runtime.Version,
                    runtime.BinarySha256,
                    runtime.Profile,
                    expected,
                    expertScores,
                    DateTime.UtcNow.AddDays(-1)),
                PathArtifactManifest.JsonOptions));
        store.Seed(new PathGenerationState(
            songId,
            1,
            datHash,
            lastModified,
            DateTime.UtcNow.AddDays(-1),
            runtime.Version,
            runtime.BinarySha256,
            runtime.Profile,
            generationId,
            expected,
            scores));
        return new SeededGeneration(generationDirectory);
    }

    private string CreateChoptScript(ChoptBehavior? behavior = null)
    {
        behavior ??= new ChoptBehavior();
        if (OperatingSystem.IsWindows())
            return CreateWindowsChoptScript(behavior);

        var path = Path.Combine(
            _dataDirectory,
            $"fake-chopt-{Guid.NewGuid():N}.sh");
        var invocationLog = ShellQuote(behavior.InvocationLog ?? "");
        var childPidPath = ShellQuote(behavior.ChildPidPath ?? "");
        var startedPath = ShellQuote(behavior.StartedPath ?? "");
        var script = $$"""
            #!/bin/sh
            if [ "$1" = "--version" ]; then
              echo "CHOpt {{behavior.Version}}"
              exit {{behavior.VersionExitCode}}
            fi
            out=""
            instrument=""
            difficulty=""
            no_pro_drums=false
            while [ "$#" -gt 0 ]; do
              case "$1" in
                -o) out="$2"; shift ;;
                -i) instrument="$2"; shift ;;
                -d) difficulty="$2"; shift ;;
                --no-pro-drums) no_pro_drums=true ;;
              esac
              shift
            done
            if [ -n {{invocationLog}} ]; then
              printf '%s\n' "-i $instrument -d $difficulty --no-pro-drums=$no_pro_drums -o $out" >> {{invocationLog}}
            fi
            if [ "{{behavior.Mode}}" = "process-tree" ]; then
              sleep 30 &
              child="$!"
              printf '%s' "$child" > {{childPidPath}}
              printf 'started' > {{startedPath}}
              wait "$child"
            fi
            if [ "$instrument" = "{{behavior.FailInstrument}}" ] && [ "$difficulty" = "{{behavior.FailDifficulty}}" ]; then
              echo "forced failure" >&2
              exit 7
            fi
            case "{{behavior.Mode}}" in
              missing-png) ;;
              empty-png) : > "$out" ;;
              bad-png) printf 'not-a-png' > "$out" ;;
              signature-only-png) printf '\211PNG\r\n\032\n' > "$out" ;;
              *) printf '%s' '{{ValidPngBase64}}' | base64 -d > "$out" ;;
            esac
            case "{{behavior.Mode}}" in
              malformed-json) printf '{' ;;
              legacy-json) printf '%s' '{{BuildLegacyPathJson(123_456, "expert")}}' ;;
              missing-notes) printf '%s' '{{BuildValidPathJson(123_456, "expert").Replace(",\"notes\":[]", "", StringComparison.Ordinal)}}' ;;
              zero-expert)
                if [ "$difficulty" = "expert" ]; then
                  printf '%s' '{{BuildValidPathJson(0, "expert")}}'
                else
                  printf '%s' '{{BuildValidPathJson(100, "easy")}}'
                fi
                ;;
              *) printf '%s' '{{BuildValidPathJson(123_456, "expert")}}' ;;
            esac
            """;
        File.WriteAllText(path, script);
        File.SetUnixFileMode(
            path,
            UnixFileMode.UserRead |
            UnixFileMode.UserWrite |
            UnixFileMode.UserExecute);
        return path;
    }

    private string CreateWindowsChoptScript(ChoptBehavior behavior)
    {
        var path = Path.Combine(
            _dataDirectory,
            $"fake-chopt-{Guid.NewGuid():N}.bat");
        var mode = behavior.Mode.Replace("\"", "");
        var invocationLog =
            (behavior.InvocationLog ?? "").Replace("\"", "");
        var script = $$"""
            @echo off
            if "%~1"=="--version" (
              echo CHOpt {{behavior.Version}}
              exit /b {{behavior.VersionExitCode}}
            )
            set "out="
            set "instrument="
            set "difficulty="
            set "noProDrums=false"
            :parse
            if "%~1"=="" goto done
            if "%~1"=="-o" set "out=%~2"
            if "%~1"=="-i" set "instrument=%~2"
            if "%~1"=="-d" set "difficulty=%~2"
            if "%~1"=="--no-pro-drums" set "noProDrums=true"
            shift
            goto parse
            :done
            if not "{{invocationLog}}"=="" echo -i %instrument% -d %difficulty% --no-pro-drums=%noProDrums% -o %out%>>"{{invocationLog}}"
            if "{{mode}}"=="missing-png" goto json
            if "{{mode}}"=="empty-png" type nul > "%out%" & goto json
            if "{{mode}}"=="bad-png" echo bad> "%out%" & goto json
            if "{{mode}}"=="signature-only-png" powershell -NoProfile -Command "[IO.File]::WriteAllBytes('%out%', [byte[]](137,80,78,71,13,10,26,10))" & goto json
            powershell -NoProfile -Command "[IO.File]::WriteAllBytes('%out%', [Convert]::FromBase64String('{{ValidPngBase64}}'))"
            :json
            if "{{mode}}"=="malformed-json" echo {
            if "{{mode}}"=="legacy-json" echo {{BuildLegacyPathJson(123_456, "expert")}}
            if "{{mode}}"=="missing-notes" echo {{BuildValidPathJson(123_456, "expert").Replace(",\"notes\":[]", "", StringComparison.Ordinal)}}
            if "{{mode}}"=="zero-expert" echo {{BuildValidPathJson(0, "expert")}}
            if not "{{mode}}"=="malformed-json" if not "{{mode}}"=="legacy-json" if not "{{mode}}"=="missing-notes" if not "{{mode}}"=="zero-expert" echo {{BuildValidPathJson(123_456, "expert")}}
            """;
        File.WriteAllText(path, script);
        return path;
    }

    private static string ShellQuote(string value)
        => $"'{value.Replace("'", "'\"'\"'")}'";

    private static async Task WaitForFileAsync(
        string path,
        TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(path))
                return;
            await Task.Delay(25);
        }

        throw new TimeoutException($"File was not created: {path}");
    }

    private static async Task WaitForProcessExitAsync(
        int processId,
        TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var process = Process.GetProcessById(processId);
                if (process.HasExited)
                    return;
            }
            catch (ArgumentException)
            {
                return;
            }

            await Task.Delay(50);
        }

        Assert.Fail($"Child process {processId} was still running after cancellation.");
    }

    private static void WriteValidPng(string path)
        => File.WriteAllBytes(
            path,
            Convert.FromBase64String(ValidPngBase64));

    private static string BuildValidPathJson(
        int totalScore,
        string difficulty)
        => $$"""{"schemaVersion":2,"songName":"Song","artist":"Artist","charter":"Charter","difficulty":"{{difficulty}}","totalScore":{{totalScore}},"pathSummary":"","activations":[],"notes":[],"spPhrases":[],"measures":[],"bpms":[],"timeSignatures":[]}""";

    private static string BuildLegacyPathJson(
        int totalScore,
        string difficulty)
        => $$"""{"songName":"Song","artist":"Artist","charter":"Charter","difficulty":"{{difficulty}}","totalScore":{{totalScore}},"pathSummary":"","activations":[],"notes":[],"spPhrases":[],"measures":[],"bpms":[],"timeSignatures":[]}""";

    private void AssertNoStagingAttempts()
    {
        var workDirectory = Path.Combine(_dataDirectory, ".path-work");
        if (Directory.Exists(workDirectory))
            Assert.Empty(Directory.EnumerateDirectories(workDirectory));
    }

    private static DateTime UtcDate(int day)
        => new(2026, 8, day, 0, 0, 0, DateTimeKind.Utc);

    private static byte[] BuildMinimalMidi()
    {
        using var stream = new MemoryStream();
        stream.Write("MThd"u8);
        WriteBigEndian32(stream, 6);
        WriteBigEndian16(stream, 1);
        WriteBigEndian16(stream, 1);
        WriteBigEndian16(stream, 480);
        var track = new byte[] { 0x00, 0xff, 0x2f, 0x00 };
        stream.Write("MTrk"u8);
        WriteBigEndian32(stream, track.Length);
        stream.Write(track);
        return stream.ToArray();
    }

    private static byte[] EncryptMidi(byte[] midi, byte[] key)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.Zeros;
        var padded = new byte[(midi.Length + 15) / 16 * 16];
        Array.Copy(midi, padded, midi.Length);
        return aes.CreateEncryptor().TransformFinalBlock(
            padded,
            0,
            padded.Length);
    }

    private static void WriteBigEndian32(Stream stream, int value)
    {
        stream.WriteByte((byte)(value >> 24));
        stream.WriteByte((byte)(value >> 16));
        stream.WriteByte((byte)(value >> 8));
        stream.WriteByte((byte)value);
    }

    private static void WriteBigEndian16(Stream stream, int value)
    {
        stream.WriteByte((byte)(value >> 8));
        stream.WriteByte((byte)value);
    }

    private sealed record ChoptBehavior(
        string Version = "1.10.3",
        int VersionExitCode = 0,
        string Mode = "success",
        string FailInstrument = "",
        string FailDifficulty = "",
        string? InvocationLog = null,
        string? ChildPidPath = null,
        string? StartedPath = null);

    private sealed record SeededGeneration(string GenerationDirectory);

    private sealed class BlockingPathGenerationAdmissionLeaseProvider
        : IPathGenerationAdmissionLeaseProvider
    {
        public static BlockingPathGenerationAdmissionLeaseProvider Instance { get; } =
            new();

        public async Task<IAsyncDisposable> AcquireAsync(CancellationToken ct)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            throw new UnreachableException();
        }
    }

    private sealed class FailingPathGenerationAdmissionLeaseProvider
        : IPathGenerationAdmissionLeaseProvider
    {
        public static FailingPathGenerationAdmissionLeaseProvider Instance { get; } =
            new();

        public Task<IAsyncDisposable> AcquireAsync(CancellationToken ct)
            => Task.FromException<IAsyncDisposable>(
                new InvalidOperationException(
                    "injected admission failure"));
    }

    private sealed class StaticDatHandler(byte[] content) : HttpMessageHandler
    {
        private int _requestCount;
        private readonly ConcurrentQueue<string> _requestPaths = new();
        public int RequestCount => Volatile.Read(ref _requestCount);
        public IReadOnlyList<string> RequestPaths => _requestPaths.ToArray();

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requestCount);
            _requestPaths.Enqueue(request.RequestUri?.AbsolutePath ?? "");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(content),
            });
        }
    }

    private sealed class FakePathDataStore : IPathDataStore
    {
        private readonly ConcurrentDictionary<string, PathGenerationState> _states =
            new(StringComparer.Ordinal);
        private readonly HashSet<string> _pending =
            new(StringComparer.Ordinal);
        private readonly object _promotionLock = new();
        private readonly TaskCompletionSource _promotionBarrier =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _errorWriteStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _errorWriteRelease =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _promotionArrivals;
        private int _errorWriteAttempts;

        public List<PathGenerationError> Errors { get; } = [];
        public List<PathGenerationPromotion> Promotions { get; } = [];
        public bool ThrowOnPromotion { get; set; }
        public bool ThrowOnStateRead { get; set; }
        public bool BlockErrorWrites { get; set; }
        public int PromotionBarrierCount { get; set; }
        public int ErrorWriteAttempts =>
            Volatile.Read(ref _errorWriteAttempts);
        public Task ErrorWriteStarted => _errorWriteStarted.Task;
        public int? FailPromotionCall { get; set; }
        public PathGenerationPromotionOutcome FailedPromotionOutcome { get; set; } =
            PathGenerationPromotionOutcome.Conflict;
        public Action<PathGenerationPromotion>? OnPromotion { get; set; }

        public void EnsureSong(string songId)
        {
            Seed(new PathGenerationState(
                songId,
                0,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                [],
                new SongMaxScores()));
            _pending.Add(songId);
        }

        public void Seed(PathGenerationState state)
            => _states[state.SongId] = state;

        public void ForceCurrentGeneration(
            PathGenerationPromotion promotion)
        {
            lock (_promotionLock)
            {
                if (!_states.TryGetValue(promotion.SongId, out var current))
                {
                    throw new InvalidOperationException(
                        $"Missing test song {promotion.SongId}.");
                }

                ApplyPromotion(promotion, current);
            }
        }

        public void ReleaseErrorWrites()
            => _errorWriteRelease.TrySetResult();

        public Dictionary<string, PathGenerationState> GetPathGenerationStates()
        {
            if (ThrowOnStateRead)
            {
                throw new InvalidOperationException(
                    "Injected path state read failure.");
            }

            return _states.ToDictionary(
                pair => pair.Key,
                pair => pair.Value,
                StringComparer.Ordinal);
        }

        public PathGenerationState? GetPathGenerationState(string songId)
            => _states.TryGetValue(songId, out var state) ? state : null;

        public HashSet<string> GetPendingPathGenerationSongIds()
            => new(_pending, StringComparer.Ordinal);

        public Dictionary<string, SongMaxScores> GetAllMaxScores()
            => _states
                .Where(pair => pair.Value.MaxScores.GetByInstrument("Solo_Guitar") is not null ||
                               pair.Value.MaxScores.GetByInstrument("Solo_Bass") is not null ||
                               pair.Value.MaxScores.GetByInstrument("Solo_Drums") is not null ||
                               pair.Value.MaxScores.GetByInstrument("Solo_Vocals") is not null ||
                               pair.Value.MaxScores.GetByInstrument("Solo_PeripheralGuitar") is not null ||
                               pair.Value.MaxScores.GetByInstrument("Solo_PeripheralBass") is not null ||
                               pair.Value.MaxScores.GetByInstrument("Solo_PeripheralCymbals") is not null ||
                               pair.Value.MaxScores.GetByInstrument("Solo_PeripheralDrums") is not null)
                .ToDictionary(
                    pair => pair.Key,
                    pair => pair.Value.MaxScores,
                    StringComparer.Ordinal);

        public async Task<PathGenerationPromotionOutcome> TryPromoteGenerationAsync(
            PathGenerationPromotion promotion,
            CancellationToken ct)
        {
            int promotionCall;
            lock (Promotions)
            {
                Promotions.Add(promotion);
                promotionCall = Promotions.Count;
            }
            OnPromotion?.Invoke(promotion);
            if (FailPromotionCall == promotionCall)
                return FailedPromotionOutcome;

            if (PromotionBarrierCount > 0)
            {
                if (Interlocked.Increment(ref _promotionArrivals) >= PromotionBarrierCount)
                    _promotionBarrier.TrySetResult();
                await _promotionBarrier.Task.WaitAsync(ct);
            }

            if (ThrowOnPromotion)
                throw new InvalidOperationException("Injected persistence failure.");

            lock (_promotionLock)
            {
                if (!_states.TryGetValue(promotion.SongId, out var current))
                    return PathGenerationPromotionOutcome.SongMissing;
                if (current.Revision != promotion.ExpectedRevision)
                    return PathGenerationPromotionOutcome.Conflict;

                ApplyPromotion(promotion, current);
                return PathGenerationPromotionOutcome.Promoted;
            }
        }

        private void ApplyPromotion(
            PathGenerationPromotion promotion,
            PathGenerationState current)
        {
            var scores = promotion.MaxScores;
            scores.GeneratedAt = promotion.GeneratedAtUtc.ToString("o");
            scores.CHOptVersion = promotion.Runtime.Version;
            scores.CHOptBinarySha256 = promotion.Runtime.BinarySha256;
            scores.GenerationProfile = promotion.Runtime.Profile;
            scores.ArtifactGenerationId = promotion.ArtifactGenerationId;
            scores.ExpectedInstruments = promotion.ExpectedInstruments.ToArray();
            _states[promotion.SongId] = new PathGenerationState(
                promotion.SongId,
                current.Revision + 1,
                promotion.DatFileHash,
                promotion.SongLastModified,
                promotion.GeneratedAtUtc,
                promotion.Runtime.Version,
                promotion.Runtime.BinarySha256,
                promotion.Runtime.Profile,
                promotion.ArtifactGenerationId,
                promotion.ExpectedInstruments.ToArray(),
                scores,
                current.CatalogLastModified,
                PathGenerationPending: false);
            _pending.Remove(promotion.SongId);
        }

        public async Task AppendPathGenerationErrorAsync(
            PathGenerationError error,
            CancellationToken ct)
        {
            Interlocked.Increment(ref _errorWriteAttempts);
            if (BlockErrorWrites)
            {
                _errorWriteStarted.TrySetResult();
                await _errorWriteRelease.Task.WaitAsync(ct);
            }

            lock (Errors)
                Errors.Add(error);
        }

    }
}
