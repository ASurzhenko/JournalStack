# Runbook

What to do, step by step, for each job that keeps the stand running. Commands are for PowerShell on Windows, run
from the repo root. Docker Desktop must be running for every command that uses `docker`.

Local parts are proven and carry real numbers from [PROGRESS.md](PROGRESS.md). The Railway parts below were written
from the first deploys (10 to 14 November 2026), never from a guess. Steps that were not run on Railway say so.
Hosted addresses are not written in the repo; take them from the service's Settings, Networking.

Settings the server and the admin app read (names only, values are never in the repo, see `.env.example`):

| Name | What it is |
|---|---|
| `PGHOST`, `PGPORT`, `PGUSER`, `PGPASSWORD`, `PGDATABASE` | Database connection. The server stops at startup if one is missing (`Database.cs`). |
| `ADMIN_PASSWORD_HASH` | Hash of the admin password. Server only. Without it the server runs and admin sign-in is off (`Program.cs`). |
| `ADMIN_API_KEY` | The service key. The same value on the server and on the admin app, 32 characters or more. Without a good key the server refuses every admin call, and the admin app does not start. |
| `ADMIN_API_URL` | Admin app only. Where the server's admin API is. Locally `http://server:8080`, set in `docker-compose.yml`. |

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

This keeps the window busy and shows the server log. Run the smoke test below in a second PowerShell window.

Compose runs `db`, then `migrate` (`dotnet JournalStack.Server.dll migrate`), then `server`, then `admin`. If
`migrate` exits with an error, `server` does not start. The admin app is at `http://127.0.0.1:8090/admin`. Then check
the server:

```powershell
powershell -File scripts/smoke.ps1 -BaseUrl http://127.0.0.1:8080
```

The last line must be `PASS`.

### Railway

