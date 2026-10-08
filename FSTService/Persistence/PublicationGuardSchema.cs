namespace FSTService.Persistence;

/// <summary>
/// Repo-owned publication pointer guard. The trigger runs inside the
/// exclusive publication cutover, so it keeps only the indexed failed-phase
/// check. Impossible ranking denominators are rejected earlier, outside the
/// cutover lock, by publication preparation.
/// </summary>
/// <remarks>
/// Production previously carried an operator-installed version of this
/// function that also repaired and scanned <c>account_rankings</c> and
/// <c>solo_family_rankings</c> (about 8 seconds against a 5-second cutover
/// budget), plus <c>fst_account_rankings_denominator_guard_1100</c>, a row
/// trigger with stale hard-coded denominators that rewrote rather than
/// rejected impossible rows. This step replaces the former and removes the
/// latter; both are idempotent.
/// </remarks>
internal static class PublicationGuardSchema
{
    internal const string StepName = "publication-guard";
    internal const string GuardFunctionName = "guard_scrape_publication_no_failed_phases";
    internal const string GuardTriggerName = "trg_guard_scrape_publication_no_failed_phases";
    internal const string LegacyDenominatorFunctionName = "fst_account_rankings_denominator_guard_1100";
    internal const string LegacyDenominatorTriggerName = "trg_account_rankings_denominator_guard_1100";

    internal const string Sql = """
        CREATE OR REPLACE FUNCTION guard_scrape_publication_no_failed_phases()
        RETURNS trigger
        LANGUAGE plpgsql
        AS $guard$
        BEGIN
            IF NEW.published_scrape_id IS DISTINCT FROM OLD.published_scrape_id THEN
                IF EXISTS (
                    SELECT 1
                    FROM scrape_phase_timings
                    WHERE scrape_id = NEW.published_scrape_id
                      AND success IS FALSE
                ) THEN
                    RAISE EXCEPTION 'Refusing to publish scrape % because failed scrape_phase_timings rows exist',
                        NEW.published_scrape_id;
                END IF;
            END IF;
            RETURN NEW;
        END;
        $guard$;

        DO $create_guard$
        BEGIN
            IF NOT EXISTS (
                SELECT 1
                FROM pg_trigger
                WHERE tgrelid = 'scrape_publication_state'::regclass
                  AND tgname = 'trg_guard_scrape_publication_no_failed_phases'
                  AND NOT tgisinternal
            ) THEN
                CREATE TRIGGER trg_guard_scrape_publication_no_failed_phases
                    BEFORE UPDATE OF published_scrape_id ON scrape_publication_state
                    FOR EACH ROW
                    EXECUTE FUNCTION guard_scrape_publication_no_failed_phases();
            END IF;
        END;
        $create_guard$;

        -- Only touch account_rankings when the legacy trigger exists: DROP
        -- TRIGGER takes an ACCESS EXCLUSIVE lock even when there is nothing
        -- to drop. Dropping a parent trigger also drops its partition clones.
        DO $drop_legacy_denominator_guard$
        DECLARE
            legacy RECORD;
        BEGIN
            FOR legacy IN
                SELECT tgrelid::regclass AS relation
                FROM pg_trigger
                WHERE tgname = 'trg_account_rankings_denominator_guard_1100'
                  AND tgparentid = 0
                  AND NOT tgisinternal
            LOOP
                EXECUTE format(
                    'DROP TRIGGER IF EXISTS trg_account_rankings_denominator_guard_1100 ON %s',
                    legacy.relation);
            END LOOP;
        END;
        $drop_legacy_denominator_guard$;

        DROP FUNCTION IF EXISTS fst_account_rankings_denominator_guard_1100();
        """;
}
