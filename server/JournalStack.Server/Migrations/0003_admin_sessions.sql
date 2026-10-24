CREATE TABLE admin_sessions (
    token_hash bytea PRIMARY KEY,
    created_at timestamptz NOT NULL DEFAULT now(),
    expires_at timestamptz NOT NULL
);
