\set ON_ERROR_STOP on

SET lock_timeout = '5s';
SET statement_timeout = '30min';

CREATE TABLE IF NOT EXISTS public.band_rank_history_compact_v3_state (
    band_type TEXT PRIMARY KEY,
    status TEXT NOT NULL,
    row_count BIGINT NOT NULL DEFAULT 0,
    min_snapshot_date DATE,
    max_snapshot_date DATE,
    validated_at TIMESTAMPTZ,
    promoted_at TIMESTAMPTZ,
    updated_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE TABLE public.band_rank_history_team_v3_duets (
    team_id BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    team_key TEXT NOT NULL UNIQUE
) WITH (fillfactor = 100);

CREATE TABLE public.band_rank_history_combo_v3_duets (
    combo_ref INTEGER GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    combo_id TEXT NOT NULL UNIQUE
) WITH (fillfactor = 100);

CREATE TABLE public.band_team_rank_history_points_v3_duets (
    team_id BIGINT NOT NULL,
    scope_id SMALLINT NOT NULL CHECK (scope_id IN (0, 1)),
    combo_ref INTEGER NOT NULL DEFAULT 0,
    snapshot_date DATE NOT NULL,
    snapshot_id BIGINT NOT NULL,
    generation_id BIGINT NOT NULL,
    snapshot_taken_at TIMESTAMPTZ NOT NULL,
    row_fingerprint BYTEA NOT NULL CHECK (octet_length(row_fingerprint) = 16),
    adjusted_skill_rank INT NOT NULL,
    weighted_rank INT NOT NULL,
    fc_rate_rank INT NOT NULL,
    total_score_rank INT NOT NULL,
    adjusted_skill_rating DOUBLE PRECISION,
    weighted_rating DOUBLE PRECISION,
    fc_rate DOUBLE PRECISION,
    total_score BIGINT,
    songs_played INT,
    coverage DOUBLE PRECISION,
    full_combo_count INT,
    total_charted_songs INT,
    total_ranked_teams INT,
    raw_weighted_rating DOUBLE PRECISION,
    raw_skill_rating DOUBLE PRECISION
) PARTITION BY RANGE (snapshot_date);

CREATE TABLE public.band_team_rank_history_points_v3_duets_202604
    PARTITION OF public.band_team_rank_history_points_v3_duets
    FOR VALUES FROM ('2026-04-01') TO ('2026-05-01')
    WITH (fillfactor = 100);

CREATE TABLE public.band_team_rank_history_points_v3_duets_202605
    PARTITION OF public.band_team_rank_history_points_v3_duets
    FOR VALUES FROM ('2026-05-01') TO ('2026-06-01')
    WITH (fillfactor = 100);

CREATE TABLE public.band_team_rank_history_points_v3_duets_202606
    PARTITION OF public.band_team_rank_history_points_v3_duets
    FOR VALUES FROM ('2026-06-01') TO ('2026-07-01')
    WITH (fillfactor = 100);

CREATE TABLE public.band_team_rank_history_points_v3_duets_202607
    PARTITION OF public.band_team_rank_history_points_v3_duets
    FOR VALUES FROM ('2026-07-01') TO ('2026-08-01')
    WITH (fillfactor = 100);

INSERT INTO public.band_rank_history_compact_v3_state (band_type, status)
VALUES ('Band_Duets', 'building')
ON CONFLICT (band_type) DO UPDATE
SET status = 'building',
    updated_at = now();
