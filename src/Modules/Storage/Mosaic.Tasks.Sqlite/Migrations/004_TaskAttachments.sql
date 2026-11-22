CREATE TABLE task_attachments (
  id TEXT PRIMARY KEY NOT NULL,
  task_id TEXT NOT NULL,
  file_name TEXT NOT NULL,
  stored_path TEXT NOT NULL,
  size_bytes INTEGER NOT NULL CHECK (size_bytes >= 0),
  added_at TEXT NOT NULL
);
CREATE INDEX ix_task_attachments_task ON task_attachments(task_id, added_at, id);
