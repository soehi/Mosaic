namespace Mosaic.Server.Data;

internal static class Schema
{
    /// <summary>
    /// Migration 1. Never edit once released: append a new migration instead.
    /// All timestamps are Unix seconds (UTC).
    /// </summary>
    public const string Migration1 = """
        CREATE TABLE IF NOT EXISTS users(
            id TEXT PRIMARY KEY,
            email TEXT NOT NULL UNIQUE COLLATE NOCASE,
            password_salt BLOB NOT NULL,
            password_hash BLOB NOT NULL,
            created_at INTEGER NOT NULL,
            email_verified_at INTEGER NOT NULL);

        CREATE TABLE IF NOT EXISTS sessions(
            id TEXT PRIMARY KEY,
            user_id TEXT NOT NULL REFERENCES users(id) ON DELETE CASCADE,
            token_hash TEXT NOT NULL UNIQUE,
            family_id TEXT NOT NULL DEFAULT '',
            created_at INTEGER NOT NULL,
            expires_at INTEGER NOT NULL,
            revoked_at INTEGER,
            device_label TEXT NOT NULL DEFAULT '');
        CREATE INDEX IF NOT EXISTS ix_sessions_user ON sessions(user_id);
        CREATE INDEX IF NOT EXISTS ix_sessions_family ON sessions(family_id);

        CREATE TABLE IF NOT EXISTS refresh_tokens(
            id TEXT PRIMARY KEY,
            user_id TEXT NOT NULL REFERENCES users(id) ON DELETE CASCADE,
            token_hash TEXT NOT NULL UNIQUE,
            family_id TEXT NOT NULL,
            created_at INTEGER NOT NULL,
            expires_at INTEGER NOT NULL,
            revoked_at INTEGER,
            replaced_by TEXT,
            device_label TEXT NOT NULL DEFAULT '');
        CREATE INDEX IF NOT EXISTS ix_refresh_family ON refresh_tokens(family_id);
        CREATE INDEX IF NOT EXISTS ix_refresh_user ON refresh_tokens(user_id);

        CREATE TABLE IF NOT EXISTS email_codes(
            id TEXT PRIMARY KEY,
            email_hash TEXT NOT NULL,
            purpose TEXT NOT NULL,
            code_hash TEXT NOT NULL,
            payload_json TEXT NOT NULL DEFAULT '{}',
            expires_at INTEGER NOT NULL,
            attempts INTEGER NOT NULL DEFAULT 0,
            consumed_at INTEGER,
            last_sent_at INTEGER NOT NULL,
            created_at INTEGER NOT NULL);
        CREATE INDEX IF NOT EXISTS ix_email_codes_lookup
            ON email_codes(email_hash, purpose, consumed_at);
        CREATE INDEX IF NOT EXISTS ix_email_codes_recent
            ON email_codes(email_hash, purpose, created_at);

        CREATE TABLE IF NOT EXISTS device_auth(
            id TEXT PRIMARY KEY,
            user_code_hash TEXT NOT NULL UNIQUE,
            device_code_hash TEXT NOT NULL UNIQUE,
            state TEXT NOT NULL DEFAULT 'pending',
            user_id TEXT REFERENCES users(id) ON DELETE CASCADE,
            session_id TEXT,
            secret_cipher TEXT,
            device_label TEXT NOT NULL DEFAULT '',
            created_at INTEGER NOT NULL,
            expires_at INTEGER NOT NULL,
            approved_at INTEGER,
            consumed_at INTEGER);
        CREATE INDEX IF NOT EXISTS ix_device_auth_state ON device_auth(state, expires_at);
        CREATE INDEX IF NOT EXISTS ix_device_auth_user ON device_auth(user_id);

        CREATE TABLE IF NOT EXISTS backups(
            id TEXT PRIMARY KEY,
            user_id TEXT NOT NULL REFERENCES users(id) ON DELETE CASCADE,
            device_id TEXT NOT NULL DEFAULT '',
            base_snapshot_id TEXT,
            content_sha256 TEXT NOT NULL,
            created_at INTEGER NOT NULL,
            size_bytes INTEGER NOT NULL,
            item_count INTEGER NOT NULL);
        CREATE INDEX IF NOT EXISTS ix_backups_user ON backups(user_id, created_at DESC);

        CREATE TABLE IF NOT EXISTS backup_items(
            id TEXT PRIMARY KEY,
            backup_id TEXT NOT NULL REFERENCES backups(id) ON DELETE CASCADE,
            kind TEXT NOT NULL,
            item_key TEXT NOT NULL,
            payload_json TEXT NOT NULL,
            revision INTEGER NOT NULL DEFAULT 0,
            UNIQUE(backup_id, kind, item_key));

        CREATE TABLE IF NOT EXISTS sync_items(
            user_id TEXT NOT NULL REFERENCES users(id) ON DELETE CASCADE,
            kind TEXT NOT NULL,
            item_key TEXT NOT NULL,
            server_revision INTEGER NOT NULL,
            deleted INTEGER NOT NULL DEFAULT 0,
            payload_json TEXT,
            updated_at INTEGER NOT NULL,
            PRIMARY KEY(user_id, kind, item_key));

        CREATE TABLE IF NOT EXISTS sync_counters(
            user_id TEXT PRIMARY KEY REFERENCES users(id) ON DELETE CASCADE,
            revision INTEGER NOT NULL);

        CREATE TABLE IF NOT EXISTS sync_changes(
            seq INTEGER PRIMARY KEY AUTOINCREMENT,
            user_id TEXT NOT NULL REFERENCES users(id) ON DELETE CASCADE,
            server_revision INTEGER NOT NULL,
            kind TEXT NOT NULL,
            item_key TEXT NOT NULL,
            deleted INTEGER NOT NULL DEFAULT 0,
            payload_json TEXT,
            device_id TEXT NOT NULL DEFAULT '',
            idempotency_key TEXT,
            created_at INTEGER NOT NULL);
        CREATE INDEX IF NOT EXISTS ix_sync_changes_user_seq ON sync_changes(user_id, seq);
        CREATE UNIQUE INDEX IF NOT EXISTS ux_sync_changes_idempotency
            ON sync_changes(user_id, idempotency_key) WHERE idempotency_key IS NOT NULL;

        CREATE TABLE IF NOT EXISTS sync_cursors(
            user_id TEXT NOT NULL REFERENCES users(id) ON DELETE CASCADE,
            device_id TEXT NOT NULL,
            cursor INTEGER NOT NULL,
            updated_at INTEGER NOT NULL,
            PRIMARY KEY(user_id, device_id));

        CREATE TABLE IF NOT EXISTS settings_audit(
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            user_id TEXT NOT NULL,
            action TEXT NOT NULL,
            created_at INTEGER NOT NULL);

        CREATE TABLE IF NOT EXISTS server_settings(
            key TEXT PRIMARY KEY,
            value TEXT NOT NULL,
            updated_at INTEGER NOT NULL);
        """;

