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
}