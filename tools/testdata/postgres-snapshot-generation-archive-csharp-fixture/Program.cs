using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using FSTService.Persistence.Maintenance;
using FSTService.Scraping.Replay;

const long cycleId = 44;
const long triggerScrapeId = 200;
const long triggerPublicationId = 20;
const string safePointKind = "terminal_worker_post_publication";

var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
{
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
};

byte[] Canonical<T>(T value) => TierZeroCanonicalJson.Serialize(value!);

string Hash<T>(T value) =>
    Convert.ToHexString(SHA256.HashData(Canonical(value))).ToLowerInvariant();

Dictionary<string, object?> Index(
    long tableOid,
    long indexOid,
    long relfilenode,
    string name,
    string relationKind,
    bool primary,
    bool unique,
    long? parentOid,
    string definition)
{
    var result = new Dictionary<string, object?>
    {
        ["tableOid"] = tableOid,
        ["indexOid"] = indexOid,
        ["indexRelfilenode"] = relfilenode,
        ["indexName"] = name,
        ["relationKind"] = relationKind,
        ["isValid"] = true,
        ["isReady"] = true,
        ["isPrimary"] = primary,
        ["isUnique"] = unique,
        ["accessMethod"] = "btree",
        ["tablespaceName"] = "pg_default",
        ["definition"] = definition,
    };
    if (parentOid.HasValue)
        result["parentIndexOid"] = parentOid.Value;
    return result;
}

Dictionary<string, object?> Observation(
    long observationId,
    string instrument,
    string rootRelation,
    long snapshotId,
    long rootOid,
    long childOid,
    long childRelfilenode,
    bool live,
    IReadOnlyList<string> reasons)
{
    var rootIndexes = new[]
    {
        Index(
            rootOid,
            rootOid + 1000,
            0,
            $"{rootRelation}_pkey",
            "I",
            true,
            true,
            501,
            $"CREATE UNIQUE INDEX {rootRelation}_pkey ON ONLY public.{rootRelation} USING btree (snapshot_id, song_id, instrument, account_id)"),
        Index(
            rootOid,
            rootOid + 1001,
            0,
            $"ix_{rootRelation}_score",
            "I",
            false,
            false,
            502,
            $"CREATE INDEX ix_{rootRelation}_score ON ONLY public.{rootRelation} USING btree (snapshot_id, song_id, instrument, score DESC)"),
    }.OrderBy(
        item => (string)item["indexName"]!,
        StringComparer.Ordinal).ThenBy(
        item => (long)item["indexOid"]!).ToArray();
    var childRelation = $"{rootRelation}_s{snapshotId}";
    var childIndexes = new[]
    {
        Index(
            childOid,
            childOid + 1000,
            childRelfilenode + 1000,
            $"{childRelation}_pkey",
            "i",
            true,
            true,
            rootOid + 1000,
            $"CREATE UNIQUE INDEX {childRelation}_pkey ON public.{childRelation} USING btree (snapshot_id, song_id, instrument, account_id)"),
        Index(
            childOid,
            childOid + 1001,
            childRelfilenode + 1001,
            $"ix_{childRelation}_score",
            "i",
            false,
            false,
            rootOid + 1001,
            $"CREATE INDEX ix_{childRelation}_score ON public.{childRelation} USING btree (snapshot_id, song_id, instrument, score DESC)"),
    }.OrderBy(
        item => (string)item["indexName"]!,
        StringComparer.Ordinal).ThenBy(
        item => (long)item["indexOid"]!).ToArray();
    var camel = new Dictionary<string, object?>
    {
        ["instrument"] = instrument,
        ["rootSchema"] = "public",
        ["rootRelation"] = rootRelation,
        ["snapshotParentOid"] = 500L,
        ["rootOid"] = rootOid,
        ["rootPartitionKey"] = "LIST (snapshot_id)",
        ["rootPartitionBound"] = $"FOR VALUES IN ('{instrument}')",
        ["rootTablespaceName"] = "pg_default",
        ["rootRelationOptions"] = Array.Empty<string>(),
        ["rootIndexes"] = rootIndexes,
        ["childSchema"] = "public",
        ["childRelation"] = childRelation,
        ["snapshotId"] = snapshotId,
        ["childOid"] = childOid,
        ["childRelfilenode"] = childRelfilenode,
        ["partitionBound"] = $"FOR VALUES IN ('{snapshotId}')",
        ["tablespaceName"] = "pg_default",
        ["relationKind"] = "r",
        ["persistenceKind"] = "p",
        ["accessMethod"] = "heap",
        ["relationOptions"] = Array.Empty<string>(),
        ["indexes"] = childIndexes,
    };
    var stableIdentity = camel
        .Where(pair => new[]
        {
            "instrument", "rootSchema", "rootRelation", "snapshotParentOid",
            "rootOid", "rootPartitionKey", "rootPartitionBound", "childSchema",
            "childRelation", "snapshotId", "childOid", "childRelfilenode",
            "partitionBound",
        }.Contains(pair.Key, StringComparer.Ordinal))
        .ToDictionary(pair => pair.Key, pair => pair.Value);
    var stableChildHash = Hash(stableIdentity);
    var stableConfigHash = Hash(camel);
    var rowEstimate = live ? 23L : 11L;
    var totalBytes = live ? 200L : 100L;
    var metricsHash = Hash(new
    {
        StableChildIdentityHash = stableChildHash,
        RowEstimate = rowEstimate,
        TotalBytes = totalBytes,
    });
    var physicalKey = string.Join(
        "|",
        instrument,
        "public",
        rootRelation,
        500,
        rootOid,
        "LIST (snapshot_id)",
        $"FOR VALUES IN ('{instrument}')",
        "public",
        childRelation,
        snapshotId,
        childOid,
        childRelfilenode,
        $"FOR VALUES IN ('{snapshotId}')");
    var classification = live ? "protected" : "candidate";
    return new Dictionary<string, object?>
    {
        ["observation_id"] = observationId,
        ["cycle_id"] = cycleId,
        ["report_only"] = true,
        ["instrument"] = instrument,
        ["root_schema"] = "public",
        ["root_relation"] = rootRelation,
        ["snapshot_parent_oid"] = 500L,
        ["root_oid"] = rootOid,
        ["root_partition_key"] = "LIST (snapshot_id)",
        ["root_partition_bound"] = $"FOR VALUES IN ('{instrument}')",
        ["root_tablespace_name"] = "pg_default",
        ["root_relation_options"] = Array.Empty<string>(),
        ["root_index_configuration"] = rootIndexes,
        ["child_schema"] = "public",
        ["child_relation"] = childRelation,
        ["snapshot_id"] = snapshotId,
        ["child_oid"] = childOid,
        ["child_relfilenode"] = childRelfilenode,
        ["partition_bound"] = $"FOR VALUES IN ('{snapshotId}')",
        ["tablespace_name"] = "pg_default",
        ["relation_kind"] = "r",
        ["persistence_kind"] = "p",
        ["access_method"] = "heap",
        ["relation_options"] = Array.Empty<string>(),
        ["index_configuration"] = childIndexes,
        ["stable_child_identity_hash"] = stableChildHash,
        ["stable_config_schema_hash"] = stableConfigHash,
        ["row_estimate"] = rowEstimate,
        ["total_bytes"] = totalBytes,
        ["observation_metrics_hash"] = metricsHash,
        ["planner_live"] = live,
        ["oracle_live"] = live,
        ["classification"] = classification,
        ["root_reasons"] = reasons,
        ["blocker_codes"] = Array.Empty<string>(),
        ["details"] = new Dictionary<string, object?>
        {
            ["childPhysicalKey"] = physicalKey,
            ["rootReasons"] = reasons,
            ["blockers"] = Array.Empty<object>(),
        },
    };
}