    /// <summary>
    /// Adds per-device session metadata, device authorization details, backup
    /// baseline pointers and the sync history floor used to detect expired cursors.
    /// </summary>
    public const string Migration2 = """
        ALTER TABLE sessions ADD COLUMN platform TEXT NOT NULL DEFAULT '';
        ALTER TABLE sessions ADD COLUMN last_seen_at INTEGER NOT NULL DEFAULT 0;

        ALTER TABLE refresh_tokens ADD COLUMN platform TEXT NOT NULL DEFAULT '';
        ALTER TABLE refresh_tokens ADD COLUMN last_seen_at INTEGER NOT NULL DEFAULT 0;

        ALTER TABLE device_auth ADD COLUMN platform TEXT NOT NULL DEFAULT '';
        ALTER TABLE device_auth ADD COLUMN app_version TEXT NOT NULL DEFAULT '';
        ALTER TABLE device_auth ADD COLUMN last_polled_at INTEGER;

        ALTER TABLE backups ADD COLUMN server_revision INTEGER NOT NULL DEFAULT 0;
        ALTER TABLE backups ADD COLUMN sync_cursor INTEGER NOT NULL DEFAULT 0;

        CREATE TABLE IF NOT EXISTS sync_floor(
            user_id TEXT PRIMARY KEY REFERENCES users(id) ON DELETE CASCADE,
            floor_seq INTEGER NOT NULL,
            updated_at INTEGER NOT NULL);
        """;

    // Version 3 is reserved for the attachment migration on the desktop branch.
    public const string Migration4 = """
        CREATE TABLE mobile_pairings(
            reference_hash TEXT PRIMARY KEY,
            user_id TEXT NOT NULL REFERENCES users(id) ON DELETE CASCADE,
            family_id TEXT NOT NULL,
            expires_at INTEGER NOT NULL,
            device_id TEXT UNIQUE REFERENCES device_auth(id) ON DELETE CASCADE,
            short_code TEXT,
            cancelled_at INTEGER);
        CREATE INDEX ix_mobile_pairings_expires ON mobile_pairings(expires_at);
        """;
}
