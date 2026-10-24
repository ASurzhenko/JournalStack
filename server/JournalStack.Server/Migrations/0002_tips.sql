CREATE TABLE tips (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    text text NOT NULL CHECK (char_length(text) BETWEEN 1 AND 2000),
    status text NOT NULL CHECK (status IN ('draft', 'published')),
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now()
);
