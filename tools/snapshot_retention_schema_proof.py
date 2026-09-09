"""Source-preservation assertions for the owned offline-retention PG17 drill."""
import base64
import hashlib
import json
import time
from snapshot_retention_deployment_parity import classify_cumulative_counters, require_initializer_dml_proof

FLAG = "--initialize-snapshot-retention-schema-only"

STATE_FUNCTION_SQL = """
CREATE FUNCTION schema_repair_non_retention_state() RETURNS jsonb
LANGUAGE plpgsql AS $body$
DECLARE relation record; row_count bigint; row_hash text; result jsonb := '{}'::jsonb;
BEGIN
    FOR relation IN
        SELECT c.oid,c.relname,c.relfilenode FROM pg_class c
        JOIN pg_namespace n ON n.oid=c.relnamespace
        WHERE n.nspname='public' AND c.relkind IN ('r','p')
          AND c.relname NOT LIKE 'snapshot_generation_retention_%'
        ORDER BY c.relname
    LOOP
        EXECUTE format($query$
            SELECT count(*),encode(digest(
                coalesce(jsonb_agg(to_jsonb(row) ORDER BY to_jsonb(row)::text),'[]'::jsonb)::text,
                'sha256'),'hex') FROM ONLY public.%I row
            $query$,relation.relname) INTO row_count,row_hash;
        result := result || jsonb_build_object(relation.relname,jsonb_build_object(
            'oid',relation.oid,'relfilenode',relation.relfilenode,
            'rows',row_count,'sha256',row_hash));
    END LOOP;
    RETURN result;
END $body$;
"""

GUARDS_SQL = """
CREATE FUNCTION schema_repair_forbid_source_write() RETURNS trigger
LANGUAGE plpgsql AS $body$
BEGIN RAISE EXCEPTION 'non-retention statement forbidden in schema-only proof'; END $body$;
DO $body$
DECLARE relation record;
BEGIN
    FOR relation IN
        SELECT relname FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace
        WHERE n.nspname='public' AND relkind IN ('r','p')
          AND relname NOT LIKE 'snapshot_generation_retention_%'
    LOOP
        EXECUTE format(
            'CREATE TRIGGER schema_repair_source_guard
             BEFORE INSERT OR UPDATE OR DELETE OR TRUNCATE ON public.%I
             FOR EACH STATEMENT EXECUTE FUNCTION schema_repair_forbid_source_write()',
            relation.relname);
    END LOOP;
END $body$;
"""

REMOVE_GUARDS_SQL = """
DO $body$
DECLARE relation record;
BEGIN
    FOR relation IN
        SELECT c.relname FROM pg_trigger t JOIN pg_class c ON c.oid=t.tgrelid
        JOIN pg_namespace n ON n.oid=c.relnamespace
        WHERE n.nspname='public' AND t.tgname='schema_repair_source_guard'
    LOOP
        EXECUTE format('DROP TRIGGER schema_repair_source_guard ON public.%I',relation.relname);
    END LOOP;
END $body$;
"""

FULL_SOURCE_GUARDS_SQL = """
CREATE FUNCTION schema_repair_forbid_full_source_dml() RETURNS trigger
LANGUAGE plpgsql AS $body$
BEGIN RAISE EXCEPTION 'source row DML forbidden during full initialization proof'; END $body$;
DO $body$
DECLARE relation record;
BEGIN
    FOR relation IN
        SELECT relname,relkind FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace
        WHERE n.nspname='public' AND relkind IN ('r','p')
          AND relname NOT LIKE 'snapshot_generation_retention_%'
    LOOP
        IF relation.relkind='r' THEN
            EXECUTE format(
                'CREATE TRIGGER schema_repair_full_dml_guard AFTER INSERT OR UPDATE OR DELETE ON public.%I
                 FOR EACH ROW EXECUTE FUNCTION schema_repair_forbid_full_source_dml()',relation.relname);
        END IF;
        EXECUTE format(
            'CREATE TRIGGER schema_repair_full_truncate_guard BEFORE TRUNCATE ON public.%I
             FOR EACH STATEMENT EXECUTE FUNCTION schema_repair_forbid_full_source_dml()',relation.relname);
    END LOOP;
END $body$;
"""

