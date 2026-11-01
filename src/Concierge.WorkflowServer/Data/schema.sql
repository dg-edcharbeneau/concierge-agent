-- Repeatable schema additions only. New NOT NULL columns must include a default or be
-- backfilled before enforcing NOT NULL. A change that cannot safely be repeated requires
-- a schema-version bump; older databases will then stop with reset guidance.
CREATE EXTENSION IF NOT EXISTS vector;
CREATE EXTENSION IF NOT EXISTS btree_gist;

CREATE TABLE IF NOT EXISTS guests (
    guest_id uuid PRIMARY KEY,
    given_name text NOT NULL CHECK (length(trim(given_name)) > 0),
    family_name text NOT NULL CHECK (length(trim(family_name)) > 0),
    created_at_utc timestamptz NOT NULL DEFAULT CURRENT_TIMESTAMP
);

CREATE TABLE IF NOT EXISTS stays (
    stay_id uuid PRIMARY KEY,
    external_stay_key text NOT NULL UNIQUE,
    guest_id uuid NOT NULL REFERENCES guests(guest_id),
    room_number varchar(4) NOT NULL CHECK (room_number ~ '^[0-9]{1,4}$'),
    check_in_utc timestamptz NOT NULL,
    check_out_utc timestamptz NOT NULL,
    eligibility_ends_at_utc timestamptz NOT NULL,
    created_at_utc timestamptz NOT NULL DEFAULT CURRENT_TIMESTAMP,
    CONSTRAINT stays_valid_interval CHECK (
        check_out_utc > check_in_utc
        AND eligibility_ends_at_utc >= check_out_utc
    )
);

CREATE INDEX IF NOT EXISTS ix_stays_guest_dates
    ON stays (guest_id, check_in_utc, check_out_utc);
CREATE INDEX IF NOT EXISTS ix_stays_room_eligibility
    ON stays (room_number, check_in_utc, eligibility_ends_at_utc);

CREATE TABLE IF NOT EXISTS knowledge_sources (
    source_id uuid PRIMARY KEY,
    source_key text NOT NULL UNIQUE,
    title text NOT NULL,
    source_uri text NOT NULL,
    media_type text NOT NULL DEFAULT 'text/markdown',
    metadata jsonb NOT NULL DEFAULT '{}'::jsonb,
    created_at_utc timestamptz NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at_utc timestamptz NOT NULL DEFAULT CURRENT_TIMESTAMP
);

CREATE TABLE IF NOT EXISTS ingestion_versions (
    ingestion_version_id uuid PRIMARY KEY,
    source_id uuid NOT NULL REFERENCES knowledge_sources(source_id) ON DELETE CASCADE,
    version_key text NOT NULL,
    content_sha256 char(64) NOT NULL CHECK (content_sha256 ~ '^[0-9A-Fa-f]{64}$'),
    status text NOT NULL CHECK (status IN ('pending', 'completed', 'failed')),
    started_at_utc timestamptz NOT NULL,
    completed_at_utc timestamptz,
    metadata jsonb NOT NULL DEFAULT '{}'::jsonb,
    CONSTRAINT uq_ingestion_versions_source_version UNIQUE (source_id, version_key),
    CONSTRAINT ingestion_versions_completion_consistent CHECK (
        (status = 'completed' AND completed_at_utc IS NOT NULL)
        OR (status <> 'completed')
    )
);

CREATE TABLE IF NOT EXISTS hotel_facts (
    fact_id uuid PRIMARY KEY,
    fact_key text NOT NULL UNIQUE,
    fact_value text NOT NULL,
    source_id uuid NOT NULL REFERENCES knowledge_sources(source_id),
    source_locator text,
    effective_from_utc timestamptz,
    effective_to_utc timestamptz,
    metadata jsonb NOT NULL DEFAULT '{}'::jsonb,
    updated_at_utc timestamptz NOT NULL DEFAULT CURRENT_TIMESTAMP,
    CONSTRAINT hotel_facts_valid_interval CHECK (
        effective_to_utc IS NULL
        OR effective_from_utc IS NULL
        OR effective_to_utc > effective_from_utc
    )
);

CREATE TABLE IF NOT EXISTS knowledge_chunks (
    chunk_id uuid PRIMARY KEY,
    ingestion_version_id uuid NOT NULL
        REFERENCES ingestion_versions(ingestion_version_id) ON DELETE CASCADE,
    stable_chunk_key text NOT NULL,
    ordinal integer NOT NULL CHECK (ordinal >= 0),
    title text NOT NULL,
    section_anchor text,
    content text NOT NULL CHECK (length(trim(content)) > 0),
    metadata jsonb NOT NULL DEFAULT '{}'::jsonb,
    search_vector tsvector GENERATED ALWAYS AS (
        to_tsvector(
            'english',
            coalesce(title, '') || ' ' ||
            coalesce(section_anchor, '') || ' ' ||
            coalesce(content, '')
        )
    ) STORED,
    CONSTRAINT uq_knowledge_chunks_version_key
        UNIQUE (ingestion_version_id, stable_chunk_key),
    CONSTRAINT uq_knowledge_chunks_version_ordinal
        UNIQUE (ingestion_version_id, ordinal)
);

