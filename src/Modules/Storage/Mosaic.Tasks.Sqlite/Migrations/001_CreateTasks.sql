CREATE TABLE tasks (
  id TEXT PRIMARY KEY NOT NULL,
  title TEXT NOT NULL,
  notes TEXT NOT NULL,
  completed INTEGER NOT NULL CHECK (completed IN (0, 1)),
  created_at TEXT NOT NULL,
  updated_at TEXT NOT NULL,
  revision INTEGER NOT NULL CHECK (revision >= 1)
);
CREATE INDEX tasks_created_idx ON tasks(created_at DESC, id);
CREATE INDEX tasks_status_created_idx ON tasks(completed, created_at DESC, id);
