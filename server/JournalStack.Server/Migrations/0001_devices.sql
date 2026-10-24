CREATE TABLE devices (
    id uuid PRIMARY KEY,
    token_hash bytea NOT NULL UNIQUE,
    created_at timestamptz NOT NULL DEFAULT now()
);