REMOVE_FULL_SOURCE_GUARDS_SQL = """
DO $body$
DECLARE item record;
BEGIN
    FOR item IN
        SELECT c.relname,t.tgname FROM pg_trigger t JOIN pg_class c ON c.oid=t.tgrelid
        JOIN pg_namespace n ON n.oid=c.relnamespace
        WHERE n.nspname='public' AND t.tgname IN ('schema_repair_full_dml_guard','schema_repair_full_truncate_guard')
    LOOP
        EXECUTE format('DROP TRIGGER %I ON public.%I',item.tgname,item.relname);
    END LOOP;
END $body$;
DROP FUNCTION schema_repair_forbid_full_source_dml();
"""

COUNTERS_SQL = """
SELECT coalesce(jsonb_agg(jsonb_build_object(
    'relation',relname,'inserted',n_tup_ins,'updated',n_tup_upd,'deleted',n_tup_del)
    ORDER BY relname),'[]'::jsonb)::text
FROM pg_stat_user_tables
WHERE schemaname='public' AND relname NOT LIKE 'snapshot_generation_retention_%';
"""

NON_RETENTION_SCHEMA_SQL = """
WITH relations AS (
    SELECT c.oid,c.relname,c.relkind FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace
    WHERE n.nspname='public' AND c.relkind IN ('r','p')
      AND c.relname NOT LIKE 'snapshot_generation_retention_%'
)
SELECT jsonb_build_object(
    'columns',(SELECT jsonb_agg(jsonb_build_object('table',r.relname,'column',a.attname,
        'type',format_type(a.atttypid,a.atttypmod),'notNull',a.attnotnull,
        'default',pg_get_expr(d.adbin,d.adrelid)) ORDER BY r.relname,a.attnum)
        FROM relations r JOIN pg_attribute a ON a.attrelid=r.oid
        LEFT JOIN pg_attrdef d ON d.adrelid=r.oid AND d.adnum=a.attnum
        WHERE a.attnum>0 AND NOT a.attisdropped),
    'constraints',(SELECT jsonb_agg(jsonb_build_object('table',r.relname,'name',c.conname,
        'definition',pg_get_constraintdef(c.oid)) ORDER BY r.relname,c.conname)
        FROM relations r JOIN pg_constraint c ON c.conrelid=r.oid),
    'indexes',(SELECT jsonb_agg(pg_get_indexdef(i.indexrelid) ORDER BY pg_get_indexdef(i.indexrelid) COLLATE "C")
        FROM relations r JOIN pg_index i ON i.indrelid=r.oid),
    'triggers',(SELECT jsonb_agg(pg_get_triggerdef(t.oid) ORDER BY pg_get_triggerdef(t.oid) COLLATE "C")
        FROM relations r JOIN pg_trigger t ON t.tgrelid=r.oid),
    'functions',(SELECT jsonb_agg(pg_get_functiondef(p.oid) ORDER BY pg_get_functiondef(p.oid) COLLATE "C")
        FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace
        WHERE n.nspname='public' AND p.prokind IN ('f','p')
          AND p.proname<>'fst_reject_snapshot_generation_retention_evidence_mutation')
)::text;
"""

BINDING_SQL = """
SELECT to_jsonb(binding)::text FROM publication_surface_bindings binding
WHERE publication_id=(SELECT current_publication_id FROM scrape_publication_state WHERE id)
  AND surface_name='path_artifacts';
"""

LEGACY_SCHEMA_SQL = """
DROP TABLE snapshot_generation_retention_worker_configuration;
ALTER TABLE snapshot_generation_retention_cycles DROP CONSTRAINT ux_snapshot_generation_retention_cycle_trigger;
ALTER TABLE snapshot_generation_retention_cycles DROP CONSTRAINT ck_snapshot_generation_retention_cycle_safe_point;
ALTER TABLE snapshot_generation_retention_cycles ADD CONSTRAINT ck_snapshot_generation_retention_cycle_safe_point
    CHECK (safe_point_kind IN ('terminal_worker_post_publication'));
ALTER TABLE snapshot_generation_retention_deferrals DROP CONSTRAINT ck_snapshot_generation_retention_deferral_safe_point;
ALTER TABLE snapshot_generation_retention_deferrals ADD CONSTRAINT ck_snapshot_generation_retention_deferral_safe_point
    CHECK (safe_point_kind IN ('terminal_worker_post_publication'));
"""

