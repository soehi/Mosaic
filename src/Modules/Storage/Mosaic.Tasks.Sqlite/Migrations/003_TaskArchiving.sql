ALTER TABLE tasks ADD COLUMN completed_at TEXT;
ALTER TABLE tasks ADD COLUMN archived_at TEXT;
ALTER TABLE tasks ADD COLUMN restored_at TEXT;
UPDATE tasks SET completed_at = updated_at WHERE completed = 1;

CREATE INDEX ix_tasks_archive_due ON tasks(MAX(completed_at, COALESCE(restored_at, completed_at)), id)
WHERE archived_at IS NULL AND completed = 1 AND completed_at IS NOT NULL;