var guitar = Observation(
    701,
    "Solo_Guitar",
    "leaderboard_entries_snapshot_solo_guitar",
    100,
    600,
    700,
    800,
    false,
    Array.Empty<string>());
var bass = Observation(
    702,
    "Solo_Bass",
    "leaderboard_entries_snapshot_solo_bass",
    101,
    610,
    710,
    810,
    true,
    new[] { "active_snapshot", "named_publication_source" });
var observations = new[] { guitar, bass };

Dictionary<string, object?> Evaluation(Dictionary<string, object?> observation) =>
    new()
    {
        ["physicalKey"] =
            ((Dictionary<string, object?>)observation["details"]!)["childPhysicalKey"],
        ["stableChildIdentityHash"] = observation["stable_child_identity_hash"],
        ["stableConfigSchemaHash"] = observation["stable_config_schema_hash"],
        ["rowEstimate"] = observation["row_estimate"],
        ["totalBytes"] = observation["total_bytes"],
        ["observationMetricsHash"] = observation["observation_metrics_hash"],
        ["plannerLive"] = observation["planner_live"],
        ["oracleLive"] = observation["oracle_live"],
        ["classification"] = observation["classification"],
        ["rootReasons"] = observation["root_reasons"],
        ["blockers"] = Array.Empty<object>(),
    };

var evaluations = observations
    .Select(Evaluation)
    .OrderBy(item => (string)item["physicalKey"]!, StringComparer.Ordinal)
    .ToArray();
var children = evaluations
    .Select(item => (string)item["physicalKey"]!)
    .Order(StringComparer.Ordinal)
    .ToArray();