SCHEMA_SQL = """
SELECT jsonb_build_object(
    'constraints',(SELECT jsonb_agg(jsonb_build_object('name',conname,
        'definition',pg_get_constraintdef(oid),'validated',convalidated) ORDER BY conname)
        FROM pg_constraint WHERE conname IN ('ux_snapshot_generation_retention_cycle_trigger',
            'ck_snapshot_generation_retention_cycle_safe_point','ck_snapshot_generation_retention_deferral_safe_point')),
    'receiptPresent',to_regclass('public.snapshot_generation_retention_worker_configuration') IS NOT NULL,
    'receiptColumns',(SELECT jsonb_agg(jsonb_build_object('name',column_name,'type',data_type,
        'nullable',is_nullable) ORDER BY ordinal_position) FROM information_schema.columns
        WHERE table_schema='public' AND table_name='snapshot_generation_retention_worker_configuration')
)::text;
"""


def binding_evidence(raw):
    return {"row": json.loads(raw), "sha256": hashlib.sha256(raw.encode()).hexdigest()}


def restore_fixture_binding(sql, original):
    encoded = base64.b64encode(original.encode()).decode()
    sql("""
        UPDATE publication_surface_bindings target SET
            binding_json=source.binding_json,binding_kind=source.binding_kind,
            row_count=source.row_count,content_hash=source.content_hash,
            status=source.status,built_at=source.built_at
        FROM jsonb_populate_record(NULL::publication_surface_bindings,
            convert_from(decode('""" + encoded + """','base64'),'UTF8')::jsonb) source
        WHERE target.publication_id=source.publication_id AND target.surface_name=source.surface_name;
        """)
    if sql(BINDING_SQL) != original:
        raise RuntimeError("The disposable fixture binding reset was not exact")


def full_executable_parity(sql, invoke_service, write, name, original):
    sql(FULL_SOURCE_GUARDS_SQL)
    time.sleep(1.2)
    before = json.loads(sql("SELECT schema_repair_non_retention_state()::text;"))
    counters_before = json.loads(sql(COUNTERS_SQL))
    schema_before = json.loads(sql(NON_RETENTION_SCHEMA_SQL))
    invoke_service(name, ["--initialize-schema-only"])
    time.sleep(1.2)
    current = sql(BINDING_SQL)
    after = json.loads(sql("SELECT schema_repair_non_retention_state()::text;"))
    counters_after = json.loads(sql(COUNTERS_SQL))
    schema_after = json.loads(sql(NON_RETENTION_SCHEMA_SQL))
    evidence = {
        "command": "--initialize-schema-only",
        "bindingBefore": binding_evidence(original), "bindingAfter": binding_evidence(current),
        "nonRetentionBefore": before, "nonRetentionAfter": after,
        "mutationCountersBefore": counters_before, "mutationCountersAfter": counters_after,
        "cumulativeCounterTelemetry": classify_cumulative_counters(counters_before, counters_after),
        "nonRetentionSchemaBefore": schema_before, "nonRetentionSchemaAfter": schema_after,
        "actualSourceDmlAndTruncateTrapsInstalled": True,
    }
    write(name + "-parity.json", evidence)
    if current != original or before != after or schema_before != schema_after:
        raise RuntimeError("Full executable initialization changed binding/source rows or schema: " + name)
    sql(REMOVE_FULL_SOURCE_GUARDS_SQL)
    return binding_evidence(current)


