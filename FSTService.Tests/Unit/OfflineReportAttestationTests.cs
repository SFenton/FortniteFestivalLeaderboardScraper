using FstSnapshotGenerationRetentionReport;
using FSTService.Persistence.Maintenance;
using Npgsql;

namespace FSTService.Tests.Unit;

public sealed partial class SnapshotGenerationRetentionPlannerTests
{
    [Fact]
    public async Task OfflineReportRequiresTheRealInstalledSchemaAndDatabaseIdentity()
    {
        SeedOfflineBaseline(("Solo_Guitar", 1307));
        var identity = await CaptureOfflineRuntimeIdentityAsync();
        Assert.True(identity.RequiredSchemaAccepted);
        var result = await CreatePlanner().ObserveCurrentOfflineAsync(new OfflineReportAttestation(
            new OfflineFixtureCode(), OfflineReportCommandTests.Assertions(identity)));
        Assert.Equal(SnapshotGenerationRetentionPlanDisposition.Observed, result.Result.Disposition);
        var after = await CaptureOfflineRuntimeIdentityAsync();
        Assert.Equal(identity.SchemaSha256, after.SchemaSha256);
        Assert.Equal(identity.DatabaseIdentitySha256, after.DatabaseIdentitySha256);
    }

    [Theory]
    [InlineData("ALTER TABLE snapshot_generation_retention_cycles DISABLE TRIGGER trg_reject_snapshot_generation_retention_cycles_mutation")]
    [InlineData("ALTER TABLE snapshot_generation_retention_observations ADD COLUMN unexpected INTEGER")]
    [InlineData("ALTER TABLE snapshot_generation_retention_holds ENABLE ROW LEVEL SECURITY")]
    [InlineData("CREATE OR REPLACE FUNCTION fst_reject_snapshot_generation_retention_evidence_mutation() RETURNS trigger LANGUAGE plpgsql AS $body$ BEGIN RETURN NULL; END $body$")]
    public async Task OfflineReportRejectsSchemaDriftWithoutRepair(string mutation)
    {
        SeedOfflineBaseline(("Solo_Guitar", 1307));
        var identity = await CaptureOfflineRuntimeIdentityAsync();
        Execute(mutation);
        var before = await CaptureOfflineRuntimeIdentityAsync();
        var error = await Assert.ThrowsAsync<OfflineReportRefusal>(() =>
            CreatePlanner().ObserveCurrentOfflineAsync(new OfflineReportAttestation(
                new OfflineFixtureCode(), OfflineReportCommandTests.Assertions(identity))));
        Assert.Equal("required_schema_mismatch", error.Code);
        Assert.Equal(before.SchemaSha256, (await CaptureOfflineRuntimeIdentityAsync()).SchemaSha256);
        Assert.Equal(0, Scalar<long>("SELECT count(*) FROM snapshot_generation_retention_cycles"));
    }

    [Fact]
    public async Task OfflineReportNeverInitializesMissingSchema()
    {
        SeedOfflineBaseline(("Solo_Guitar", 1307));
        var identity = await CaptureOfflineRuntimeIdentityAsync();
        Execute("DROP TABLE snapshot_generation_retention_evidence");
        var error = await Assert.ThrowsAsync<OfflineReportRefusal>(() =>
            CreatePlanner().ObserveCurrentOfflineAsync(new OfflineReportAttestation(
                new OfflineFixtureCode(), OfflineReportCommandTests.Assertions(identity))));
        Assert.Equal("required_schema_mismatch", error.Code);
        Assert.True(Scalar<bool>("SELECT to_regclass('public.snapshot_generation_retention_evidence') IS NULL"));
        Assert.Equal(0, Scalar<long>("SELECT count(*) FROM snapshot_generation_retention_cycles"));
    }

    [Fact]
    public async Task OfflineReportRejectsWrongDatabaseBeforeSourceAdmission()
    {
        SeedOfflineBaseline(("Solo_Guitar", 1307));
        var identity = await CaptureOfflineRuntimeIdentityAsync();
        var assertions = OfflineReportCommandTests.Assertions(identity) with
        {
            DatabaseIdentitySha256 = new('f', 64),
        };
        var error = await Assert.ThrowsAsync<OfflineReportRefusal>(() =>
            CreatePlanner().ObserveCurrentOfflineAsync(new OfflineReportAttestation(
                new OfflineFixtureCode(), assertions)));
        Assert.Equal("database_identity_mismatch", error.Code);
        Assert.Equal(0, Scalar<long>("SELECT count(*) FROM snapshot_generation_retention_cycles"));
    }

    private async Task<OfflineReportRuntimeIdentity> CaptureOfflineRuntimeIdentityAsync()
    {
        await using var connection = await _fixture.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var result = await OfflineReportDatabase.CaptureAsync(
            connection, transaction, new OfflineFixtureCode(), CancellationToken.None);
        await transaction.CommitAsync();
        return result;
    }

    private sealed class OfflineFixtureCode : IOfflineReportCodeIdentityProvider
    {
        public Task<OfflineReportCodeIdentity> CaptureAsync(CancellationToken ct) =>
            Task.FromResult(new OfflineReportCodeIdentity(
                new('a', 40), new('b', 40), new('c', 64),
                new('d', 64), new('e', 64), false));
    }
}
