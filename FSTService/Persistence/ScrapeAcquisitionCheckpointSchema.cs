namespace FSTService.Persistence;

internal static class ScrapeAcquisitionCheckpointSchema
{
    internal const string Sql = """
        ALTER TABLE scrape_log
            ADD COLUMN IF NOT EXISTS acquisition_completed_at TIMESTAMPTZ;
        ALTER TABLE scrape_log
            ADD COLUMN IF NOT EXISTS expected_solo_scope_count INTEGER;
        ALTER TABLE scrape_log
            ADD COLUMN IF NOT EXISTS expected_solo_scope_fingerprint_version INTEGER;
        ALTER TABLE scrape_log
            ADD COLUMN IF NOT EXISTS expected_solo_scope_fingerprint TEXT;

        DO $$
        BEGIN
            IF NOT EXISTS (
                SELECT 1
                FROM pg_constraint
                WHERE conrelid = 'scrape_log'::regclass
                  AND conname =
                      'ck_scrape_log_acquisition_checkpoint')
            THEN
                ALTER TABLE scrape_log
                    ADD CONSTRAINT ck_scrape_log_acquisition_checkpoint
                    CHECK (
                        acquisition_completed_at IS NULL
                        OR (
                            songs_scraped IS NOT NULL
                            AND songs_scraped >= 0
                            AND total_entries IS NOT NULL
                            AND total_entries >= 0
                            AND total_requests IS NOT NULL
                            AND total_requests >= 0
                            AND total_bytes IS NOT NULL
                            AND total_bytes >= 0
                            AND epic_reported_over_100_pages IS NOT NULL
                            AND expected_solo_scope_count IS NOT NULL
                            AND expected_solo_scope_count > 0
                            AND expected_solo_scope_fingerprint_version =
                                1
                            AND expected_solo_scope_fingerprint ~
                                '^[0-9a-f]{64}$'
                        )
                    ) NOT VALID;
            END IF;
        END $$;

        ALTER TABLE scrape_log
            VALIDATE CONSTRAINT ck_scrape_log_acquisition_checkpoint;
        """;
}
