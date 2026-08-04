using FSTService.Scraping;

namespace FSTService.Tests.Unit;

public sealed class SoloFamilyRankingBuilderTests
{
    [Fact]
    public void PadUsesMaximumCanonicalInstrumentDenominator()
    {
        var perInstrument = EmptyInstrumentRows();
        perInstrument["Solo_Guitar"]["max"] =
            Metrics(697, 697, 697, totalScore: 11);
        perInstrument["Solo_Guitar"]["retained-696"] =
            Metrics(696, 696, 696, totalScore: 13);
        perInstrument["Solo_Bass"]["max"] =
            Metrics(697, 697, 697, totalScore: 17);
        perInstrument["Solo_Drums"]["max"] =
            Metrics(697, 697, 697, totalScore: 19);
        perInstrument["Solo_Vocals"]["max"] =
            Metrics(697, 697, 697, totalScore: 23);

        var catalog = EmptyCatalogDenominators();
        catalog["Solo_Guitar"] = 695;
        catalog["Solo_Bass"] = 697;
        catalog["Solo_Drums"] = 697;
        catalog["Solo_Vocals"] = 697;

        var result = Build(perInstrument, catalog);
        var padRows = result.Rankings
            .Where(row => row.ScopeId == SoloFamilyRankingScopes.Pad)
            .ToArray();

        Assert.Equal(2, padRows.Length);
        Assert.All(
            padRows,
            row => Assert.Equal(2788, row.TotalChartedSongs));
        var max = Assert.Single(padRows, row => row.AccountId == "max");
        Assert.Equal(2788, max.SongsPlayed);
        Assert.Equal(2788, max.FullComboCount);
        Assert.Equal(70, max.TotalScore);
        Assert.Equal(1.0, max.Coverage, precision: 12);
        Assert.Equal(1.0, max.FcRate, precision: 12);
        Assert.Equal(0, result.InvalidRowCount);
        Assert.Equal(2788, result.ScopeDenominators[
            SoloFamilyRankingScopes.Pad]);

        var guitar = Assert.Single(
            result.InstrumentDenominators,
            row => row.Instrument == "Solo_Guitar");
        Assert.Equal(695, guitar.CatalogDenominator);
        Assert.Equal(697, guitar.CanonicalDenominator);
        Assert.Equal(697, guitar.EffectiveDenominator);
        Assert.True(guitar.IsOverride);

        Assert.All(
            padRows,
            row =>
            {
                Assert.True(row.SongsPlayed <= row.TotalChartedSongs);
                Assert.True(row.FullComboCount <= row.TotalChartedSongs);
                Assert.True(
                    (float)row.Coverage <=
                    1f + (float)SoloFamilyRankingBuilder.RateTolerance);
                Assert.True(
                    (float)row.FcRate <=
                    1f + (float)SoloFamilyRankingBuilder.RateTolerance);
            });
    }

    [Fact]
    public void NormalCatalogKeepsMissingSlotInCoverageAndFcDenominator()
    {
        var perInstrument = EmptyInstrumentRows();
        foreach (var instrument in SoloFamilyRankingScopes.All[0].Instruments)
        {
            perInstrument[instrument]["complete"] =
                Metrics(1, 1, 1, totalScore: 100);
        }
        perInstrument["Solo_Guitar"]["partial"] =
            Metrics(1, 1, 1, totalScore: 50);

        var catalog = EmptyCatalogDenominators();
        foreach (var instrument in SoloFamilyRankingScopes.All[0].Instruments)
            catalog[instrument] = 1;

        var result = Build(perInstrument, catalog);
        var complete = Assert.Single(
            result.Rankings,
            row => row.ScopeId == SoloFamilyRankingScopes.Pad
                   && row.AccountId == "complete");
        var partial = Assert.Single(
            result.Rankings,
            row => row.ScopeId == SoloFamilyRankingScopes.Pad
                   && row.AccountId == "partial");

        Assert.Equal(4, complete.TotalChartedSongs);
        Assert.Equal(4, complete.SongsPlayed);
        Assert.Equal(1.0, complete.FcRate, precision: 12);
        Assert.Equal(4, partial.TotalChartedSongs);
        Assert.Equal(1, partial.SongsPlayed);
        Assert.Equal(0.25, partial.Coverage, precision: 12);
        Assert.Equal(0.25, partial.FcRate, precision: 12);
        Assert.DoesNotContain(
            result.InstrumentDenominators,
            row => row.IsOverride);
        Assert.Equal(0, result.InvalidRowCount);
    }

