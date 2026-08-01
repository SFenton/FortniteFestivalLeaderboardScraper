using System.Reflection;
using System.Text.Json;
using FortniteFestival.Core;
using FortniteFestival.Core.Persistence;
using FortniteFestival.Core.Services;
using FSTService.Persistence;
using FSTService.Tests.Helpers;
using Npgsql;

namespace FSTService.Tests.Unit;

public sealed class FestivalPersistenceTests : IDisposable
{
    private readonly NpgsqlDataSource _dataSource =
        SharedPostgresContainer.CreateDatabase();

    public void Dispose() => _dataSource.Dispose();

    [Fact]
    public async Task SaveSongsAsync_captures_canonical_provider_catalog()
    {
        var persistence = new FestivalPersistence(_dataSource);
        var first = CreateSong("song-a", "Alpha");
        var second = CreateSong("song-z", "Zulu");
        first.imagePath = "/local/alpha.jpg";
        first.isSelected = true;
        first.isInLocalData = "yes";

        var expected = SongCatalogSnapshotBuilder.Create([first, second]);
        var reversed = SongCatalogSnapshotBuilder.Create([second, first]);
        Assert.Equal(expected.CatalogJson, reversed.CatalogJson);
        Assert.Equal(expected.ContentHash, reversed.ContentHash);

        await persistence.SaveSongsAsync([second, first]);

        long catalogVersion;
        int schemaVersion;
        string catalogJson;
        string contentHash;
        int songCount;
        string sourceKind;
        bool isExact;
        DateTime capturedAt;
        await using (var conn = await _dataSource.OpenConnectionAsync())
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT catalog_version, schema_version, catalog_json::text,
                       content_hash, song_count, source_kind, is_exact,
                       captured_at
                FROM live_song_catalog
                WHERE id = TRUE
                """;
            await using var reader = await cmd.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            catalogVersion = reader.GetInt64(0);
            schemaVersion = reader.GetInt32(1);
            catalogJson = reader.GetString(2);
            contentHash = reader.GetString(3);
            songCount = reader.GetInt32(4);
            sourceKind = reader.GetString(5);
            isExact = reader.GetBoolean(6);
            capturedAt = reader.GetDateTime(7);
        }

        Assert.True(catalogVersion > 0);
        Assert.Equal(SongCatalogSnapshotBuilder.SchemaVersion, schemaVersion);
        Assert.Equal(expected.ContentHash, contentHash);
        Assert.Equal(2, songCount);
        Assert.Equal("provider_exact", sourceKind);
        Assert.True(isExact);

        using (var document = JsonDocument.Parse(catalogJson))
        {
            var root = document.RootElement;
            Assert.Equal(
                SongCatalogSnapshotBuilder.SchemaVersion,
                root.GetProperty("schemaVersion").GetInt32());
            var songs = root.GetProperty("songs");
            Assert.Equal(2, songs.GetArrayLength());

            var alpha = songs[0];
            Assert.Equal("song-a", alpha.GetProperty("track").GetProperty("su").GetString());
            Assert.Equal("Alpha provider title", alpha.GetProperty("_title").GetString());
            Assert.Equal("en-US", alpha.GetProperty("_locale").GetString());
            Assert.Equal("template-song-a", alpha.GetProperty("_templateName").GetString());
            Assert.Equal(
                "2026-07-30T12:00:00.0000000Z",
                alpha.GetProperty("_activeDate").GetString());
            Assert.Equal(
                "2026-07-31T13:14:15.0000000Z",
                alpha.GetProperty("lastModified").GetString());
            Assert.False(alpha.TryGetProperty("imagePath", out _));
            Assert.False(alpha.TryGetProperty("isSelected", out _));
            Assert.False(alpha.TryGetProperty("isInLocalData", out _));

            var track = alpha.GetProperty("track");
            Assert.Equal("Album", track.GetProperty("ab").GetString());
            Assert.Equal("https://example.test/song-a.dat", track.GetProperty("mu").GetString());
            Assert.Equal("https://example.test/song-a.jpg", track.GetProperty("au").GetString());
            Assert.Equal(
                ["rock", "electronic"],
                track.GetProperty("ge")
                    .EnumerateArray()
                    .Select(static value => value.GetString()!)
                    .ToArray());
            Assert.Equal(8, track.GetProperty("in").GetProperty("bd").GetInt32());
        }

        await persistence.SaveSongsAsync([first, second]);

        await using var verifyConn = await _dataSource.OpenConnectionAsync();
        await using var verify = verifyConn.CreateCommand();
        verify.CommandText = """
            SELECT catalog_version, content_hash, captured_at
            FROM live_song_catalog
            WHERE id = TRUE
            """;
        await using var verifyReader = await verify.ExecuteReaderAsync();
        Assert.True(await verifyReader.ReadAsync());
        Assert.Equal(catalogVersion, verifyReader.GetInt64(0));
        Assert.Equal(contentHash, verifyReader.GetString(1));
        Assert.Equal(capturedAt, verifyReader.GetDateTime(2));
    }

    [Fact]
    public async Task Provider_fixture_roundtrips_across_restart_without_field_loss()
    {
        var providerJson = LoadProviderFixtureSongJson();
        var song =
            SongCatalogSnapshotBuilder.DeserializeProviderSong(providerJson);
        song.imagePath = "/local/provider-fixture.jpg";
        song.isSelected = true;
        song.isInLocalData = "yes";
        var expected = SongCatalogSnapshotBuilder.Create([song]);

        var persistence = new FestivalPersistence(_dataSource);
        var token = await persistence.SaveSongsVersionedAsync([song]);

        var restartedPersistence = new FestivalPersistence(_dataSource);
        var restartedSongs = await restartedPersistence.LoadSongsAsync();
        var restarted = Assert.Single(restartedSongs);
        var actual = SongCatalogSnapshotBuilder.Create(restartedSongs);

        Assert.Equal(expected.CatalogJson, actual.CatalogJson);
        Assert.Equal(expected.ContentHash, actual.ContentHash);
        Assert.Equal(expected.ContentHash, token.ContentHash);
        Assert.Equal(expected.SongCount, token.SongCount);
        Assert.Equal("/local/provider-fixture.jpg", restarted.imagePath);

        using var document = JsonDocument.Parse(actual.CatalogJson);
        var persistedSong = document.RootElement
            .GetProperty("songs")[0];
        Assert.False(persistedSong.TryGetProperty("imagePath", out _));
        Assert.False(persistedSong.TryGetProperty("isSelected", out _));
        Assert.False(persistedSong.TryGetProperty("isInLocalData", out _));
        Assert.True(persistedSong.TryGetProperty(
            "futureTopLevel",
            out var futureTopLevel));
        Assert.Equal(3, futureTopLevel.GetProperty("revision").GetInt32());

        var track = persistedSong.GetProperty("track");
        Assert.Equal("ag-value", track.GetProperty("ag").GetString());
        Assert.Equal("ci-value", track.GetProperty("ci").GetString());
        Assert.Equal(
            "US-AAA-26-00001",
            track.GetProperty("isrc").GetString());
        Assert.Equal(
            12.5,
            track.GetProperty("mmo")
                .GetProperty("previewStart")
                .GetDouble());
        Assert.True(track.GetProperty("nu").GetBoolean());
        Assert.Equal(2, track.GetProperty("sm").GetArrayLength());
        Assert.Equal(42, track.GetProperty("tb").GetInt32());
        Assert.Equal(
            3,
            track.GetProperty("in")
                .GetProperty("futureIntensity")
                .GetProperty("bands")
                .GetArrayLength());
    }

    [Fact]
    public void Provider_sync_replaces_all_provider_fields_but_keeps_local_state()
    {
        var existing = CreateSong("fixture-song", "Old title");
        existing.imagePath = "/local/existing.jpg";
        existing.isSelected = true;
        existing.isInLocalData = "yes";
        var incoming = SongCatalogSnapshotBuilder.DeserializeProviderSong(
            LoadProviderFixtureSongJson());

        existing.ReplaceProviderDataFrom(incoming);

        Assert.Equal(
            SongCatalogSnapshotBuilder.Create([incoming]),
            SongCatalogSnapshotBuilder.Create([existing]));
        Assert.Equal("/local/existing.jpg", existing.imagePath);
        Assert.True(existing.isSelected);
        Assert.Equal("yes", existing.isInLocalData);
        Assert.Equal("Provider Fixture", existing._title);
        Assert.Equal("en-US", existing._locale);
        Assert.Equal(
            "AthenaMusicPackItemDefinition:fixture",
            existing._templateName);
        Assert.True(existing.track.providerFields.ContainsKey("ag"));
        Assert.True(existing.providerFields.ContainsKey("futureTopLevel"));
    }

    [Fact]
    public async Task Restart_load_uses_exact_catalog_not_stale_legacy_rows()
    {
        var persistence = new FestivalPersistence(_dataSource);
        var retained = CreateSong("retained-song", "Retained");
        var removed = CreateSong("removed-song", "Removed");
        await persistence.SaveSongsVersionedAsync([retained, removed]);
        await persistence.SaveSongsVersionedAsync([retained]);

        var restarted = await persistence.LoadSongsAsync();

        var song = Assert.Single(restarted);
        Assert.Equal("retained-song", song.track.su);
        await using var conn = await _dataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT COUNT(*)
            FROM songs
            WHERE song_id = 'removed-song'
            """;
        Assert.Equal(1L, (long)(await cmd.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task Explicit_catalog_persistence_failure_is_not_swallowed()
    {
        var service = new FestivalService(
            new ThrowingVersionedPersistence(),
            CreateProviderClient(
                System.Net.HttpStatusCode.OK,
                LoadProviderFixtureJson()));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            service.SyncSongsWithResultAsync);

        Assert.Equal("injected catalog persistence failure", exception.Message);
    }

    [Fact]
    public async Task Exact_provider_sync_returns_persisted_capture_token()
    {
        var service = new FestivalService(
            new FestivalPersistence(_dataSource),
            CreateProviderClient(
                System.Net.HttpStatusCode.OK,
                LoadProviderFixtureJson()));

        var result = await service.SyncSongsWithResultAsync();

        Assert.True(result.ProviderRequestSucceeded);
        Assert.True(result.IsExact);
        Assert.False(result.SafetyMergeApplied);
        Assert.Equal(1, result.ProviderSongCount);
        Assert.Equal(1, result.CatalogSongCount);
        Assert.Equal(0, result.DroppedProviderObjectCount);
        Assert.Null(result.FailureReason);
        Assert.NotNull(result.PersistenceToken);
        Assert.Equal(
            ReadLiveCatalogHash(),
            result.PersistenceToken.ContentHash);
    }

    [Fact]
    public async Task Safety_merged_provider_sync_does_not_persist_exact_catalog()
    {
        var persistence = new FestivalPersistence(_dataSource);
        var loadedSongs = Enumerable.Range(1, 20)
            .Select(index =>
                CreateSong($"song-{index:D2}", $"Song {index:D2}"))
            .ToArray();
        var originalToken =
            await persistence.SaveSongsVersionedAsync(loadedSongs);
        var partialPayload = JsonSerializer.Serialize(
            new Dictionary<string, object>
            {
                ["song-01"] = new
                {
                    _title = "Song 01 updated",
                    track = new
                    {
                        su = "song-01",
                        tt = "Song 01 updated",
                        an = "Artist",
                    },
                },
            });
        var service = new FestivalService(
            persistence,
            CreateProviderClient(
                System.Net.HttpStatusCode.OK,
                partialPayload));
        SetSongs(service, loadedSongs, trustedBaseline: true);

        var result = await service.SyncSongsWithResultAsync();

        Assert.True(result.ProviderRequestSucceeded);
        Assert.False(result.IsExact);
        Assert.True(result.SafetyMergeApplied);
        Assert.Null(result.PersistenceToken);
        Assert.Contains("Blocked eviction", result.FailureReason);
        Assert.Equal(originalToken.ContentHash, ReadLiveCatalogHash());
    }

    [Fact]
    public async Task Failed_initialization_uses_local_save_without_exact_persistence()
    {
        var persistence = new TrackingInitializationPersistence(
        [
            new Song
            {
                _title = "Legacy Song",
                track = new Track
                {
                    su = "legacy-song",
                    tt = "Legacy Song",
                    an = "Legacy Artist",
                },
            },
        ]);
        var service = new FestivalService(
            persistence,
            CreateProviderClient(
                System.Net.HttpStatusCode.ServiceUnavailable,
                """{"errorCode":"errors.com.epicgames.service_unavailable"}"""));

        await service.InitializeAsync();

        Assert.Equal(0, persistence.VersionedSaveCount);
        Assert.Equal(0, persistence.ProviderSaveCount);
        Assert.Equal(1, persistence.LocalSaveCount);
        var localState = Assert.Single(persistence.LastLocalStates);
        Assert.Equal("legacy-song", localState.SongId);
    }

    [Fact]
    public async Task Failed_initialization_keeps_legacy_bootstrap_inexact()
    {
        await using (var conn = await _dataSource.OpenConnectionAsync())
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                DELETE FROM live_song_catalog
                WHERE id = TRUE;
                INSERT INTO songs (
                    song_id, title, artist, active_date, last_modified,
                    image_path, provider_json)
                VALUES (
                    'legacy-song', 'Legacy Song', 'Legacy Artist',
                    '2026-07-30T00:00:00Z',
                    '2026-07-31T00:00:00Z',
                    '/local/legacy.jpg',
                    NULL)
                ON CONFLICT (song_id) DO UPDATE SET
                    title = EXCLUDED.title,
                    artist = EXCLUDED.artist,
                    image_path = EXCLUDED.image_path,
                    provider_json = NULL;
                """;
            await cmd.ExecuteNonQueryAsync();
        }
        await DatabaseInitializer.EnsureSchemaAsync(_dataSource);
        var before = ReadLiveCatalogState();
        var service = new FestivalService(
            new FestivalPersistence(_dataSource),
            CreateProviderClient(
                System.Net.HttpStatusCode.ServiceUnavailable,
                """{"errorCode":"errors.com.epicgames.service_unavailable"}"""));

        await service.InitializeAsync();

        var after = ReadLiveCatalogState();
        Assert.Equal(before, after);
        Assert.False(after.IsExact);
        Assert.Equal(
            "legacy_columns_reconstructed",
            after.SourceKind);
    }

    [Fact]
    public async Task Local_image_save_does_not_change_exact_catalog_token()
    {
        var persistence = new FestivalPersistence(_dataSource);
        var song = CreateSong("song-a", "Alpha");
        var token = await persistence.SaveSongsVersionedAsync([song]);

        await persistence.SaveSongLocalStateAsync(
        [
            new SongLocalState("song-a", "/local/alpha.jpg"),
        ]);

        Assert.Equal(token.ContentHash, ReadLiveCatalogHash());
        await using var conn = await _dataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT catalog_version, image_path
            FROM live_song_catalog
            CROSS JOIN songs
            WHERE live_song_catalog.id = TRUE
              AND songs.song_id = 'song-a'
            """;
        await using var reader = await cmd.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(token.CatalogVersion, reader.GetInt64(0));
        Assert.Equal("/local/alpha.jpg", reader.GetString(1));
    }

    [Fact]
    public async Task Concurrent_exact_and_inexact_syncs_are_fully_serialized()
    {
        var exactPayload = CreateProviderPayload(20, "Exact");
        var partialPayload = CreateProviderPayload(1, "Partial");
        var handler = new CoordinatedProviderHandler(
            exactPayload,
            partialPayload);
        var service = new FestivalService(
            new FestivalPersistence(_dataSource),
            new HttpClient(handler)
            {
                BaseAddress = new Uri(
                    "https://fortnitecontent-website-prod07.ol.epicgames.com"),
            });

        var exactTask = service.SyncSongsWithResultAsync();
        await handler.FirstRequestStarted.Task.WaitAsync(
            TimeSpan.FromSeconds(5));
        var inexactTask = service.SyncSongsWithResultAsync();
        await Task.Delay(100);
        Assert.Equal(1, handler.RequestCount);
        Assert.False(handler.SecondRequestStarted.Task.IsCompleted);

        handler.ReleaseFirstResponse.TrySetResult(true);
        var exact = await exactTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(exact.IsExact);
        Assert.NotNull(exact.PersistenceToken);

        await handler.SecondRequestStarted.Task.WaitAsync(
            TimeSpan.FromSeconds(5));
        var inexact = await inexactTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(inexact.IsExact);
        Assert.True(inexact.SafetyMergeApplied);
        Assert.Null(inexact.PersistenceToken);
        Assert.Equal(
            exact.PersistenceToken.ContentHash,
            ReadLiveCatalogHash());
    }

    [Fact]
    public async Task Exact_provider_response_replaces_large_untrusted_legacy_baseline()
    {
        await using (var conn = await _dataSource.OpenConnectionAsync())
        await using (var tx = await conn.BeginTransactionAsync())
        {
            await using (var clear = conn.CreateCommand())
            {
                clear.Transaction = tx;
                clear.CommandText = """
                    DELETE FROM live_song_catalog
                    WHERE id = TRUE;
                    """;
                await clear.ExecuteNonQueryAsync();
            }

            for (var index = 1; index <= 20; index++)
            {
                await using var insert = conn.CreateCommand();
                insert.Transaction = tx;
                insert.CommandText = """
                    INSERT INTO songs (
                        song_id, title, artist, active_date,
                        last_modified, provider_json)
                    VALUES (
                        @songId, @title, 'Legacy Artist',
                        '2026-07-30T00:00:00Z',
                        '2026-07-31T00:00:00Z',
                        NULL)
                    ON CONFLICT (song_id) DO UPDATE SET
                        title = EXCLUDED.title,
                        artist = EXCLUDED.artist,
                        provider_json = NULL
                    """;
                insert.Parameters.AddWithValue(
                    "songId",
                    $"legacy-{index:D2}");
                insert.Parameters.AddWithValue(
                    "title",
                    $"Legacy {index:D2}");
                await insert.ExecuteNonQueryAsync();
            }
            await tx.CommitAsync();
        }
        await DatabaseInitializer.EnsureSchemaAsync(_dataSource);
        Assert.False(ReadLiveCatalogState().IsExact);

        var replacementPayload = JsonSerializer.Serialize(
            new Dictionary<string, object>
            {
                ["provider-new"] = new
                {
                    _title = "Provider New",
                    track = new
                    {
                        su = "provider-new",
                        tt = "Provider New",
                        an = "Provider Artist",
                    },
                },
            });
        var service = new FestivalService(
            new FestivalPersistence(_dataSource),
            CreateProviderClient(
                System.Net.HttpStatusCode.OK,
                replacementPayload));

        await service.InitializeAsync();

        var onlySong = Assert.Single(service.Songs);
        Assert.Equal("provider-new", onlySong.track.su);
        var live = ReadLiveCatalogState();
        Assert.True(live.IsExact);
        Assert.Equal("provider_exact", live.SourceKind);
        await using var verifyConn =
            await _dataSource.OpenConnectionAsync();
        await using var verify = verifyConn.CreateCommand();
        verify.CommandText = """
            SELECT song_count
            FROM live_song_catalog
            WHERE id = TRUE
            """;
        Assert.Equal(1, (int)(await verify.ExecuteScalarAsync())!);
    }

    private static Song CreateSong(string songId, string title) =>
        new()
        {
            _title = $"{title} provider title",
            _noIndex = false,
            _activeDate = new DateTime(
                2026, 7, 30, 12, 0, 0, DateTimeKind.Utc),
            lastModified = new DateTime(
                2026, 7, 31, 13, 14, 15, DateTimeKind.Utc),
            _locale = "en-US",
            _templateName = $"template-{songId}",
            track = new Track
            {
                tt = title,
                ry = 2026,
                dn = 245,
                sib = "sib",
                sid = "sid",
                sig = "4/4",
                qi = "qi",
                sn = "sn",
                ge = ["rock", "electronic"],
                mk = "mk",
                mm = "mm",
                ab = "Album",
                siv = "siv",
                su = songId,
                @in = new In
                {
                    pb = 1,
                    pd = 2,
                    vl = 3,
                    pg = 4,
                    _type = "SparkTrackIntensities",
                    gr = 5,
                    ds = 6,
                    ba = 7,
                    bd = 8,
                },
                mt = 120,
                _type = "SparkTrack",
                mu = $"https://example.test/{songId}.dat",
                an = "Artist",
                gt = ["tag-z", "tag-a"],
                ar = "Artist",
                au = $"https://example.test/{songId}.jpg",
                ti = "ti",
                ld = "ld",
                jc = "jc",
            },
        };

    private static string LoadProviderFixtureSongJson()
    {
        using var document =
            JsonDocument.Parse(LoadProviderFixtureJson());
        return document.RootElement
            .GetProperty("fixture-song")
            .GetRawText();
    }

    private static string LoadProviderFixtureJson()
    {
        var path = Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures",
            "epic-song-provider.json");
        return File.ReadAllText(path);
    }

    private static HttpClient CreateProviderClient(
        System.Net.HttpStatusCode statusCode,
        string content) =>
        new(new StaticProviderHandler(statusCode, content))
        {
            BaseAddress = new Uri(
                "https://fortnitecontent-website-prod07.ol.epicgames.com"),
        };

    private static string CreateProviderPayload(
        int count,
        string titlePrefix)
    {
        var payload = Enumerable.Range(1, count)
            .ToDictionary(
                index => $"song-{index:D2}",
                index => (object)new
                {
                    _title = $"{titlePrefix} {index:D2}",
                    track = new
                    {
                        su = $"song-{index:D2}",
                        tt = $"{titlePrefix} {index:D2}",
                        an = "Artist",
                    },
                });
        return JsonSerializer.Serialize(payload);
    }

    private static void SetSongs(
        FestivalService service,
        IEnumerable<Song> songs,
        bool trustedBaseline = false)
    {
        var flags = BindingFlags.NonPublic | BindingFlags.Instance;
        var songsField =
            typeof(FestivalService).GetField("_songs", flags)!;
        var dirtyField =
            typeof(FestivalService).GetField("_songsDirty", flags)!;
        var dictionary =
            (Dictionary<string, Song>)songsField.GetValue(service)!;
        foreach (var song in songs)
            dictionary[song.track.su] = song;
        dirtyField.SetValue(service, true);
        var trustedField = typeof(FestivalService).GetField(
            "_songCatalogBaselineTrusted",
            flags)!;
        trustedField.SetValue(service, trustedBaseline);
    }

    private string ReadLiveCatalogHash()
    {
        using var conn = _dataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT content_hash
            FROM live_song_catalog
            WHERE id = TRUE
            """;
        return (string)cmd.ExecuteScalar()!;
    }

    private (
        long CatalogVersion,
        string ContentHash,
        string SourceKind,
        bool IsExact) ReadLiveCatalogState()
    {
        using var conn = _dataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT catalog_version, content_hash, source_kind, is_exact
            FROM live_song_catalog
            WHERE id = TRUE
            """;
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());
        return (
            reader.GetInt64(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetBoolean(3));
    }

    private sealed class StaticProviderHandler : HttpMessageHandler
    {
        private readonly System.Net.HttpStatusCode _statusCode;
        private readonly string _content;

        public StaticProviderHandler(
            System.Net.HttpStatusCode statusCode,
            string content)
        {
            _statusCode = statusCode;
            _content = content;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(_statusCode)
            {
                Content = new StringContent(
                    _content,
                    System.Text.Encoding.UTF8,
                    "application/json"),
            });
    }

    private sealed class CoordinatedProviderHandler : HttpMessageHandler
    {
        private readonly string _firstContent;
        private readonly string _secondContent;
        private int _requestCount;

        public CoordinatedProviderHandler(
            string firstContent,
            string secondContent)
        {
            _firstContent = firstContent;
            _secondContent = secondContent;
        }

        public int RequestCount => Volatile.Read(ref _requestCount);
        public TaskCompletionSource<bool> FirstRequestStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> SecondRequestStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> ReleaseFirstResponse { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var requestNumber = Interlocked.Increment(
                ref _requestCount);
            string content;
            if (requestNumber == 1)
            {
                FirstRequestStarted.TrySetResult(true);
                await ReleaseFirstResponse.Task.WaitAsync(
                    cancellationToken);
                content = _firstContent;
            }
            else
            {
                SecondRequestStarted.TrySetResult(true);
                content = _secondContent;
            }

            return new HttpResponseMessage(
                System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(
                    content,
                    System.Text.Encoding.UTF8,
                    "application/json"),
            };
        }
    }

    private sealed class TrackingInitializationPersistence :
        IFestivalPersistence,
        IVersionedSongCatalogPersistence,
        ILocalSongStatePersistence
    {
        private readonly IList<Song> _songs;

        public TrackingInitializationPersistence(IList<Song> songs)
        {
            _songs = songs;
        }

        public int VersionedSaveCount { get; private set; }
        public int ProviderSaveCount { get; private set; }
        public int LocalSaveCount { get; private set; }
        public IReadOnlyList<SongLocalState> LastLocalStates { get; private set; } =
            Array.Empty<SongLocalState>();

        public Task<IList<LeaderboardData>> LoadScoresAsync() =>
            Task.FromResult<IList<LeaderboardData>>([]);

        public Task SaveScoresAsync(IEnumerable<LeaderboardData> scores) =>
            Task.CompletedTask;

        public Task<IList<Song>> LoadSongsAsync() =>
            Task.FromResult(_songs);

        public Task SaveSongsAsync(IEnumerable<Song> songs)
        {
            ProviderSaveCount++;
            return Task.CompletedTask;
        }

        public Task<SongCatalogPersistenceToken> SaveSongsVersionedAsync(
            IEnumerable<Song> songs)
        {
            VersionedSaveCount++;
            return Task.FromResult(
                new SongCatalogPersistenceToken(
                    1,
                    SongCatalogSnapshotBuilder.SchemaVersion,
                    new string('a', 64),
                    songs.Count()));
        }

        public Task SaveSongLocalStateAsync(
            IEnumerable<SongLocalState> states)
        {
            LocalSaveCount++;
            LastLocalStates = states.ToArray();
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingVersionedPersistence :
        IFestivalPersistence,
        IVersionedSongCatalogPersistence
    {
        public Task<IList<LeaderboardData>> LoadScoresAsync() =>
            Task.FromResult<IList<LeaderboardData>>([]);

        public Task SaveScoresAsync(IEnumerable<LeaderboardData> scores) =>
            Task.CompletedTask;

        public Task<IList<Song>> LoadSongsAsync() =>
            Task.FromResult<IList<Song>>([]);

        public Task SaveSongsAsync(IEnumerable<Song> songs) =>
            Task.CompletedTask;

        public Task<SongCatalogPersistenceToken> SaveSongsVersionedAsync(
            IEnumerable<Song> songs) =>
            Task.FromException<SongCatalogPersistenceToken>(
                new InvalidOperationException(
                    "injected catalog persistence failure"));
    }
}
