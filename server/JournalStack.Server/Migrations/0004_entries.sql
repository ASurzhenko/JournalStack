CREATE TABLE entries (
    id uuid PRIMARY KEY,
    device_id uuid NOT NULL REFERENCES devices (id) ON DELETE CASCADE,
    text text NOT NULL CHECK (char_length(text) BETWEEN 1 AND 2000),
    client_created_at timestamptz NOT NULL,
    received_at timestamptz NOT NULL DEFAULT now(),
    request_hash bytea NOT NULL
);

CREATE INDEX entries_device_received ON entries (device_id, received_at DESC);
