using FSTService.Persistence;
using FSTService.Scraping;

namespace FSTService.Tests.Unit;

/// <summary>
/// Tests for <see cref="CyclicalSongMachine"/> static helpers
/// and backfill integration behavior.
/// </summary>
public class CyclicalSongMachineTests
{
    // ── DeduplicateUsers: AlreadyChecked merging ────────────────

    [Fact]
    public void DeduplicateUsers_MergesAlreadyChecked_WithUnion()
    {
        // When two work items for the same account overlap, AlreadyChecked
        // should be the UNION so no previously checked work is repeated.
        var users = new List<UserWorkItem>
        {
            new()
            {
                AccountId = "user1",
                Purposes = WorkPurpose.Backfill,
                AllTimeNeeded = true,
                SeasonsNeeded = new HashSet<int> { 1 },
                AlreadyChecked = new HashSet<(string, string)> { ("songA", "Solo_Guitar"), ("songB", "Solo_Bass") },
            },
            new()
            {
                AccountId = "user1",
                Purposes = WorkPurpose.PostScrape,
                AllTimeNeeded = true,
                SeasonsNeeded = new HashSet<int> { 2 },
                AlreadyChecked = new HashSet<(string, string)> { ("songB", "Solo_Bass"), ("songC", "Solo_Drums") },
            },
        };

        var result = InvokeDeduplicateUsers(users);

        Assert.Single(result);
        var merged = result[0];
        Assert.Equal("user1", merged.AccountId);
        Assert.True(merged.Purposes.HasFlag(WorkPurpose.Backfill));
        Assert.True(merged.Purposes.HasFlag(WorkPurpose.PostScrape));
        Assert.Contains(1, merged.SeasonsNeeded);
        Assert.Contains(2, merged.SeasonsNeeded);

        // Union: all three pairs should be present
        Assert.NotNull(merged.AlreadyChecked);
        Assert.Equal(3, merged.AlreadyChecked!.Count);
        Assert.Contains(("songA", "Solo_Guitar"), merged.AlreadyChecked);
        Assert.Contains(("songB", "Solo_Bass"), merged.AlreadyChecked);
        Assert.Contains(("songC", "Solo_Drums"), merged.AlreadyChecked);
    }

    [Fact]
    public void DeduplicateUsers_NullAlreadyChecked_ProducesNull()
    {
        var users = new List<UserWorkItem>
        {
            new()
            {
                AccountId = "user1",
                Purposes = WorkPurpose.Backfill,
                AllTimeNeeded = true,
                SeasonsNeeded = new HashSet<int> { 1 },
                AlreadyChecked = null,
            },
            new()
            {
                AccountId = "user1",
                Purposes = WorkPurpose.PostScrape,
                AllTimeNeeded = false,
                SeasonsNeeded = new HashSet<int>(),
                AlreadyChecked = null,
            },
        };

        var result = InvokeDeduplicateUsers(users);

        Assert.Single(result);
        Assert.Null(result[0].AlreadyChecked);
        Assert.True(result[0].AllTimeNeeded);
    }

    [Fact]
    public void DeduplicateUsers_OneNullOnePopulated_KeepsPopulated()
    {
        var users = new List<UserWorkItem>
        {
            new()
            {
                AccountId = "user1",
                Purposes = WorkPurpose.Backfill,
                AllTimeNeeded = true,
                SeasonsNeeded = new HashSet<int>(),
                AlreadyChecked = null,
            },
            new()
            {
                AccountId = "user1",
                Purposes = WorkPurpose.PostScrape,
                AllTimeNeeded = false,
                SeasonsNeeded = new HashSet<int>(),
                AlreadyChecked = new HashSet<(string, string)> { ("songA", "Solo_Guitar") },
            },
        };

        var result = InvokeDeduplicateUsers(users);

        Assert.Single(result);
        // The union of null + {"songA/Solo_Guitar"} should be just the one entry
        Assert.NotNull(result[0].AlreadyChecked);
        Assert.Single(result[0].AlreadyChecked!);
    }