One Railway project with three services: `server` (built from `server/Dockerfile`), `admin` (built from
`admin/Dockerfile`) and `Postgres` (Railway's template, with a volume and no public address). The pre-deploy command
`dotnet JournalStack.Server.dll migrate` is on the server only. The variables above are Railway service variables
(D-003, D-004, D-006, D-033). The admin app gets no database variables and no password hash.

How it is set up, per service, in Settings:

- Source: this repo, branch `main`. Root directory `/server` for the server, `/admin` for the admin app.
- Build: builder **Dockerfile**. Railway's default builder is a different one, so this is chosen by hand.
- Deploy: pre-deploy command (server only), health check path `/health` (both).
- Networking: a generated public domain. The container listens on 8080.

Server variables: the five `PG*` names as references to the Postgres service (`${{Postgres.PGHOST}}` and so on),
`PORT=8080`, `ADMIN_PASSWORD_HASH`, `ADMIN_API_KEY`. Admin variables: `ADMIN_API_URL=http://server.railway.internal:8080`,
`ADMIN_API_KEY=${{server.ADMIN_API_KEY}}`, `PORT=8080`.

How a deploy starts: a push to `main`, or the Deploy button. The dashboard collects changes to settings and
variables as "Apply N changes", and nothing happens until Deploy is pressed. A deploy builds the image, runs the
pre-deploy command, starts the new container, waits for `/health` to answer 200, then moves traffic to it. The first
server deploy took 46 seconds from start to Active.

Two things learned on the first deploy:

- A reference to the server's private address, `${{server.RAILWAY_PRIVATE_DOMAIN}}`, showed the right value in the
  variable preview but reached the container unresolved, and the admin app refused to start. The literal private
  name works. Check what a deployment really got in that deployment's Details, Variables, not in the preview.
- A variable name with a space at the end is accepted without a warning, and the app then does not find it. Check
  names in the Raw Editor.

After every deploy, run the smoke test against the hosted server address:

```powershell
powershell -File scripts/smoke.ps1 -BaseUrl https://<server address>
```

The smoke test deletes its own device and entry at the end, so it leaves nothing behind. It passed against Railway
on the first deploy.

The "Wait for CI" switch on Railway makes a push deploy only after the CI checks pass (D-031). It was off for the
first deploys.

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

This keeps the window busy and shows the logs. Run any other command in a second PowerShell window.

`migrate` then finds nothing new to run and the older server starts on the same database.

### Railway

In the service's Deployments list, each deployment has a Redeploy action. A redeploy runs that deployment again with
the variables it had then, not the current ones.

A redeploy of the current server was run once: a health check every 2 seconds for about six minutes saw no answer
other than 200, and the admin stayed signed in. A rollback to an older deployment, and whether the pre-deploy step
runs again on it, have not been rehearsed (see "Not done and why" in [WHAT-CAN-GO-WRONG.md](WHAT-CAN-GO-WRONG.md)).

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

3. **Admin API** (`server/JournalStack.Server/AdminContent.cs`). `ListEntries` selects `mood`, and the `AdminEntry`
   record gets one more property, `short? Mood`. The JSON from `GET /admin/api/entries` then carries `mood`.

   **Admin app** (`admin/JournalStack.AdminApp/ApiClient.cs` and `Pages/Admin/Entries.cshtml`). Its own `AdminEntry`
   record gets `short? Mood`, and the page gets one more column. Either side can go live first. An old admin app
   ignores the new field. A new admin app against an old server reads it as empty. What to say on a call: the admin
   app only shows what the API returns. The API added a field, and the admin app added a column.

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

   The first line keeps the window busy and shows the server log. Run the second line, the smoke test, in a second
   PowerShell window.

   The smoke test sends entries without `mood`, so a `PASS` shows the old request shape still gets 201.

7. **Prove it.** An entry made before the change shows an empty mood in the admin. A new entry with mood 4 shows 4.

8. **Railway.** Merge `live-demo` into `main`. The pre-deploy migration runs, then the new server starts. The
   deploy log on Railway did not show a separate pre-deploy stage or the migration output on the first deploy, so
   check the result instead: the smoke test needs the tables, and an entry with a mood shows the new column works.

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

Not run on Railway yet. The plan: the database has no public address, so it has to be made reachable from this
computer for the length of the dump, through Railway's TCP proxy on the Postgres service, and closed again after.

1. Open access: switch on the TCP proxy for the Postgres service. It gives a host and a port.
2. Set all five variables in this PowerShell window. With `PGHOST` set, the script needs the other four too and
   never falls back to the local database.

   ```powershell
   $env:PGHOST = "<proxy host>"
   $env:PGPORT = "<proxy port>"
   $env:PGUSER = "<PGUSER from the Postgres service>"
   $env:PGDATABASE = "<PGDATABASE from the Postgres service>"
   $env:PGPASSWORD = Read-Host "Railway database password"
   powershell -File scripts/backup.ps1 -OutFile backups/journalstack-railway-<date>.dump
   ```

3. Close access: switch the TCP proxy off again.
4. Clear the variables so the next local command does not reach Railway:

   ```powershell
   Remove-Item env:PGHOST, env:PGPORT, env:PGUSER, env:PGDATABASE, env:PGPASSWORD
   ```

Not known yet: the PostgreSQL major version Railway runs (the script prints it on the first run), the dump size and
time, and whether Railway's own volume backups are on the plan in use (the Trial).

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

These are the drill's restore steps pointed at the compose database. Take a dump with section 4 first.

**This deletes the local database first.**

```powershell
docker compose down -v
docker compose up -d --wait db
$env:PGPASSWORD = Read-Host "PGPASSWORD from .env"
docker run --rm -e PGPASSWORD -v "${PWD}\backups:/out" postgres:16-alpine pg_restore --exit-on-error --no-owner --no-privileges -h host.docker.internal -p 5440 -U journal -d journalstack /out/journalstack-local-2026-10-30.dump
Remove-Item env:PGPASSWORD
docker compose up --build
powershell -File scripts/smoke.ps1 -BaseUrl http://127.0.0.1:8080
```

The `docker compose up --build` line keeps the window busy and shows the server log. Run the last line, the smoke
test, in a second PowerShell window.

The dump carries the migration journal, so `migrate` finds nothing new to run.

`--wait` matters. Without it `docker compose up -d db` returns while a new database is still setting itself up,
and `pg_restore` fails with "server closed the connection unexpectedly". Nothing is written in that case, so run the
`docker run ... pg_restore` line again.

Run on 2026-10-30 against the compose database (PostgreSQL 16.15, 84 entries, 15 devices, 1 tip, 4 journal rows):
dump 16,541 bytes, database healthy 3.1 s after `up`, restore 0.8 s. Table list, the row count of every table and
the SHA-256 of all `entries` rows in id order were equal before and after. `migrate` printed "No new scripts need to
be executed" and the smoke test passed.

### Railway

Not run on Railway yet. The plan is the local steps above pointed at the TCP proxy (section 4), into an empty
database. A restore from a dump of a different PostgreSQL major has not been tested.

## 6. Reset the demo

Never drop a column or edit `schemaversions` by hand. A dropped column would leave its migration marked as done,
and the next run would skip it.

### Local

**This deletes all local data.**

```powershell
docker compose down -v
docker compose up --build
```

This keeps the window busy and shows the server log. Run any other command in a second PowerShell window.

The migrations build the empty database again. There is no seed script; demo data is typed in through the app and
the admin pages.

The app: stop Play, then delete its file for this server. It holds the device token and every local entry (D-023).
The file is named after the server's host and port; for the local stack on port 8080:

```powershell
Remove-Item "$env:USERPROFILE\AppData\LocalLow\DefaultCompany\JournalStack\state-127.0.0.1-8080.json"
```

On next start the app registers as a new device.

### Railway

Restore the dump taken before the demo (section 5). Not run on Railway yet. Until then, demo data on Railway is
cleaned through the app: "Delete my data" removes the device it runs on and every entry of that device. Entries of
other devices stay; they are removed by deleting their device row (section 9, a device token). Tips are never
deleted, only set to draft (D-012).

## 7. Where the logs are

### Local

```powershell
docker compose logs server
docker compose logs admin
docker compose logs migrate
docker compose logs -f server
```

What the server writes: on an unhandled error, the exception type and a request id. The same request id is in the
error answer the caller got. It never writes entry text, tokens, cookies, passwords or the service key. The admin
app writes the same kind of line. When the server fails on a call from the admin app, the admin app's answer is a
502 that carries the server's request id, so one id finds the line in both logs. Tests:
`Entry_text_appears_in_no_log_line_and_in_no_refusal` (`EntryTests`),
`An_unhandled_exception_logs_its_type_and_a_request_id_but_not_its_message` and
`A_failed_constraint_leaves_no_row_data_in_the_response_or_the_log` (`FailureTests`),
`Neither_the_password_nor_the_token_nor_the_hash_nor_the_key_reach_the_server_log` (`AdminApiTests`),
`Neither_the_password_nor_the_token_nor_the_hash_nor_the_cookie_nor_the_key_reach_either_log` and
`N13_a_full_session_leaves_no_secret_in_the_admin_app_log` (`AdminAppTests`).

Lines that are expected and harmless:

- `ADMIN_PASSWORD_HASH is not set. Admin login is disabled.` when the hash is empty.
- `ADMIN_API_KEY is not set or is shorter than 32 characters. The admin API is disabled.` when the key is missing or
  short. The line names the variable and never its value.
- Two Data Protection warnings at startup, in the admin app's log only: keys are kept in a folder inside the container and are not encrypted.
  No admin request depends on those keys ([PROGRESS.md](PROGRESS.md), "Startup log with Razor Pages").
- `Cannot load library libgssapi_krb5.so.2` from `migrate` on every run. It does not change the exit code.

### Railway

Per service: Deployments, then a deployment, then Build Logs or Deploy Logs. Deploy Logs carry what the running
container writes, the same lines as locally. The first server deploy showed `Now listening on: http://[::]:8080`.

The pre-deploy step does not show as its own stage there, and the migration output did not appear in Deploy Logs.
That a migration ran is checked by its result: the smoke test needs the tables.

A deployment's Details lists the variables it really received. Read them there when a setting seems to be ignored.

The Railway CLI was not used; everything above is in the dashboard.

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

The admin app has its own `/health`. It answers 200 without calling the server, so an admin deploy never fails
because the server is down. Use the server's `/health/ready` to look at the database.

What the person at the admin sees, and what it means. Only the first row signs the person out.

| What is shown | Status | Meaning | Next step |
|---|---|---|---|
| The sign-in page | 302 | The session ended: it is 8 hours old, or the password hash changed | Sign in again |
| "The database is not reachable. Try again later." | 503 `database_unavailable` | The server is up and the database is not | Database first. The person stays signed in |
| "The server cannot be reached. Try again later." | 503 `api_unreachable` | The admin app cannot reach the server: it is stopped, or `ADMIN_API_URL` is wrong | `docker compose ps`, then the server log |
| "The server did not answer in time. A save may or may not have happened; check the list." | 504 `api_timeout` | The server took more than 10 seconds | Check the list before saving again |
| "Something went wrong on the server." with a request id | 502 `api_error` | The server failed on this call | Look for the request id in `docker compose logs server` |
| "The admin app is not set up to call the server." | 502 `admin_key_refused` | `ADMIN_API_KEY` differs between the admin app and the server | Section 9, service key |
| "The server has no admin key set." | 503 `admin_key_not_configured` | `ADMIN_API_KEY` is missing or shorter than 32 characters on the server | Set it on the server (section 9) |
| Sign-in page: "Admin login is not set up on this server." | 503 `admin_not_configured` | `ADMIN_PASSWORD_HASH` is missing on the server | Set it on the server (section 9) |

The two "not set up" answers name different variables: `admin_not_configured` is the password hash,
`admin_key_not_configured` is the service key.

Tests: `N8` to `N12`, `N16` and `N19` in `AdminAppTests`, and the two admin tests in `FailureTests`. The answers are
JSON for now, with the codes above.

### Local

```powershell
docker compose ps
docker compose logs --tail 50 server
docker compose up -d
```

### While it is down

Nothing typed on the phone is lost. Entries stay Pending in the app's file and go out when the server answers again,
each exactly once (Play checks P4 to P7 in [PROGRESS.md](PROGRESS.md)). The app polls `/health` only, so it can show
Online while every post gets 503. The waiting entries in the status line show it (D-028). While a delete waits for
the server, the app takes no new entries (D-032).

### Railway

The project page shows each service with its status. Then:

1. Check the two addresses above against the hosted server address.
2. Read the service's Deploy Logs (section 7).
3. Restart: Redeploy on the latest deployment (section 2). A server redeploy was measured once with no answer other
   than 200, so the app saw nothing.
4. The database: `/health/ready` answers 503 while it is not reachable. Look at the Postgres service's status and
   logs on the same page.

## 9. Rotate secrets

### Admin password

1. Make a new hash. Type the new password (12 characters or more) when the command waits, then press Enter.

   ```powershell
   dotnet run --project server/JournalStack.Server -- hash-password
   ```

   Type the password; do not pipe it into the command from Windows PowerShell 5.1. On this machine that put an
   invisible character (U+FEFF) in front of the password, the hash matched "U+FEFF plus password", and sign-in
   always said the password was wrong. Check a new hash before using it: sign in locally with it first.

2. Local: put the printed line into `.env` as `ADMIN_PASSWORD_HASH` and run `docker compose up -d`.
   Railway: on the `server` service, Variables, set `ADMIN_PASSWORD_HASH` to the new line, then Deploy. Only the
   server needs it.

A new hash ends every admin session at once, because each stored session is a keyed hash of the token with the
password hash as the key (D-020). Test: `A_new_password_hash_ends_every_old_session` (`AdminApiTests`).

### Service key

The key sits between the admin app and the server (D-034). Both need the same value, 32 characters or more.

1. Make a new key. The command prints it. Copy it straight into the place that needs it and clear the clipboard.

   ```powershell
   $bytes = New-Object byte[] 32; [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes); [Convert]::ToBase64String($bytes)
   ```

2. Local: put the value into `.env` as `ADMIN_API_KEY` and restart both services: `docker compose up -d`.
   Railway: set `ADMIN_API_KEY` on the `server` service and Deploy. The `admin` service reads it by reference
   (`${{server.ADMIN_API_KEY}}`); redeploy `admin` too rather than trusting that a changed reference redeploys it.
   Not rotated on Railway yet.

Sessions stay valid, because the key is not part of the session hash. Until both services have the new value, every
admin page answers 502 `admin_key_refused` or 503 `admin_key_not_configured`. A short key on the server stops the
admin API completely, and the server log says so by variable name.

### Database password

Not done on Railway yet. The server reads the password by reference to the Postgres service, so the value is
changed in one place, and the server is redeployed to pick it up. The exact steps on the Postgres service are not
checked.

### A device token

`DELETE /api/v1/devices/me` removes the calling device and its entries (D-014). There is still no way to cancel a
token without deleting its data.

A person's delete does not reach an older device row. That happens when the app lost its token and registered
again: the old row keeps the entries, and the app shows them as `Failed` with `id_taken` (D-025, D-032). Remove
the old device by hand. Deleting the device row also deletes all of its entries (test
`Deleting_a_device_deletes_its_entries` in `MigrationTests`). Find the owner of an entry, then delete that
device:

```powershell
docker compose exec db psql -U journal -d journalstack -c "SELECT device_id FROM entries WHERE id = '<entry id>'"
docker compose exec db psql -U journal -d journalstack -c "DELETE FROM devices WHERE id = '<device id>'"
```

On Railway: open access through the TCP proxy (section 4) and run the same two statements with `psql` against the
proxy host and port. Not run on Railway yet.

### A secret that reached git

Anyone who can read the repo can read its history, so the secret is treated as known. Rotate it as above first, then remove it from the code. The
`secrets` job in CI scans the whole history with gitleaks on every push to `main` and every pull request (D-031).