var live = evaluations
    .Where(item => (bool)item["plannerLive"]!)
    .Select(item => (string)item["physicalKey"]!)
    .Order(StringComparer.Ordinal)
    .ToArray();
var candidates = children.Except(live, StringComparer.Ordinal).ToArray();
var candidateIdentityHash = Hash(
    evaluations
        .Where(item => (string)item["classification"]! == "candidate")
        .Select(item => new
        {
            PhysicalKey = item["physicalKey"],
            StableChildIdentityHash = item["stableChildIdentityHash"],
            StableConfigSchemaHash = item["stableConfigSchemaHash"],
        }));
var comparison = new
{
    Agrees = true,
    PublicationSourceValidationAgrees = true,
    IndexTopologyValidationAgrees = true,
    PlannerOnlyChildren = Array.Empty<string>(),
    OracleOnlyChildren = Array.Empty<string>(),
    PlannerOnlyLive = Array.Empty<string>(),
    OracleOnlyLive = Array.Empty<string>(),
    PlannerOnlyCandidates = Array.Empty<string>(),
    OracleOnlyCandidates = Array.Empty<string>(),
};
var observationHash = Hash(new
{
    PlannerVersion = 3,
    ConfigVersion = 1,
    TriggerScrapeId = triggerScrapeId,
    TriggerPublicationId = triggerPublicationId,
    SafePointKind = safePointKind,
    Evaluations = evaluations,
    GlobalBlockers = Array.Empty<object>(),
    Anomalies = Array.Empty<object>(),
    Comparison = comparison,
});

SnapshotGenerationRetentionPublicationSourceValidation PublicationValidation(
    string slot,
    long publicationId,
    long scrapeId,
    string hash) =>
    new(
        slot,
        publicationId,
        scrapeId,
        12,
        12,
        12,
        hash,
        hash,
        0,
        0,
        true);

SnapshotGenerationRetentionNumericChildIndexValidation NumericValidation(
    string instrument,
    long snapshotId,
    string relation,
    string key) =>
    new(
        instrument,
        snapshotId,
        relation,
        new[] { key },
        1,
        0,
        0,
        0,
        0,
        0,
        0);

SnapshotGenerationRetentionIndexTopologyValidation TopologyValidation(
    string instrument,
    SnapshotGenerationRetentionNumericChildIndexValidation numeric) =>
    new(
        instrument,
        new[] { $"top|{instrument}|\"primary\"" },
        new[] { $"root|{instrument}|\"primary\"" },
        new[] { $"default|{instrument}|\"primary\"" },
        Array.Empty<string>(),
        0,
        0,
        0,
        0,
        0,
        0,
        0,
        0,
        0,
        0,
        0,
        0,
        0,
        new[] { numeric });

var publicationValidations = new[]
{
    PublicationValidation("current", 20, 200, new string('a', 64)),
    PublicationValidation("previous", 19, 199, new string('b', 64)),
};
var oraclePublicationValidations = publicationValidations.Reverse().ToArray();
var numericGuitar = NumericValidation(
    "Solo_Guitar",
    100,
    "leaderboard_entries_snapshot_solo_guitar_s100",
    "700|1700|1800|\"guitar-primary\"");
var numericBass = NumericValidation(
    "Solo_Bass",
    101,
    "leaderboard_entries_snapshot_solo_bass_s101",
    "710|1710|1810|\"bass-primary\"");
var topologyValidations = new[]
{
    TopologyValidation("Solo_Guitar", numericGuitar),
    TopologyValidation("Solo_Bass", numericBass),
};
var oracleTopologyValidations = topologyValidations.Reverse().ToArray();
var summaryPayload = new
{
    Status = "observed",
    OracleAgreement = true,
    CandidateIdentityHash = candidateIdentityHash,
    ObservationHash = observationHash,
    PlannerChildKeys = children,
    PlannerLiveKeys = live,
    PlannerCandidateKeys = candidates,
    OracleChildKeys = children,
    OracleLiveKeys = live,
    OracleCandidateKeys = candidates,
    PlannerPublicationSourceValidations = publicationValidations,
    OraclePublicationSourceValidations = oraclePublicationValidations,
    PlannerIndexTopologyValidations = topologyValidations,
    OracleIndexTopologyValidations = oracleTopologyValidations,
    GlobalBlockers = Array.Empty<object>(),
    Anomalies = Array.Empty<object>(),
};

