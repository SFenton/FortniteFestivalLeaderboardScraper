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
        ALTER TABLE scrape_log
            ADD COLUMN IF NOT EXISTS wire_send_total BIGINT;
        ALTER TABLE scrape_log
            ADD COLUMN IF NOT EXISTS wire_send_probe_sends BIGINT;
        ALTER TABLE scrape_log
            ADD COLUMN IF NOT EXISTS wire_send_probe_successes BIGINT;
        ALTER TABLE scrape_log
            ADD COLUMN IF NOT EXISTS wire_send_status_retries BIGINT;
        ALTER TABLE scrape_log
            ADD COLUMN IF NOT EXISTS wire_send_network_errors BIGINT;
        ALTER TABLE scrape_log
            ADD COLUMN IF NOT EXISTS wire_send_cdn_blocks BIGINT;

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

        DO $$
        BEGIN
            IF NOT EXISTS (
                SELECT 1
                FROM pg_constraint
                WHERE conrelid = 'scrape_log'::regclass
                  AND conname = 'ck_scrape_log_wire_send_telemetry')
            THEN
                ALTER TABLE scrape_log
                    ADD CONSTRAINT ck_scrape_log_wire_send_telemetry
                    CHECK (
                        (wire_send_total IS NULL OR wire_send_total >= 0)
                        AND (wire_send_probe_sends IS NULL OR wire_send_probe_sends >= 0)
                        AND (wire_send_probe_successes IS NULL
                             OR wire_send_probe_successes >= 0)
                        AND (wire_send_status_retries IS NULL
                             OR wire_send_status_retries >= 0)
                        AND (wire_send_network_errors IS NULL
                             OR wire_send_network_errors >= 0)
                        AND (wire_send_cdn_blocks IS NULL
                             OR wire_send_cdn_blocks >= 0)
                    ) NOT VALID;
            END IF;
        END $$;

        ALTER TABLE scrape_log
            VALIDATE CONSTRAINT ck_scrape_log_wire_send_telemetry;

        DO $$
        BEGIN
            IF NOT EXISTS (
                SELECT 1
                FROM pg_constraint
                WHERE conrelid = 'scrape_log'::regclass
                  AND conname = 'ck_scrape_log_wire_send_telemetry_complete')
            THEN
                ALTER TABLE scrape_log
                    ADD CONSTRAINT ck_scrape_log_wire_send_telemetry_complete
                    CHECK (
                        (
                            wire_send_total IS NULL
                            AND wire_send_probe_sends IS NULL
                            AND wire_send_probe_successes IS NULL
                            AND wire_send_status_retries IS NULL
                            AND wire_send_network_errors IS NULL
                            AND wire_send_cdn_blocks IS NULL
                        )
                        OR (
                            wire_send_total IS NOT NULL
                            AND wire_send_probe_sends IS NOT NULL
                            AND wire_send_probe_successes IS NOT NULL
                            AND wire_send_status_retries IS NOT NULL
                            AND wire_send_network_errors IS NOT NULL
                            AND wire_send_cdn_blocks IS NOT NULL
                            AND wire_send_probe_successes <= wire_send_probe_sends
                            AND wire_send_probe_sends <= wire_send_total
                            AND wire_send_status_retries <= wire_send_total
                            AND wire_send_network_errors <= wire_send_total
                            AND wire_send_cdn_blocks <= wire_send_total
                        )
                    ) NOT VALID;
            END IF;
        END $$;

        ALTER TABLE scrape_log
            VALIDATE CONSTRAINT ck_scrape_log_wire_send_telemetry_complete;
        """;
}
