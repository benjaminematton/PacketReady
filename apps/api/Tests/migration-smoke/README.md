# migration-smoke

SQL fixtures that exercise invariants the EF model + xUnit tests can't
reach: trigger behavior, multi-column unique constraints with NULL
semantics, and cross-field CHECK constraints. Run after applying a
migration; one file per migration.

## Why not xUnit?

The backend has no DB-bound integration test harness. Wiring one for
~4 assertions per migration would carry more rope than it saves.
A `.sql` file is read-once, debuggable in `psql`, and survives EF
version churn.

## Running

```
psql "$DATABASE_URL" -v ON_ERROR_STOP=1 -f AddDocumentStore.sql
```

A passing run prints `OK 1`, `OK 2`, `OK 3`, `OK 4`. Any other output
on stderr or any non-zero exit code means a regression — investigate
before unblocking the slice this migration belongs to.

Every block is wrapped in `BEGIN; … ROLLBACK;` so the script leaves no
trace; safe to run against a live dev DB if you have one handy.
