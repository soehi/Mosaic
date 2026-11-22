DELETE FROM task_attachments WHERE task_id NOT IN (SELECT id FROM tasks);
CREATE TABLE task_attachments_new (
  id TEXT PRIMARY KEY NOT NULL,
  task_id TEXT NOT NULL REFERENCES tasks(id) ON DELETE CASCADE,
  file_name TEXT NOT NULL,
  stored_path TEXT NOT NULL,
  size_bytes INTEGER NOT NULL CHECK (size_bytes >= 0),
  added_at TEXT NOT NULL
);
INSERT INTO task_attachments_new SELECT id, task_id, file_name, stored_path, size_bytes, added_at FROM task_attachments;
DROP TABLE task_attachments;
ALTER TABLE task_attachments_new RENAME TO task_attachments;
CREATE INDEX ix_task_attachments_task ON task_attachments(task_id, added_at, id);