Dictionary<string, object?> Evidence(
    long evidenceId,
    long? observationId,
    int sequence,
    string kind,
    object payload,
    string? previousHash)
{
    var hashInput = new Dictionary<string, object?>
    {
        ["cycleId"] = cycleId,
        ["sequence"] = sequence,
        ["phase"] = "observation",
        ["kind"] = kind,
        ["payload"] = payload,
    };
    if (observationId.HasValue)
        hashInput["observationId"] = observationId.Value;
    if (previousHash is not null)
        hashInput["previousHash"] = previousHash;
    var currentHash = Hash(hashInput);
    return new Dictionary<string, object?>
    {
        ["evidence_id"] = evidenceId,
        ["cycle_id"] = cycleId,
        ["observation_id"] = observationId,
        ["sequence"] = sequence,
        ["phase"] = "observation",
        ["kind"] = kind,
        ["payload"] = payload,
        ["previous_hash"] = previousHash,
        ["current_hash"] = currentHash,
    };
}

var summary = Evidence(1, null, 1, "summary", summaryPayload, null);
var guitarEvaluation = Evaluation(guitar);
var childPayloadKeys = new[]
{
    "physicalKey",
    "stableChildIdentityHash",
    "stableConfigSchemaHash",
    "observationMetricsHash",
    "plannerLive",
    "oracleLive",
    "classification",
    "rootReasons",
    "blockers",
};
var guitarPayload = childPayloadKeys.ToDictionary(
    key => key,
    key => guitarEvaluation[key]);
var childGuitar = Evidence(
    2,
    701,
    2,
    "child",
    guitarPayload,
    (string)summary["current_hash"]!);
var bassEvaluation = Evaluation(bass);
var bassPayload = childPayloadKeys.ToDictionary(
    key => key,
    key => bassEvaluation[key]);
var childBass = Evidence(
    3,
    702,
    3,
    "child",
    bassPayload,
    (string)childGuitar["current_hash"]!);

var cycle = new Dictionary<string, object?>
{
    ["cycle_id"] = cycleId,
    ["trigger_scrape_id"] = triggerScrapeId,
    ["trigger_publication_id"] = triggerPublicationId,
    ["safe_point_kind"] = safePointKind,
    ["safe_point_at"] = "2026-08-29T20:00:00Z",
    ["planner_version"] = 3,
    ["config_version"] = 1,
    ["report_only"] = true,
    ["status"] = "observed",
    ["oracle_agreement"] = true,
    ["candidate_identity_hash"] = candidateIdentityHash,
    ["observation_hash"] = observationHash,
    ["planner_child_set"] = children,
    ["planner_live_set"] = live,
    ["planner_candidate_set"] = candidates,
    ["oracle_child_set"] = children,
    ["oracle_live_set"] = live,
    ["oracle_candidate_set"] = candidates,
    ["candidate_count"] = 1,
    ["protected_count"] = 1,
    ["blocked_count"] = 0,
    ["candidate_bytes"] = 100L,
    ["global_blockers"] = Array.Empty<object>(),
    ["anomalies"] = Array.Empty<object>(),
    ["error_message"] = null,
};

var output = new Dictionary<string, object?>
{
    ["cycle"] = cycle,
    ["candidateCountActual"] = 1,
    ["target"] = guitar,
    ["observations"] = observations,
    ["evidence"] = new[] { summary, childGuitar, childBass },
    ["publicationState"] = new Dictionary<string, object?>
    {
        ["published_scrape_id"] = triggerScrapeId,
        ["current_publication_id"] = triggerPublicationId,
        ["working_publication_id"] = null,
        ["public_reads_frozen"] = false,
        ["publication_commit_intent_started_at"] = null,
        ["publication_commit_intent_heartbeat_at"] = null,
        ["publication_commit_intent_owner"] = null,
        ["max_score_mutation_gate_token"] = null,
        ["max_score_mutation_gate_publication_id"] = null,
        ["max_score_mutation_gate_backend_pid"] = null,
        ["max_score_mutation_gate_backend_start"] = null,
        ["max_score_mutation_gate_acquired_at"] = null,
        ["improvement_notifications_scrape_id"] = triggerScrapeId,
        ["improvement_notifications_status"] = "completed",
        ["improvement_notifications_completed_at"] =
            "2026-08-29T20:00:00Z",
        ["improvement_notifications_projection_ready"] = true,
        ["improvement_notifications_projection_scrape_id"] = triggerScrapeId,
    },
    ["triggerScrape"] = new Dictionary<string, object?>
    {
        ["id"] = triggerScrapeId,
        ["status"] = "completed",
        ["completed_at"] = "2026-08-29T20:00:00Z",
        ["failed_at"] = null,
    },
    ["triggerPublication"] = new Dictionary<string, object?>
    {
        ["publication_id"] = triggerPublicationId,
        ["scrape_id"] = triggerScrapeId,
        ["status"] = "current",
    },
    ["runningScrapes"] = Array.Empty<long>(),
    ["activeHoldCount"] = 0L,
    ["unreplayedWriterFailureCount"] = 0L,
};

Console.WriteLine(JsonSerializer.Serialize(output, options));
