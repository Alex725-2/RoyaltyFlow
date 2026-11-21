-- PostgreSQL 16. The initial EF migration embeds this script.
CREATE EXTENSION IF NOT EXISTS btree_gist;

CREATE TABLE artists (
    id uuid PRIMARY KEY,
    name varchar(200) NOT NULL CHECK (btrim(name) <> '')
);
CREATE TABLE tracks (
    id uuid PRIMARY KEY,
    artist_id uuid NOT NULL REFERENCES artists(id),
    title varchar(200) NOT NULL CHECK (btrim(title) <> '')
);
CREATE INDEX ix_tracks_artist_id ON tracks (artist_id);

CREATE TABLE rights_holders (
    id uuid PRIMARY KEY,
    name varchar(200) NOT NULL CHECK (btrim(name) <> '')
);
CREATE TABLE royalty_contracts (
    id uuid PRIMARY KEY,
    track_id uuid NOT NULL REFERENCES tracks(id),
    valid_from date NOT NULL,
    valid_to date,
    CONSTRAINT ck_contract_period CHECK (valid_to IS NULL OR valid_to > valid_from),
    CONSTRAINT ex_contract_period EXCLUDE USING gist
        (track_id WITH =, daterange(valid_from, valid_to, '[)') WITH &&)
);
CREATE INDEX ix_royalty_contracts_track_id ON royalty_contracts (track_id);

CREATE TABLE royalty_shares (
    contract_id uuid NOT NULL REFERENCES royalty_contracts(id) ON DELETE CASCADE,
    rights_holder_id uuid NOT NULL REFERENCES rights_holders(id),
    percentage numeric(7,4) NOT NULL CHECK (percentage > 0 AND percentage <= 100),
    PRIMARY KEY (contract_id, rights_holder_id)
);
CREATE INDEX ix_royalty_shares_rights_holder_id ON royalty_shares (rights_holder_id);

CREATE TABLE usage_imports (
    id uuid PRIMARY KEY,
    sha256 varchar(64) NOT NULL UNIQUE CHECK (sha256 ~ '^[0-9a-f]{64}$'),
    status varchar(10) NOT NULL CHECK (status IN ('Pending','Processing','Completed','Failed')),
    total bigint NOT NULL DEFAULT 0,
    inserted bigint NOT NULL DEFAULT 0,
    duplicate bigint NOT NULL DEFAULT 0,
    invalid bigint NOT NULL DEFAULT 0,
    created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    started_at timestamptz,
    finished_at timestamptz,
    duration_ms bigint,
    CONSTRAINT ck_import_statistics CHECK (total >= 0 AND inserted >= 0 AND duplicate >= 0
        AND invalid >= 0 AND total = inserted + duplicate + invalid),
    CONSTRAINT ck_import_timing CHECK (
        (status = 'Pending' AND started_at IS NULL AND finished_at IS NULL AND duration_ms IS NULL)
        OR (status = 'Processing' AND started_at IS NOT NULL AND finished_at IS NULL AND duration_ms IS NULL)
        OR (status IN ('Completed', 'Failed') AND started_at IS NOT NULL AND finished_at IS NOT NULL
            AND duration_ms IS NOT NULL AND duration_ms >= 0))
);
CREATE INDEX ix_usage_imports_created_id ON usage_imports (created_at DESC, id DESC);

CREATE TABLE usage_records (
    event_id uuid PRIMARY KEY,
    track_id uuid NOT NULL REFERENCES tracks(id),
    import_id uuid NOT NULL REFERENCES usage_imports(id),
    platform varchar(64) NOT NULL CHECK (btrim(platform) <> ''),
    country varchar(2) NOT NULL CHECK (country ~ '^[A-Z]{2}$'),
    usage_date date NOT NULL,
    play_count bigint NOT NULL CHECK (play_count BETWEEN 0 AND 1000000000)
);
CREATE INDEX ix_usage_records_date_event ON usage_records (usage_date DESC, event_id DESC);
CREATE INDEX ix_usage_records_track_date_event ON usage_records (track_id, usage_date DESC, event_id DESC);
CREATE INDEX ix_usage_records_import_event ON usage_records (import_id, event_id);

CREATE TABLE royalty_accruals (
    event_id uuid NOT NULL REFERENCES usage_records(event_id),
    contract_id uuid NOT NULL,
    rights_holder_id uuid NOT NULL,
    amount numeric(19,2) NOT NULL CHECK (amount >= 0 AND amount <= 92233720368547758.07),
    created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    PRIMARY KEY (event_id, contract_id, rights_holder_id),
    FOREIGN KEY (contract_id, rights_holder_id) REFERENCES royalty_shares(contract_id, rights_holder_id)
);
CREATE INDEX ix_royalty_accruals_contract_holder ON royalty_accruals (contract_id, rights_holder_id);
