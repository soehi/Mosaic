CREATE TABLE task_creation_receipts (
    source_id TEXT NOT NULL,
    request_key TEXT NOT NULL,
    task_id TEXT NOT NULL,
    payload_fingerprint TEXT NOT NULL,
    created_at TEXT NOT NULL,
    PRIMARY KEY (source_id, request_key)
);
-- Deliberately no foreign key: a user's deletion must not erase the creation fact.
