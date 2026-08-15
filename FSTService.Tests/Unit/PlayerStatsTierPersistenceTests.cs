using FSTService.Persistence;
using FSTService.Tests.Helpers;

namespace FSTService.Tests.Unit;

public sealed class PlayerStatsTierPersistenceTests : IDisposable
{
    private readonly InMemoryMetaDatabase _fixture = new();

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public void UpsertPlayerStatsTiersBatch_large_batch_inserts_and_updates_rows()
    {
        var initialRows = Enumerable.Range(0, 40)
            .Select(i => new PlayerStatsTiersRow
            {
                AccountId = "acct_large",
                Instrument = $"Instrument_{i}",
                TiersJson = $"[{{\"sp\":{i}}}]",
            })
            .ToList();

        _fixture.Db.UpsertPlayerStatsTiersBatch(initialRows);

        var rows = _fixture.Db.GetPlayerStatsTiers("acct_large");
        Assert.Equal(40, rows.Count);
        Assert.Contains(rows, row => row.Instrument == "Instrument_17" && row.TiersJson.Contains("17", StringComparison.Ordinal));

        _fixture.Db.UpsertPlayerStatsTiersBatch([
            new PlayerStatsTiersRow
            {
                AccountId = "acct_large",
                Instrument = "Instrument_17",
                TiersJson = "[{\"sp\":999}]",
            },
        ]);

        rows = _fixture.Db.GetPlayerStatsTiers("acct_large");
        Assert.Equal(40, rows.Count);
        Assert.Contains(rows, row => row.Instrument == "Instrument_17" && row.TiersJson.Contains("999", StringComparison.Ordinal));
    }

    [Fact]
    public void Max_score_replacement_removes_stale_affected_tiers_and_preserves_unrelated_accounts()
    {
        _fixture.Db.UpsertPlayerStatsTiersBatch(
        [
            Row("affected", "Overall", 1),
            Row("affected", "Solo_Guitar", 2),
            Row("affected", "Solo_Drums", 3),
            Row("unrelated", "Overall", 4),
            Row("unrelated", "Solo_Drums", 5),
        ]);

        using (var connection =
               _fixture.DataSource.OpenConnection())
        using (var transaction =
               connection.BeginTransaction())
        {
            _fixture.Db
                .ReplacePlayerStatsTiersForMaxScoreMaintenance(
                    ["affected"],
                    ["Solo_Guitar", "Solo_Bass"],
                    [
                        Row("affected", "Overall", 10),
                        Row(
                            "affected",
                            "Solo_Guitar",
                            20),
                    ],
                    connection,
                    transaction);
            transaction.Commit();
        }

        var affected = _fixture.Db
            .GetPlayerStatsTiers("affected")
            .OrderBy(row => row.Instrument)
            .ToArray();
        Assert.Equal(
            ["Overall", "Solo_Guitar"],
            affected.Select(row => row.Instrument));
        Assert.All(
            affected,
            row => Assert.DoesNotContain(
                "\"sp\":3",
                row.TiersJson,
                StringComparison.Ordinal));

        var unrelated = _fixture.Db
            .GetPlayerStatsTiers("unrelated")
            .OrderBy(row => row.Instrument)
            .ToArray();
        Assert.Equal(
            ["Overall", "Solo_Drums"],
            unrelated.Select(row => row.Instrument));
        Assert.Contains(
            unrelated,
            row => row.Instrument == "Solo_Drums"
                   && row.TiersJson.Contains(
                       "\"sp\": 5",
                       StringComparison.Ordinal));
    }

    private static PlayerStatsTiersRow Row(
        string accountId,
        string instrument,
        int scorePercent)
        => new()
        {
            AccountId = accountId,
            Instrument = instrument,
            TiersJson =
                $"[{{\"sp\":{scorePercent}}}]",
        };
}