    [Fact]
    public void DeduplicateUsers_DistinctAccounts_NoMerge()
    {
        var users = new List<UserWorkItem>
        {
            new()
            {
                AccountId = "user1",
                Purposes = WorkPurpose.Backfill,
                AllTimeNeeded = true,
                SeasonsNeeded = new HashSet<int> { 1 },
            },
            new()
            {
                AccountId = "user2",
                Purposes = WorkPurpose.PostScrape,
                AllTimeNeeded = false,
                SeasonsNeeded = new HashSet<int> { 2 },
            },
        };

        var result = InvokeDeduplicateUsers(users);

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public async Task MachineAttachment_RecordSongResult_AllowsConcurrentUpdates()
    {
        var attachment = CreateAttachment(
            Enumerable.Range(0, 1_000).Select(i => $"song-{i}").ToArray());

        var result = new SongProcessingMachine.SongStepResult
        {
            EntriesUpdated = 1,
            SessionsInserted = 2,
            ApiCalls = 3,
        };

        await Task.WhenAll(Enumerable.Range(0, 5_000)
            .Select(i => Task.Run(() => attachment.RecordSongResult(i % 1_000, result))));

        Assert.Equal(5_000, attachment.TotalEntriesUpdated);
        Assert.Equal(10_000, attachment.TotalSessionsInserted);
        Assert.Equal(15_000, attachment.TotalApiCalls);

        attachment.StampJoinIndex(0);
        attachment.MarkCyclePassComplete();

        Assert.True(attachment.IsFullyComplete);
        Assert.Empty(attachment.GetMissingSongIndices(1_000));
    }

    [Fact]
    public async Task MachineAttachment_TryFault_CompletesAwaiterWithException()
    {
        var attachment = CreateAttachment(["song-1"]);
        var expected = new InvalidOperationException("cycle failed");

        attachment.TryFault(expected);

        var actual = await Assert.ThrowsAsync<InvalidOperationException>(() => attachment.Completion.Task);
        Assert.Same(expected, actual);
        Assert.True(attachment.IsCompleted);
    }

    [Fact]
    public void ShouldClearProgressWhenIdle_PreservesPostScrapeOwnedPhase()
    {
        var preserve = CreateAttachment(["song-1"], preserveProgressPhaseOnIdle: true);
        var normal = CreateAttachment(["song-1"]);

        Assert.False(CyclicalSongMachine.ShouldClearProgressWhenIdle(
            ScrapeProgressTracker.ScrapePhase.SongMachine,
            [preserve]));

        Assert.True(CyclicalSongMachine.ShouldClearProgressWhenIdle(
            ScrapeProgressTracker.ScrapePhase.SongMachine,
            [normal]));

        Assert.False(CyclicalSongMachine.ShouldClearProgressWhenIdle(
            ScrapeProgressTracker.ScrapePhase.BandScraping,
            [normal]));
    }

    // ── Helper: invoke private static DeduplicateUsers via reflection ──

    private static CyclicalSongMachine.MachineAttachment CreateAttachment(
        IReadOnlyList<string> songIds,
        bool preserveProgressPhaseOnIdle = false)
    {
        return new CyclicalSongMachine.MachineAttachment(
            callerId: "test-caller",
            users:
            [
                new UserWorkItem
                {
                    AccountId = "user1",
                    Purposes = WorkPurpose.PostScrape,
                    AllTimeNeeded = true,
                    SeasonsNeeded = [],
                },
            ],
            songIds: songIds,
            seasonWindows: Array.Empty<SeasonWindowInfo>(),
            source: SongMachineSource.PostScrape,
            isHighPriority: true,
                preserveProgressPhaseOnIdle: preserveProgressPhaseOnIdle,
            callerCt: CancellationToken.None);
    }

    private static List<UserWorkItem> InvokeDeduplicateUsers(List<UserWorkItem> users)
    {
        var method = typeof(CyclicalSongMachine)
            .GetMethod("DeduplicateUsers",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);
        return (List<UserWorkItem>)method!.Invoke(null, [users])!;
    }
}
