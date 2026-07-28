\set ON_ERROR_STOP on

SET statement_timeout = '90min';
SET lock_timeout = '5s';
SET synchronous_commit = off;
SET wal_compression = on;
SET maintenance_work_mem = '1GB';
SET max_parallel_maintenance_workers = 1;

CREATE UNIQUE INDEX IF NOT EXISTS band_team_rank_history_points_v3_duets_202604_uidx
    ON public.band_team_rank_history_points_v3_duets_202604
       (team_id, scope_id, combo_ref, snapshot_date);

CREATE UNIQUE INDEX IF NOT EXISTS band_team_rank_history_points_v3_duets_202605_uidx
    ON public.band_team_rank_history_points_v3_duets_202605
       (team_id, scope_id, combo_ref, snapshot_date);

CREATE UNIQUE INDEX IF NOT EXISTS band_team_rank_history_points_v3_duets_202606_uidx
    ON public.band_team_rank_history_points_v3_duets_202606
       (team_id, scope_id, combo_ref, snapshot_date);

CREATE UNIQUE INDEX IF NOT EXISTS band_team_rank_history_points_v3_duets_202607_uidx
    ON public.band_team_rank_history_points_v3_duets_202607
       (team_id, scope_id, combo_ref, snapshot_date);

CREATE UNIQUE INDEX band_team_rank_history_points_v3_duets_uidx
    ON ONLY public.band_team_rank_history_points_v3_duets
       (team_id, scope_id, combo_ref, snapshot_date);

ALTER INDEX public.band_team_rank_history_points_v3_duets_uidx
    ATTACH PARTITION public.band_team_rank_history_points_v3_duets_202604_uidx;
ALTER INDEX public.band_team_rank_history_points_v3_duets_uidx
    ATTACH PARTITION public.band_team_rank_history_points_v3_duets_202605_uidx;
ALTER INDEX public.band_team_rank_history_points_v3_duets_uidx
    ATTACH PARTITION public.band_team_rank_history_points_v3_duets_202606_uidx;
ALTER INDEX public.band_team_rank_history_points_v3_duets_uidx
    ATTACH PARTITION public.band_team_rank_history_points_v3_duets_202607_uidx;

ALTER TABLE public.band_team_rank_history_points_v3_duets
    ADD CONSTRAINT band_team_rank_history_points_v3_duets_pkey
    PRIMARY KEY USING INDEX band_team_rank_history_points_v3_duets_uidx;

ANALYZE public.band_team_rank_history_points_v3_duets;
CHECKPOINT;
