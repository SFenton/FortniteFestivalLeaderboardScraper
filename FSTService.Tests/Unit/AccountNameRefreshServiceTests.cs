using System.Net;
using FSTService.Auth;
using FSTService.Scraping;
using FSTService.Tests.Helpers;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace FSTService.Tests.Unit;

public class AccountNameRefreshServiceTests
{
    private static (AccountNameRefreshService Service, MockHttpMessageHandler Handler, InMemoryMetaDatabase MetaDb) CreateService()
    {
        var handler = new MockHttpMessageHandler();
        var httpClient = new HttpClient(handler);
        var metaDb = new InMemoryMetaDatabase();

        var authHandler = new MockHttpMessageHandler();
        for (var i = 0; i < 100; i++)
        {
            authHandler.EnqueueJsonOk("""
            {
                "access_token": "test_token",
                "expires_in": 7200,
                "expires_at": "2099-12-31T23:59:59.000Z",
                "token_type": "bearer",
                "refresh_token": "rt",
                "account_id": "caller"
            }
            """);
        }

        var authService = new EpicAuthService(new HttpClient(authHandler), Substitute.For<ILogger<EpicAuthService>>());
        var store = Substitute.For<ICredentialStore>();
        store.LoadAsync(Arg.Any<CancellationToken>())
            .Returns(new StoredCredentials { AccountId = "caller", RefreshToken = "rt" });
        var tokenManager = new TokenManager(authService, store, Substitute.For<ILogger<TokenManager>>());
        var resolver = new AccountNameResolver(
            httpClient,
            metaDb.Db,
            tokenManager,
            new ScrapeProgressTracker(),
            Substitute.For<ILogger<AccountNameResolver>>());

        var service = new AccountNameRefreshService(
            resolver,
            metaDb.Db,
            Substitute.For<ILogger<AccountNameRefreshService>>());

        return (service, handler, metaDb);
    }

    [Fact]
    public async Task RefreshAsync_ChangedName_UpdatesDbAndReturnsChangedName()
    {
        var (service, handler, metaDb) = CreateService();
        metaDb.Db.InsertAccountNames([("acct1", "OldName")]);
        handler.EnqueueJsonOk("""[{"id":"acct1","displayName":"NewName"}]""");

        var result = await service.RefreshAsync(["acct1"]);

        Assert.Equal(1, result.Changed);
        Assert.Equal(0, result.Unchanged);
        Assert.Equal("NewName", result.Names["acct1"]);
        Assert.Equal("NewName", metaDb.Db.GetDisplayName("acct1"));
    }

    [Fact]
    public async Task RefreshAsync_UnchangedName_DoesNotRewriteLastResolved()
    {
        var (service, handler, metaDb) = CreateService();
        metaDb.Db.InsertAccountNames([("acct1", "SameName")]);
        SetLastResolved(metaDb, "acct1", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var before = GetLastResolved(metaDb, "acct1");
        handler.EnqueueJsonOk("""[{"id":"acct1","displayName":"SameName"}]""");

        var result = await service.RefreshAsync(["acct1"]);

        Assert.Equal(0, result.Changed);
        Assert.Equal(1, result.Unchanged);
        Assert.Empty(result.Names);
        Assert.Equal(before, GetLastResolved(metaDb, "acct1"));
    }

    [Fact]
    public async Task RefreshAsync_MissingEpicResult_KeepsExistingName()
    {
        var (service, handler, metaDb) = CreateService();
        metaDb.Db.InsertAccountNames([("acct1", "KnownName")]);
        handler.EnqueueJsonOk("[]");

        var result = await service.RefreshAsync(["acct1"]);

        Assert.Equal(0, result.Changed);
        Assert.Equal(0, result.Unchanged);
        Assert.Equal(1, result.Missing);
        Assert.Equal("KnownName", metaDb.Db.GetDisplayName("acct1"));
    }

    [Fact]
    public async Task RefreshAsync_FailedBatch_ReturnsFailedAndKeepsExistingName()
    {
        var (service, handler, metaDb) = CreateService();
        metaDb.Db.InsertAccountNames([("acct1", "KnownName")]);
        handler.EnqueueError(HttpStatusCode.InternalServerError, "error");
        handler.EnqueueError(HttpStatusCode.InternalServerError, "error");
        handler.EnqueueError(HttpStatusCode.InternalServerError, "error");
        handler.EnqueueError(HttpStatusCode.InternalServerError, "error");

        var result = await service.RefreshAsync(["acct1"]);

        Assert.Equal(0, result.Changed);
        Assert.Equal(1, result.Failed);
        Assert.Equal("KnownName", metaDb.Db.GetDisplayName("acct1"));
    }

    [Fact]
    public async Task RefreshAsync_DedupesAccountIdsBeforeLookup()
    {
        var (service, handler, _) = CreateService();
        handler.EnqueueJsonOk("""
        [
            { "id": "acct1", "displayName": "One" },
            { "id": "acct2", "displayName": "Two" }
        ]
        """);

        var result = await service.RefreshAsync(["acct1", " acct1 ", "acct2"]);

        Assert.Equal(2, result.Changed);
        Assert.Single(handler.Requests);
        var query = handler.Requests[0].RequestUri!.Query;
        Assert.Contains("accountId=acct1", query);
        Assert.Contains("accountId=acct2", query);
        Assert.Equal(query.IndexOf("acct1", StringComparison.Ordinal), query.LastIndexOf("acct1", StringComparison.Ordinal));
    }

    private static void SetLastResolved(InMemoryMetaDatabase metaDb, string accountId, DateTime value)
    {
        using var conn = metaDb.DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE account_names SET last_resolved = @lastResolved WHERE account_id = @accountId";
        cmd.Parameters.AddWithValue("lastResolved", value);
        cmd.Parameters.AddWithValue("accountId", accountId);
        cmd.ExecuteNonQuery();
    }

    private static DateTime? GetLastResolved(InMemoryMetaDatabase metaDb, string accountId)
    {
        using var conn = metaDb.DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT last_resolved FROM account_names WHERE account_id = @accountId";
        cmd.Parameters.AddWithValue("accountId", accountId);
        var result = cmd.ExecuteScalar();
        return result is null or DBNull ? null : (DateTime)result;
    }
}