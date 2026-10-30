# Runbook

What to do, step by step, for each job that keeps the stand running. Commands are for PowerShell on Windows, run
from the repo root. Docker Desktop must be running for every command that uses `docker`.

Local parts are proven and carry real numbers from [PROGRESS.md](PROGRESS.md). Anything on Railway has not been
run yet. Those places are marked **FILL AFTER DEPLOY** and get filled in from the first real run, never from a
guess.

Settings the server reads (names only, values are never in the repo, see `.env.example`):

| Name | What it is |
|---|---|
| `PGHOST`, `PGPORT`, `PGUSER`, `PGPASSWORD`, `PGDATABASE` | Database connection. The server stops at startup if one is missing (`Database.cs`). |
| `ADMIN_PASSWORD_HASH` | Hash of the admin password. Without it the server runs and admin sign-in is off (`Program.cs`). |

## Contents

1. [Deploy](#1-deploy)
2. [Roll back](#2-roll-back)
3. [Add a field (the live change)](#3-add-a-field-the-live-change)
4. [Back up](#4-back-up)
5. [Restore](#5-restore)
6. [Reset the demo](#6-reset-the-demo)
7. [Where the logs are](#7-where-the-logs-are)
8. [When it is down](#8-when-it-is-down)
9. [Rotate secrets](#9-rotate-secrets)

## 1. Deploy

The order is always the same: the migrations run first, and the new server starts only if they succeeded
(D-004). The server never changes the database on startup.

### Local

```powershell
docker compose up --build
```

Compose runs `db`, then `migrate` (`dotnet JournalStack.Server.dll migrate`), then `server`. If `migrate` exits
with an error, `server` does not start. Then check it:

```powershell
powershell -File scripts/smoke.ps1 -BaseUrl http://127.0.0.1:8080
```

The last line must be `PASS`.

### Railway

What is decided: one Railway service built from `server/Dockerfile`, one Railway PostgreSQL, the pre-deploy
command `dotnet JournalStack.Server.dll migrate`, and the six variables above set as Railway service variables
(D-003, D-004, D-006).

FILL AFTER DEPLOY: how a deploy is started (push to a branch, or `railway up`), which branch Railway watches, where
the pre-deploy command is set, the health check path, and how long a deploy takes.

After every deploy, run the smoke test against the hosted address:

```powershell
powershell -File scripts/smoke.ps1 -BaseUrl https://FILL-AFTER-DEPLOY
```

The smoke test leaves one device and one entry named "smoke test ..." in the database. There is no delete route.

The "Wait for CI" switch on Railway stays off until the first deploy (D-031). FILL AFTER DEPLOY: whether it was
turned on.

## 2. Roll back

A rollback puts the previous server version back. It does not undo migrations. That is safe here because
migrations only add tables and nullable columns (D-005), so the previous version still finds everything it reads.
The test `A_migration_that_only_adds_a_column_keeps_old_entries_and_the_old_request_shape_still_gets_201` in
`MigrationTests` shows old rows and the old request shape still work after a column is added.

### Local

Check out the previous commit and start again:

```powershell
git log --oneline -5
git checkout <previous-commit>
docker compose up --build
```

`migrate` then finds nothing new to run and the older server starts on the same database.

### Railway

FILL AFTER DEPLOY: where the previous deployment is redeployed in Railway, whether the pre-deploy step runs again
on a rollback, and how long it takes. This has not been rehearsed (see "Not done and why" in
[WHAT-CAN-GO-WRONG.md](WHAT-CAN-GO-WRONG.md)).

## 3. Add a field (the live change)

The example is `mood`, a number from 1 to 5 on an entry. It touches every layer. The work is done on a branch
`live-demo`, never on `main`.

The file number follows D-015: `0006_add_entry_mood.sql`, with 0005 kept for images. If images are cut, the file
becomes `0005_add_entry_mood.sql` and is renamed before its first Railway run.

1. **Migration.** Add `server/JournalStack.Server/Migrations/0006_add_entry_mood.sql`:

   ```sql
   ALTER TABLE entries ADD COLUMN mood smallint NULL CHECK (mood BETWEEN 1 AND 5);
   ```

   The column is optional. Old rows get an empty value. Nothing is removed.

2. **Server** (`server/JournalStack.Server/Entries.cs`). Read `mood` from the request. Accept it empty or 1 to 5,
   refuse anything else with 422. Add it to the INSERT, to `Columns` and to the answer. In `Hash`, append
   `"\nmood=N"` only when a mood was sent. The hash format is frozen and only grows at the end (D-016), so an old
   app's retry of an old entry still matches its stored row.

3. **Admin** (`server/JournalStack.Server/Pages/Admin/Entries.cshtml` and its `.cs` file). One more column.

4. **App** (`Assets/Scripts/Data/Dtos.cs` and the composer). One field in the request, buttons 1 to 5. An older app
   does not send it, and the server accepts that.

5. **Tests.** `MigrationTests` counts the embedded scripts, so a new script needs no change there. It does list the
   table names, so a migration that creates a table adds its name to that list. A column needs no change.

   ```powershell
   dotnet test server/JournalStack.Server.sln
   ```

6. **Run locally.**

   ```powershell
   docker compose up --build
   powershell -File scripts/smoke.ps1 -BaseUrl http://127.0.0.1:8080
   ```

   The smoke test sends entries without `mood`, so a `PASS` shows the old request shape still gets 201.

7. **Prove it.** An entry made before the change shows an empty mood in the admin. A new entry with mood 4 shows 4.

8. **Railway.** Merge `live-demo` into the branch Railway deploys. The pre-deploy migration runs, then the new
   server starts. FILL AFTER DEPLOY: where the deploy log shows the migration.

If the migration fails, the deploy stops and the old version keeps serving. This was rehearsed locally with a
broken script: `migrate` exited with code 1, the journal stayed at four scripts, no rows changed and the running
server kept answering ([PROGRESS.md](PROGRESS.md), "Bad migration, local rehearsal"). Fix the script and deploy
again. Never edit the `schemaversions` table by hand.

## 4. Back up

`scripts/backup.ps1` writes one dump file with `pg_dump -Fc`. It reads the PostgreSQL version from the database and
runs `pg_dump` from the `postgres:<same major>-alpine` image (D-030). It refuses to overwrite a file, and checks
that the dump is not empty and that `pg_restore --list` can read it. It never prints the password. `*.dump` files
are ignored by git.

### Local

```powershell
powershell -File scripts/backup.ps1 -OutFile backups/journalstack-local-2026-10-30.dump
```

With no `PG*` variables set, it dumps the local compose database and takes the password from `.env`.

Measured on 2026-10-29 with 78 entries, 9 devices, 1 tip: dump 15,503 bytes, `pg_dump` 0.9 s, the whole script
2.7 s. The first run also pulls `postgres:alpine` to read the version.

### Railway

The database has to be reachable from this computer for the length of the dump. The steps are: open access, dump,
close access.

1. Open access. FILL AFTER DEPLOY: where the TCP proxy is switched on in Railway, and what host and port it gives.
2. Set all five variables in this PowerShell window. With `PGHOST` set, the script needs the other four too and
   never falls back to the local database.

   ```powershell
   $env:PGHOST = "FILL-AFTER-DEPLOY"
   $env:PGPORT = "FILL-AFTER-DEPLOY"
   $env:PGUSER = "FILL-AFTER-DEPLOY"
   $env:PGDATABASE = "FILL-AFTER-DEPLOY"
   $env:PGPASSWORD = Read-Host "Railway database password"
   powershell -File scripts/backup.ps1 -OutFile backups/journalstack-railway-2026-10-30.dump
   ```

3. Close access. FILL AFTER DEPLOY: where the TCP proxy is switched off again.
4. Clear the variables so the next local command does not reach Railway:

   ```powershell
   Remove-Item env:PGHOST, env:PGPORT, env:PGUSER, env:PGDATABASE, env:PGPASSWORD
   ```

FILL AFTER DEPLOY: the PostgreSQL major version Railway runs (the script prints it), the dump size and time, and
whether Railway volume backups are on the plan.

There is no scheduled backup. The data lost in a disaster is everything since the last dump.

## 5. Restore

A restore loads a dump into an empty database. What it does to the app: a device registered after the dump is
unknown to the server and gets 401. The app then registers again and sends every entry it holds again. Entry ids
make that safe (D-025).

### The drill (proven)

```powershell
docker compose up -d
powershell -File scripts/restore-drill.ps1
```

It writes a known device and entry, dumps, writes a second device and entry, restores into a scratch PostgreSQL of
the same major (port 5541), and compares the table list, the row count of every table and the SHA-256 of all
`entries` rows in id order. Then it starts the server image on the restored copy (port 8082), runs the smoke test,
and checks the 401, the new registration and the resend. It removes its containers, volume and temp folder at the
end, also on failure. It leaves two devices and two entries named "drill ..." in the source.

Passing run on 2026-10-29: restore 0.8 s, whole drill 15.5 s, all checks equal, smoke `PASS`. The drill was also
made to fail on purpose (one row deleted, then one text changed) and stopped with exit code 1 both times
([PROGRESS.md](PROGRESS.md), "Backup and restore drill (local)").

### Local database from a dump

These are the drill's restore steps pointed at the compose database. The drill runs them against a scratch
database; this exact sequence on the compose database has not been run.

**This deletes the local database first.**

```powershell
docker compose down -v
docker compose up -d db
$env:PGPASSWORD = Read-Host "PGPASSWORD from .env"
docker run --rm -e PGPASSWORD -v "${PWD}\backups:/out" postgres:16-alpine pg_restore --exit-on-error --no-owner --no-privileges -h host.docker.internal -p 5440 -U journal -d journalstack /out/journalstack-local-2026-10-30.dump
Remove-Item env:PGPASSWORD
docker compose up --build
powershell -File scripts/smoke.ps1 -BaseUrl http://127.0.0.1:8080
```

The dump carries the migration journal, so `migrate` finds nothing new to run.

### Railway

FILL AFTER DEPLOY: how an empty Railway database is made or emptied, the restore command through the TCP proxy, and
the time. Not run yet. A restore from a dump of a different PostgreSQL major has not been tested.

## 6. Reset the demo

Never drop a column or edit `schemaversions` by hand. A dropped column would leave its migration marked as done,
and the next run would skip it.

### Local

**This deletes all local data.**

```powershell
docker compose down -v
docker compose up --build
```

The migrations build the empty database again. There is no seed script; demo data is typed in through the app and
the admin pages.

The app: stop Play, then delete its file. It holds the device token and every local entry (D-023).

```powershell
Remove-Item "$env:USERPROFILE\AppData\LocalLow\DefaultCompany\JournalStack\state.json"
```

On next start the app registers as a new device.

### Railway

Restore the dump taken before the demo (section 5). FILL AFTER DEPLOY: the steps and the time.

## 7. Where the logs are

### Local

```powershell
docker compose logs server
docker compose logs migrate
docker compose logs -f server
```

What the server writes: on an unhandled error, the exception type and a request id. The same request id is in the
error answer the caller got. It never writes entry text, tokens, cookies or passwords. Tests:
`Entry_text_appears_in_no_log_line_and_in_no_refusal` (`EntryTests`),
`An_unhandled_exception_logs_its_type_and_a_request_id_but_not_its_message` and
`A_failed_constraint_leaves_no_row_data_in_the_response_or_the_log` (`FailureTests`),
`Neither_the_password_nor_the_token_nor_the_hash_nor_the_cookie_reach_the_log` (`AdminAuthTests`).

Lines that are expected and harmless:

- `ADMIN_PASSWORD_HASH is not set. Admin login is disabled.` when the hash is empty.
- Two Data Protection warnings at startup: keys are kept in a folder inside the container and are not encrypted.
  No admin request depends on those keys ([PROGRESS.md](PROGRESS.md), "Startup log with Razor Pages").
- `Cannot load library libgssapi_krb5.so.2` from `migrate` on every run. It does not change the exit code.

### Railway

FILL AFTER DEPLOY: where the deploy log, the pre-deploy log and the running service log are, and how to read them
with the Railway CLI.

## 8. When it is down

Two addresses tell the two halves apart:

```powershell
curl.exe -i http://127.0.0.1:8080/health
curl.exe -i http://127.0.0.1:8080/health/ready
```

| `/health` | `/health/ready` | Meaning | Next step |
|---|---|---|---|
| 200 | 200 | Server and database are up | Run the smoke test. Look at the logs for the failing request id. |
| 200 | 503 | Server is up, database is not reachable | Database first (below). Device and admin calls answer 503 meanwhile. |
| no answer | no answer | The server process is not running or not reachable | Logs, then start it again. |

Tests for the 503 behaviour: `FailureTests`, for example
`When_the_database_is_down_health_stays_up_and_ready_says_503` and
`When_the_database_is_down_an_entry_post_answers_503_and_never_2xx`.

### Local

```powershell
docker compose ps
docker compose logs --tail 50 server
docker compose up -d
```

### While it is down

Nothing typed on the phone is lost. Entries stay Pending in the app's file and go out when the server answers again,
each exactly once (Play checks P4 to P7 in [PROGRESS.md](PROGRESS.md)). The app polls `/health` only, so it can show
Online while every post gets 503. The waiting entries in the status line show it (D-028).

### Railway

FILL AFTER DEPLOY: where the service status is, how to restart the service, how to check the database service, and
what the app sees during a restart.

## 9. Rotate secrets

### Admin password

1. Make a new hash. Type the new password (12 characters or more) when the command waits, then press Enter.

   ```powershell
   dotnet run --project server/JournalStack.Server -- hash-password
   ```

2. Local: put the printed line into `.env` as `ADMIN_PASSWORD_HASH` and run `docker compose up -d`.
   Railway: FILL AFTER DEPLOY (set the service variable, how the service restarts).

A new hash ends every admin session at once, because each stored session is a keyed hash of the token with the
password hash as the key (D-020). Test: `A_new_password_hash_ends_every_old_session` (`AdminAuthTests`).

### Database password

FILL AFTER DEPLOY: how the Railway database password is changed and how the server service gets the new value.

### A device token

There is no route to cancel one token. `DELETE /api/v1/devices/me` is planned and not built (D-014). Deleting the
device row by hand also deletes all of its entries (test `Deleting_a_device_deletes_its_entries` in
`MigrationTests`).

### A secret that reached git

Anyone who can read the repo can read its history, so the secret is treated as known. Rotate it as above first, then remove it from the code. The
`secrets` job in CI scans the whole history with gitleaks on every push to `main` and every pull request (D-031).