    [Fact]
    public void MultipleScopesPreserveCatalogSlotsAndEmptyInstrumentRows()
    {
        var perInstrument = EmptyInstrumentRows();
        perInstrument["Solo_PeripheralGuitar"]["strings"] =
            Metrics(2, 1, 2);
        perInstrument["Solo_PeripheralDrums"]["drums"] =
            Metrics(1, 1, 1);

        var catalog = EmptyCatalogDenominators();
        catalog["Solo_Guitar"] = 1;
        catalog["Solo_Bass"] = 1;
        catalog["Solo_Drums"] = 1;
        catalog["Solo_Vocals"] = 1;
        catalog["Solo_PeripheralGuitar"] = 2;
        catalog["Solo_PeripheralBass"] = 3;

        var result = Build(perInstrument, catalog);

        Assert.Equal(4, result.ScopeDenominators[
            SoloFamilyRankingScopes.Pad]);
        Assert.DoesNotContain(
            result.Rankings,
            row => row.ScopeId == SoloFamilyRankingScopes.Pad);

        var strings = Assert.Single(
            result.Rankings,
            row => row.ScopeId == SoloFamilyRankingScopes.ProStrings);
        Assert.Equal(5, strings.TotalChartedSongs);
        Assert.Equal(2.0 / 5.0, strings.Coverage, precision: 12);

        Assert.Equal(0, result.ScopeDenominators[
            SoloFamilyRankingScopes.ProVocals]);
        Assert.DoesNotContain(
            result.Rankings,
            row => row.ScopeId == SoloFamilyRankingScopes.ProVocals);

        var drums = Assert.Single(
            result.Rankings,
            row => row.ScopeId == SoloFamilyRankingScopes.ProDrums);
        Assert.Equal(1, drums.TotalChartedSongs);
        Assert.Equal(1.0, drums.FcRate, precision: 12);
        Assert.Equal(0, result.InvalidRowCount);
    }

    [Fact]
    public void ImpossibleProducedRowsFailClosedBeforePersistence()
    {
        var perInstrument = EmptyInstrumentRows();
        perInstrument["Solo_Guitar"]["invalid"] =
            Metrics(2, 2, 1);
        var catalog = EmptyCatalogDenominators();
        catalog["Solo_Guitar"] = 1;

        var result = Build(perInstrument, catalog);

        Assert.Equal(1, result.InvalidRowCount);
        Assert.NotNull(result.FirstInvalidRow);
        Assert.Contains(
            "songs_played_exceeds_denominator",
            result.FirstInvalidRow.Reason,
            StringComparison.Ordinal);
        Assert.Contains(
            "full_combo_count_exceeds_denominator",
            result.FirstInvalidRow.Reason,
            StringComparison.Ordinal);
        Assert.Contains(
            "coverage_exceeds_one",
            result.FirstInvalidRow.Reason,
            StringComparison.Ordinal);
        Assert.Contains(
            "fc_rate_exceeds_one",
            result.FirstInvalidRow.Reason,
            StringComparison.Ordinal);

        var exception = Assert.Throws<InvalidOperationException>(
            result.ThrowIfInvalid);
        Assert.Contains(
            "publication-incompatible",
            exception.Message,
            StringComparison.Ordinal);
    }

    private static SoloFamilyRankingBuildResult Build(
        Dictionary<
            string,
            Dictionary<string, RankingsCalculator.AccountMetrics>>
            perInstrument,
        IReadOnlyDictionary<string, int> catalog)
        => SoloFamilyRankingBuilder.BuildRankings(
            SoloFamilyRankingScopes.All,
            perInstrument,
            catalog,
            RankingsCalculator.CredibilityThreshold,
            RankingsCalculator.PopulationMedian);

    private static Dictionary<
        string,
        Dictionary<string, RankingsCalculator.AccountMetrics>>
        EmptyInstrumentRows()
        => GlobalLeaderboardScraper.AllInstruments.ToDictionary(
            instrument => instrument,
            _ => new Dictionary<
                string,
                RankingsCalculator.AccountMetrics>(
                StringComparer.OrdinalIgnoreCase),
            StringComparer.OrdinalIgnoreCase);

    private static Dictionary<string, int> EmptyCatalogDenominators()
        => GlobalLeaderboardScraper.AllInstruments.ToDictionary(
            instrument => instrument,
            _ => 0,
            StringComparer.OrdinalIgnoreCase);

    private static RankingsCalculator.AccountMetrics Metrics(
        int songsPlayed,
        int fullComboCount,
        int totalChartedSongs,
        long totalScore = 100)
        => new(
            AdjustedRating: 0.2,
            WeightedRating: 0.3,
            FcRate: totalChartedSongs == 0
                ? 0
                : (double)fullComboCount / totalChartedSongs,
            TotalScore: totalScore,
            MaxScorePercent: 0.9,
            SongsPlayed: songsPlayed,
            FullComboCount: fullComboCount,
            TotalChartedSongs: totalChartedSongs,
            RawSkillRating: 0.2,
            RawWeightedRating: 0.3,
            RawMaxScorePercent: 0.9);
}