def run_schema_repair_proof(sql, invoke_service, write, baseline_service=None, serve_degraded=None):
    sql(STATE_FUNCTION_SQL)
    original = sql(BINDING_SQL)
    binding = json.loads(original)
    metadata = binding["binding_json"]
    if metadata.get("source") != "generation_prepared_snapshot" \
            or metadata.get("operatorEvidence") != "preserve-exactly" \
            or metadata.get("table") != "publication_path_artifacts" \
            or metadata.get("authoritative") is not True \
            or metadata.get("publicationId") != binding["publication_id"] \
            or metadata.get("contractVersion") != 1 or metadata.get("manifestVersion") != 2 \
            or metadata.get("expectedRowCount") != binding["row_count"] \
            or binding["binding_kind"] != "generation_path_artifact_manifest" \
            or binding["status"] != "ready" or binding["row_count"] != 1 \
            or binding["built_at"] != "2026-08-02T03:04:05.123456+00:00" \
            or len(binding["content_hash"]) != 64:
        raise RuntimeError("The executable proof requires its exact canonical nonlegacy starting binding")
    identity = json.loads(sql("""
        SELECT jsonb_build_object('scrapeId',generation.scrape_id,
            'hash',publication_path_artifact_manifest_sha256(generation.publication_id))::text
        FROM publication_generations generation
        JOIN scrape_publication_state state ON state.current_publication_id=generation.publication_id
        WHERE state.id;
        """))
    if metadata.get("scrapeId") != identity["scrapeId"] or binding["content_hash"] != identity["hash"]:
        raise RuntimeError("The starting binding must match its exact scrape and canonical manifest hash")
    write("schema-original-binding.json", binding_evidence(original))
    reproduction = None
    if baseline_service is not None:
        invoke_service("schema-baseline-full-initialization", ["--initialize-schema-only"],
                       service=baseline_service)
        mutated = sql(BINDING_SQL)
        reproduction = {"before": binding_evidence(original), "after": binding_evidence(mutated)}
        if mutated == original:
            raise RuntimeError("The exact baseline did not reproduce the live binding mutation")
        write("schema-baseline-mutation-reproduced.json", reproduction)
        restore_fixture_binding(sql, original)

    full = []
    for attempt in range(2):
        full.append(full_executable_parity(
            sql, invoke_service, write, "schema-full-preservation-" + str(attempt), original))
    write("schema-full-initialization-parity.json", full)

    sql(LEGACY_SCHEMA_SQL)
    legacy = json.loads(sql(SCHEMA_SQL))
    write("schema-legacy-retention-shape.json", legacy)
    sql(GUARDS_SQL)
    time.sleep(1.2)
    before = json.loads(sql("SELECT schema_repair_non_retention_state()::text;"))
    counters_before = json.loads(sql(COUNTERS_SQL))
    schema_before = json.loads(sql(NON_RETENTION_SCHEMA_SQL))
    write("schema-non-retention-before.json", before)
    write("schema-mutation-counters-before.json", counters_before)
    write("schema-non-retention-definitions-before.json", schema_before)
    results = []
    for attempt in range(2):
        result = invoke_service("schema-retention-only-" + str(attempt), [FLAG], json_output=True)
        require_initializer_dml_proof(result)
        time.sleep(1.2)
        after = json.loads(sql("SELECT schema_repair_non_retention_state()::text;"))
        counters_after = json.loads(sql(COUNTERS_SQL))
        schema_after = json.loads(sql(NON_RETENTION_SCHEMA_SQL))
        write("schema-non-retention-after-" + str(attempt) + ".json", after)
        write("schema-mutation-counters-after-" + str(attempt) + ".json", counters_after)
        write("schema-non-retention-definitions-after-" + str(attempt) + ".json", schema_after)
        write("schema-cumulative-counter-telemetry-" + str(attempt) + ".json",
              classify_cumulative_counters(counters_before, counters_after))
        if after != before or schema_after != schema_before \
                or sql(BINDING_SQL) != original:
            raise RuntimeError("Retention-only initialization changed non-retention rows, schema or binding")
        results.append(result)
    write("schema-transaction-local-dml-proofs.json", results)
    upgraded = json.loads(sql(SCHEMA_SQL))
    write("schema-upgraded-retention-shape.json", upgraded)
    if not upgraded["receiptPresent"] or len(upgraded["receiptColumns"]) != 8 \
            or len(upgraded["constraints"]) != 3 \
            or not all(item["validated"] for item in upgraded["constraints"]):
        raise RuntimeError("The dedicated initializer did not install the accepted retention schema")

    refusals = []
    for other in ("--initialize-schema-only", "--replay-tier0", "--once", "--backfill-only",
                  "--recover-improvement-notifications", "--max-score-maintenance",
                  "--sql", "--path", "--target", "--archive", "--drop"):
        result = invoke_service("schema-mixed-" + other.lstrip("-"), [FLAG, other],
                                expected_exit=64, json_output=True)
        if result.get("code") != "invalid_command_arguments":
            raise RuntimeError("A mixed schema command reached another execution surface")
        refusals.append(other)
    if json.loads(sql("SELECT schema_repair_non_retention_state()::text;")) != before:
        raise RuntimeError("Rejected CLI arguments changed source rows")
    sql(REMOVE_GUARDS_SQL)
    for kind, statement, expected in (
        ("dml", "INSERT INTO public.schema_repair_injected_source VALUES(3)", "non_retention_dml_detected"),
        ("truncate", "TRUNCATE public.schema_repair_injected_source", "non_retention_relation_identity_changed"),
        ("rewrite", "ALTER TABLE public.schema_repair_injected_source ALTER COLUMN value TYPE bigint USING value::bigint",
         "non_retention_relation_identity_changed"),
    ):
        sql("""
            CREATE TABLE schema_repair_injected_source(value integer);
            INSERT INTO schema_repair_injected_source VALUES(1),(2);
            CREATE FUNCTION schema_repair_inject_dml() RETURNS event_trigger LANGUAGE plpgsql AS $body$
            BEGIN
                IF pg_catalog.current_setting('application_name')='fst-snapshot-retention-schema-only'
                   AND pg_catalog.current_setting('fst.schema_repair_injected',true) IS DISTINCT FROM 'yes'
                THEN
                    PERFORM pg_catalog.set_config('fst.schema_repair_injected','yes',true);
                    """ + statement + """;
                END IF;
            END $body$;
            CREATE EVENT TRIGGER schema_repair_inject_dml ON ddl_command_start
                EXECUTE FUNCTION schema_repair_inject_dml();
            """)
        injection_before = json.loads(sql("SELECT schema_repair_non_retention_state()::text;"))
        injected = invoke_service("schema-injected-non-retention-" + kind, [FLAG], expected_exit=2, json_output=True)
        injection_after = json.loads(sql("SELECT schema_repair_non_retention_state()::text;"))
        if injected.get("code") != expected or injected.get("transactionCommitted") is not False \
                or injected["dmlProof"]["version"] != 2 or injection_before != injection_after:
            raise RuntimeError("The real dedicated CLI failed to reject and roll back injected source mutation: " + kind)
        if kind == "dml" and injected["dmlProof"]["nonRetentionDml"]["inserted"] <= 0:
            raise RuntimeError("Injected tuple DML was not counted")
        if kind != "dml" and injected["dmlProof"]["nonRetentionRelationIdentity"]["unchanged"] is not False:
            raise RuntimeError("Injected source identity mutation was not captured")
        write("schema-injected-" + kind + "-rollback-proof.json", {
            "response": injected, "sourceBefore": injection_before, "sourceAfter": injection_after,
            "sourceRowsUnchanged": True, "transactionRolledBack": True,
        })
        sql("""
            DROP EVENT TRIGGER schema_repair_inject_dml;
            DROP FUNCTION schema_repair_inject_dml();
            DROP TABLE schema_repair_injected_source;
            """)
    for attempt in range(2):
        full_executable_parity(
            sql, invoke_service, write, "schema-post-upgrade-full-" + str(attempt), original)
    diagnostics = []
    for defect, patch, expected in (
        ("future", '{"manifestVersion":3}', "manifest_version_future"),
        ("malformed", '{"manifestVersion":0}', "manifest_version_invalid"),
        ("ready-contract", '{"publicationId":999999}', "binding_publication_mismatch"),
    ):
        encoded = base64.b64encode(patch.encode()).decode()
        sql("""
            UPDATE publication_surface_bindings SET binding_json=binding_json
                || convert_from(decode('""" + encoded + """','base64'),'UTF8')::jsonb
            WHERE publication_id=(SELECT current_publication_id FROM scrape_publication_state WHERE id)
              AND surface_name='path_artifacts';
            """)
        invalid = sql(BINDING_SQL)
        invocation = invoke_service("schema-diagnostic-" + defect, ["--initialize-schema-only"], expected_exit=2)
        records = [json.loads(line) for line in invocation.stderr.splitlines() if line.startswith("{")]
        if not any(record.get("code") == "path_artifact_initialization_rejected"
                   and any(item["Code"] == expected for item in record["failures"])
                   for record in records) or sql(BINDING_SQL) != invalid:
            raise RuntimeError("Invalid active binding was silent, accepted or rewritten: " + defect)
        diagnostics.append({"defect": defect, "code": expected, "bindingUnchanged": True,
                            "structuredStderr": records})
        restore_fixture_binding(sql, original)
    write("schema-executable-diagnostics.json", diagnostics)
    availability = []
    if serve_degraded is not None:
        payload = '[{"songId":"schema-repair-song","title":"persisted-read-proof"}]'
        encoded = base64.b64encode(payload.encode()).decode()
        sql("""
            INSERT INTO api_response_cache(cache_key,json_data,etag,cached_at)
            VALUES('public-api:songs:v1',decode('""" + encoded + """','base64'),'"persisted-read-proof"',now())
            ON CONFLICT(cache_key) DO UPDATE SET json_data=excluded.json_data,etag=excluded.etag,cached_at=excluded.cached_at;
            INSERT INTO publication_api_response_cache(publication_id,cache_key,json_data,etag,cached_at)
            SELECT current_publication_id,'public-api:songs:v1',decode('""" + encoded + """','base64'),
                '"persisted-read-proof"',now() FROM scrape_publication_state WHERE id
            ON CONFLICT(publication_id,cache_key) DO UPDATE SET
                json_data=excluded.json_data,etag=excluded.etag,cached_at=excluded.cached_at;
            """)
        catalog = sql("""
            SELECT to_jsonb(catalog)::text FROM publication_song_catalog catalog
            JOIN scrape_publication_state state ON state.current_publication_id=catalog.publication_id WHERE state.id;
            """)
        for defect in ("inexact-catalog", "missing-catalog", "working-invalid"):
            if defect == "inexact-catalog":
                sql("""
                    UPDATE publication_song_catalog SET is_exact=FALSE
                    WHERE publication_id=(SELECT current_publication_id FROM scrape_publication_state WHERE id);
                    """)
            elif defect == "missing-catalog":
                sql("""
                    DELETE FROM publication_song_catalog
                    WHERE publication_id=(SELECT current_publication_id FROM scrape_publication_state WHERE id);
                    """)
            else:
                sql("""
                    INSERT INTO scrape_log(id,started_at,status)
                    SELECT max(id)+1,now(),'running' FROM scrape_log;
                    INSERT INTO publication_generations(publication_id,scrape_id,status,created_at)
                    SELECT (SELECT max(publication_id)+1 FROM publication_generations),
                        (SELECT max(id) FROM scrape_log),'building',now();
                    UPDATE scrape_publication_state
                    SET working_publication_id=(SELECT max(publication_id) FROM publication_generations),
                        public_reads_frozen=TRUE,public_reads_frozen_at=now(),
                        public_reads_frozen_scrape_id=published_scrape_id,public_reads_frozen_reason='scrape'
                    WHERE id;
                    INSERT INTO publication_surface_bindings(publication_id,surface_name,binding_kind,binding_json,
                        row_count,content_hash,status,built_at)
                    SELECT state.working_publication_id,'path_artifacts',binding.binding_kind,
                        binding.binding_json || jsonb_build_object('publicationId',state.working_publication_id,
                            'scrapeId',(SELECT max(id) FROM scrape_log),'manifestVersion',3),
                        binding.row_count,binding.content_hash,'ready',binding.built_at
                    FROM scrape_publication_state state
                    JOIN publication_surface_bindings binding
                      ON binding.publication_id=state.current_publication_id AND binding.surface_name='path_artifacts'
                    WHERE state.id;
                    """)
            time.sleep(1.2)
            before_state = json.loads(sql("SELECT schema_repair_non_retention_state()::text;"))
            before_counters = json.loads(sql(COUNTERS_SQL))
            invocation = invoke_service("startup-" + defect + "-explicit-cli",
                                        ["--initialize-schema-only"], expected_exit=2)
            if "path_artifact_initialization_rejected" not in invocation.stderr:
                raise RuntimeError("The explicit full CLI did not preserve its structured refusal")
            if json.loads(sql("SELECT schema_repair_non_retention_state()::text;")) != before_state:
                raise RuntimeError("The explicit full CLI changed rows before refusing a reachable invalid publication")
            serve_degraded("startup-" + defect, [] if defect == "working-invalid" else ["--api-only"])
            time.sleep(1.2)
            after_state = json.loads(sql("SELECT schema_repair_non_retention_state()::text;"))
            after_counters = json.loads(sql(COUNTERS_SQL))
            write("startup-" + defect + "-source-parity.json", {
                "before": before_state, "after": after_state,
                "countersBefore": before_counters, "countersAfter": after_counters,
                "cumulativeCounterTelemetry": classify_cumulative_counters(before_counters, after_counters)})
            if before_state != after_state:
                raise RuntimeError("Ordinary degraded startup or HTTP requests changed source state: " + defect)
            availability.append(defect)
            if defect == "inexact-catalog":
                sql("""
                    UPDATE publication_song_catalog SET is_exact=TRUE
                    WHERE publication_id=(SELECT current_publication_id FROM scrape_publication_state WHERE id);
                    """)
            elif defect == "missing-catalog":
                encoded_catalog = base64.b64encode(catalog.encode()).decode()
                sql("""
                    INSERT INTO publication_song_catalog
                    SELECT * FROM jsonb_populate_record(NULL::publication_song_catalog,
                        convert_from(decode('""" + encoded_catalog + """','base64'),'UTF8')::jsonb);
                    """)
    write("schema-source-preservation-summary.json", {
        "baselineMutationReproduced": reproduction is not None,
        "fullInitializationParityRuns": 4, "retentionOnlyParityRuns": 2,
        "mixedCommandsRefused": refusals, "nonRetentionTables": len(before),
        "nonRetentionRowsAndSchemaUnchanged": True,
        "initializerTransactionNonRetentionDmlZero": True,
        "initializerDmlAllowlist": [],
        "injectedNonRetentionDmlRefusedAndRolledBack": True,
        "injectedTruncateAndRewriteRefusedAndRolledBack": True,
        "combinedProofVersion": 2,
        "cumulativeCountersUsedForAcceptance": False,
        "fullBindingJsonAndBuiltAtPreserved": True,
        "fullExecutableNonRetentionRowsAndSchemaUnchanged": True,
        "fullExecutableActualSourceDmlTrapsPassed": True,
        "invalidBindingsRefusedWithStructuredDiagnostics": diagnostics,
        "ordinaryDegradedServingCases": availability,
        "onlyRetentionSchemaChanged": True,
        "publicationRowsManuallyRestored": "owned fixture reproduction/diagnostic resets only",
    })
    return {"outcome": "passed", "schemaOnlyRepairProof": True,
            "baselineMutationReproduced": reproduction is not None,
            "nonRetentionTables": len(before), "fullInitializationParityRuns": 4,
            "retentionOnlyParityRuns": 2, "mixedCommandRefusals": len(refusals),
            "executableDiagnosticRefusals": len(diagnostics),
            "ordinaryDegradedServingCases": len(availability),
            "causalDmlProofs": len(results), "injectedDmlRefusals": 1,
            "injectedIdentityRefusals": 2, "combinedProofVersion": 2,
            "cumulativeCountersUsedForAcceptance": False}