CREATE INDEX IF NOT EXISTS ix_knowledge_chunks_search
    ON knowledge_chunks USING gin (search_vector);

CREATE TABLE IF NOT EXISTS transcript_turns (
    transcript_turn_id uuid PRIMARY KEY,
    stay_id uuid NOT NULL REFERENCES stays(stay_id) ON DELETE CASCADE,
    turn_id text NOT NULL,
    turn_sequence bigint NOT NULL CHECK (turn_sequence >= 0),
    channel text NOT NULL CHECK (channel IN ('web', 'voice')),
    guest_message text NOT NULL,
    assistant_message text NOT NULL,
    occurred_at_utc timestamptz NOT NULL,
    persisted_at_utc timestamptz NOT NULL DEFAULT CURRENT_TIMESTAMP,
    metadata jsonb NOT NULL DEFAULT '{}'::jsonb,
    CONSTRAINT uq_transcript_turns_stay_turn UNIQUE (stay_id, turn_id),
    CONSTRAINT uq_transcript_turns_stay_sequence UNIQUE (stay_id, turn_sequence)
);

CREATE INDEX IF NOT EXISTS ix_transcript_turns_stay_order
    ON transcript_turns (stay_id, turn_sequence, occurred_at_utc);

ALTER TABLE transcript_turns
    ADD COLUMN IF NOT EXISTS interrupted boolean NOT NULL DEFAULT FALSE;

ALTER TABLE transcript_turns
    ADD COLUMN IF NOT EXISTS superseded boolean NOT NULL DEFAULT FALSE;

CREATE TABLE IF NOT EXISTS knowledge_embedding_identity (
    identity_id smallint PRIMARY KEY DEFAULT 1 CHECK (identity_id = 1),
    provider text NOT NULL CHECK (length(trim(provider)) > 0),
    model text NOT NULL CHECK (length(trim(model)) > 0),
    dimensions integer NOT NULL CHECK (dimensions = 768),
    updated_at_utc timestamptz NOT NULL DEFAULT CURRENT_TIMESTAMP
);

ALTER TABLE knowledge_chunks
    ADD COLUMN IF NOT EXISTS embedding vector(768);

CREATE OR REPLACE FUNCTION refuse_populated_embedding_identity_change()
RETURNS trigger
LANGUAGE plpgsql
AS $$
BEGIN
    IF EXISTS (SELECT 1 FROM knowledge_chunks WHERE embedding IS NOT NULL) THEN
        RAISE EXCEPTION
            'Refusing embedding identity change while indexed vectors exist; clear and re-ingest knowledge embeddings first.';
    END IF;

    RETURN CASE WHEN TG_OP = 'DELETE' THEN OLD ELSE NEW END;
END;
$$;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1
        FROM pg_trigger
        WHERE tgname = 'trg_refuse_populated_embedding_identity_change'
          AND tgrelid = 'knowledge_embedding_identity'::regclass
          AND NOT tgisinternal
    ) THEN
        CREATE TRIGGER trg_refuse_populated_embedding_identity_change
        BEFORE UPDATE OR DELETE ON knowledge_embedding_identity
        FOR EACH ROW
        EXECUTE FUNCTION refuse_populated_embedding_identity_change();
    END IF;
END;
$$;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1
        FROM pg_constraint
        WHERE conname = 'ck_stays_local_day_eligibility'
          AND conrelid = 'stays'::regclass
    ) THEN
        ALTER TABLE stays
            ADD CONSTRAINT ck_stays_local_day_eligibility
            CHECK (
                check_in_utc =
                    ((check_in_utc AT TIME ZONE 'America/New_York')::date::timestamp
                        AT TIME ZONE 'America/New_York')
                AND check_out_utc =
                    ((check_out_utc AT TIME ZONE 'America/New_York')::date::timestamp
                        AT TIME ZONE 'America/New_York')
                AND eligibility_ends_at_utc =
                    (((check_out_utc AT TIME ZONE 'America/New_York')::date + 1)::timestamp
                        AT TIME ZONE 'America/New_York')
            );
    END IF;

    IF NOT EXISTS (
        SELECT 1
        FROM pg_constraint
        WHERE conname = 'ex_stays_room_eligibility_overlap'
          AND conrelid = 'stays'::regclass
    ) THEN
        ALTER TABLE stays
            ADD CONSTRAINT ex_stays_room_eligibility_overlap
            EXCLUDE USING gist (
                room_number WITH =,
                tstzrange(check_in_utc, eligibility_ends_at_utc, '[)') WITH &&
            );
    END IF;
END;
$$;

CREATE TABLE IF NOT EXISTS concierge_sessions (
    session_id uuid PRIMARY KEY,
    stay_id uuid NOT NULL REFERENCES stays(stay_id) ON DELETE CASCADE,
    credential_sha256 bytea NOT NULL UNIQUE CHECK (octet_length(credential_sha256) = 32),
    expires_at_utc timestamptz NOT NULL,
    ended_at_utc timestamptz,
    created_at_utc timestamptz NOT NULL DEFAULT CURRENT_TIMESTAMP
);

CREATE INDEX IF NOT EXISTS ix_concierge_sessions_stay
    ON concierge_sessions (stay_id, expires_at_utc)
    WHERE ended_at_utc IS NULL;
