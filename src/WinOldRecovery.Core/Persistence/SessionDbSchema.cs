namespace WinOldRecovery.Core.Persistence;

internal static class SessionDbSchema
{
    public const int CurrentVersion = 3;

    public static IReadOnlyList<SchemaMigration> Migrations { get; } =
    [
        new(
            1,
            """
            CREATE TABLE schema_migrations (
                version INTEGER PRIMARY KEY,
                applied_at_utc TEXT NOT NULL
            ) STRICT;

            CREATE TABLE sessions (
                id TEXT PRIMARY KEY,
                started_at_utc TEXT NOT NULL,
                source_root TEXT,
                status TEXT NOT NULL,
                app_version TEXT NOT NULL,
                options_json TEXT NOT NULL DEFAULT '{}'
            ) STRICT;

            CREATE TABLE profiles (
                id INTEGER PRIMARY KEY,
                session_id TEXT NOT NULL REFERENCES sessions(id) ON DELETE CASCADE,
                name TEXT NOT NULL,
                source_path TEXT NOT NULL,
                kind TEXT NOT NULL,
                last_used_utc TEXT
            ) STRICT;

            CREATE TABLE nodes (
                id INTEGER PRIMARY KEY,
                session_id TEXT NOT NULL REFERENCES sessions(id) ON DELETE CASCADE,
                profile_id INTEGER REFERENCES profiles(id) ON DELETE CASCADE,
                parent_id INTEGER REFERENCES nodes(id) ON DELETE CASCADE,
                name TEXT NOT NULL,
                rel_path TEXT NOT NULL,
                kind TEXT NOT NULL,
                size INTEGER NOT NULL DEFAULT 0 CHECK (size >= 0),
                agg_size INTEGER NOT NULL DEFAULT 0 CHECK (agg_size >= 0),
                agg_files INTEGER NOT NULL DEFAULT 0 CHECK (agg_files >= 0),
                mtime_utc TEXT,
                attributes INTEGER NOT NULL DEFAULT 0,
                problem TEXT NOT NULL DEFAULT 'None',
                sensitive INTEGER NOT NULL DEFAULT 0 CHECK (sensitive IN (0, 1)),
                eff_decision TEXT NOT NULL DEFAULT 'Undecided'
            ) STRICT;

            CREATE TABLE badges (
                node_id INTEGER NOT NULL REFERENCES nodes(id) ON DELETE CASCADE,
                kind TEXT NOT NULL,
                detail TEXT NOT NULL,
                PRIMARY KEY (node_id, kind, detail)
            ) STRICT;

            CREATE TABLE decisions (
                node_id INTEGER PRIMARY KEY REFERENCES nodes(id) ON DELETE CASCADE,
                decision TEXT NOT NULL,
                source TEXT NOT NULL,
                decided_at_utc TEXT NOT NULL
            ) STRICT;

            CREATE TABLE cards (
                id INTEGER PRIMARY KEY,
                session_id TEXT NOT NULL REFERENCES sessions(id) ON DELETE CASCADE,
                recipe_id TEXT NOT NULL,
                profile_id INTEGER REFERENCES profiles(id) ON DELETE CASCADE,
                title TEXT NOT NULL,
                json TEXT NOT NULL
            ) STRICT;

            CREATE TABLE components (
                card_id INTEGER NOT NULL REFERENCES cards(id) ON DELETE CASCADE,
                key TEXT NOT NULL,
                decision TEXT NOT NULL,
                fixed INTEGER NOT NULL DEFAULT 0 CHECK (fixed IN (0, 1)),
                fixed_reason TEXT,
                PRIMARY KEY (card_id, key)
            ) STRICT;

            CREATE TABLE plan_items (
                id INTEGER PRIMARY KEY,
                session_id TEXT NOT NULL REFERENCES sessions(id) ON DELETE CASCADE,
                job_id INTEGER NOT NULL,
                operation TEXT NOT NULL,
                source_path TEXT NOT NULL,
                destination_path TEXT NOT NULL,
                bytes INTEGER NOT NULL DEFAULT 0 CHECK (bytes >= 0),
                conflict_policy TEXT NOT NULL,
                overwrite_approved INTEGER NOT NULL DEFAULT 0
                    CHECK (overwrite_approved IN (0, 1)),
                recipe_id TEXT
            ) STRICT;

            CREATE TABLE journal (
                id INTEGER PRIMARY KEY,
                plan_item_id INTEGER NOT NULL REFERENCES plan_items(id) ON DELETE CASCADE,
                state TEXT NOT NULL,
                recorded_at_utc TEXT NOT NULL,
                error TEXT
            ) STRICT;

            CREATE TABLE verify_results (
                id INTEGER PRIMARY KEY,
                plan_item_id INTEGER NOT NULL REFERENCES plan_items(id) ON DELETE CASCADE,
                report_id TEXT NOT NULL,
                level INTEGER NOT NULL CHECK (level BETWEEN 0 AND 3),
                ok INTEGER NOT NULL CHECK (ok IN (0, 1)),
                detail TEXT NOT NULL,
                recorded_at_utc TEXT NOT NULL
            ) STRICT;

            CREATE TABLE kv (
                session_id TEXT NOT NULL REFERENCES sessions(id) ON DELETE CASCADE,
                key TEXT NOT NULL,
                value TEXT NOT NULL,
                PRIMARY KEY (session_id, key)
            ) STRICT;

            CREATE INDEX ix_profiles_session ON profiles(session_id);
            CREATE INDEX ix_nodes_parent ON nodes(session_id, parent_id, name);
            CREATE INDEX ix_nodes_profile_path ON nodes(profile_id, rel_path);
            CREATE INDEX ix_nodes_largest ON nodes(session_id, agg_size DESC);
            CREATE INDEX ix_nodes_recent ON nodes(session_id, mtime_utc DESC);
            CREATE INDEX ix_nodes_problem ON nodes(session_id, problem)
                WHERE problem <> 'None';
            CREATE INDEX ix_cards_session_recipe ON cards(session_id, recipe_id);
            CREATE INDEX ix_plan_items_job ON plan_items(session_id, job_id, id);
            CREATE INDEX ix_journal_item_latest
                ON journal(plan_item_id, recorded_at_utc DESC, id DESC);
            CREATE INDEX ix_verify_report ON verify_results(report_id, plan_item_id);
            """),
        new(
            2,
            """
            CREATE TABLE decisions_v2 (
                node_id INTEGER NOT NULL REFERENCES nodes(id) ON DELETE CASCADE,
                source TEXT NOT NULL,
                decision TEXT NOT NULL,
                decided_at_utc TEXT NOT NULL,
                PRIMARY KEY (node_id, source)
            ) STRICT;

            INSERT INTO decisions_v2(node_id, source, decision, decided_at_utc)
            SELECT node_id, source, decision, decided_at_utc FROM decisions;

            DROP TABLE decisions;
            ALTER TABLE decisions_v2 RENAME TO decisions;
            """),
        new(
            3,
            """
            DROP INDEX ix_nodes_parent;
            CREATE INDEX ix_nodes_parent ON nodes(session_id, parent_id, name COLLATE NOCASE);
            """),
    ];
}

internal sealed record SchemaMigration(int Version, string Sql);
