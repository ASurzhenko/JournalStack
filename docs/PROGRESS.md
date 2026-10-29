# Progress

## Now

- Current slot: the minimal Unity client (slice B) is built on the branch `feat/unity-client`. Slots 0, 1, 2 slice A
  (entries as text with the idempotency core), 3 (admin panel) and 3b (the admin front as Razor Pages) are in `main`.
  The client has 68 EditMode tests, all passing, and was run in Play mode against the local stack (see "Unity
  client" below). The caret check on a device is not done yet.
- Done: slot 0, slot 1 (server, migrations 0001-0003, tests), slice A: migration 0004, `POST` and `GET`
  `/api/v1/entries`, entry cap, body cap, text checks, the tests and the mutation pass below. Slot 3: login, logout,
  session, tips and entries endpoints, the admin page, 54 new tests (169 in all) and the slot 3 mutation pass below.
  Slot 3b: the JSON admin API and the static page are replaced by three Razor Pages (sign in, tips, entries), the
  Origin check replaces the request-header check, 174 tests in all, and the slot 3b mutation pass below. The device
  API, the migrations and the data model did not change.
- Next step: the caret check on a phone, then the first Railway deploy. The images slice (0005) and
  `DELETE /devices/me` come later.
- Slot 3 was checked against a separate compose stack with curl and SQL: cookie flags, lock after five tries,
  session survives a restart, old session ends on a new hash, 503 and not 401 with the database down. Slot 3b was
  built with `docker compose build` (the Razor views compile in the image) and smoke-checked on a throwaway stack:
  the login page and the stylesheet answer 200, `/admin` without a session redirects to the login page, and a POST
  without an `Origin` header is refused with 403.
  Not yet checked in a real browser: entry text that looks like script shown literally, entries dated year 0001 and
  9999, a 2000-character entry, a double click on Save (two drafts are the expected result), a form on another
  origin posting to `/admin`.
- Slot 6, local half: backup script, timed restore drill, bad-migration rehearsal and two migration tests are
  done on the branch `chore/backup-restore-drill` (see "Backup and restore drill (local)" below). The Railway half
  (TCP proxy dump, rollback, volume backups) is not run.
- Open questions: the recruiter's answer about their Railway stack; the exact wording of the 2024 production claim;
  whether to keep the reserved migration number 0005 (D-015).

## Sessions

One line per session: date, focused minutes, slot, minutes to get started again.

| Date | Focused min | Slot | Start-up min |
|---|---|---|---|
| 2026-10-24 | | 0 | |
| 2026-10-24 | | 2 (slice A) | |
| 2026-10-24 | | 3 (admin panel) | |
| 2026-10-25 | | 3b (admin as Razor Pages) | |
| 2026-10-25 | | 5 (Unity client, minimal form) | |
| 2026-10-28 | | chore (server to .NET 10 LTS) | |

## Mutation pass, slice A

Each row: one edit to the code, the targeted test run, the result, then the edit undone.

| # | Edit | Test | Failed |
|---|---|---|---|
| 1 | check-then-insert instead of `ON CONFLICT DO NOTHING` | E6 held transaction, E7 burst | yes, E6 on the first run, E7 on the first attempt |
| 2 | no device comparison | E5 other device, identical request | yes |
| 3 | no hash comparison | E4 different content | yes (3 of 3 rows) |
| 4 | hash of the trimmed text | E4 outer-space row | yes |
| 5 | persist wrapped in a `catch` that answers 201 | E14 renamed table | yes |
| 6 | no control-character rule | E8 NUL row | yes |
| 7 | no 2000 limit | E8 2001 row (database `CHECK` answers 500) | yes |
| 8 | no empty-after-trim rule | E8 spaces row | yes |
| 9a | no `Content-Length` pre-check | E10 declared-length arm | yes |
| 9b | no counted read | E10 chunked arm | yes, after the test was rewritten (see below) |
| 10 | no content-type check | E9 | yes |
| 11 | no `EntryPosts.TryTake` | E13 cap | yes |
| 12 | no auth filter on the group | E15 | yes |

Mutation 9b first passed: the in-memory test server buffers every body and sets `Content-Length` itself, so the
chunked arm was a copy of the declared-length arm. The test was rewritten. A test-only header now removes
`Content-Length` before the endpoint runs, and the test asserts through a probe route that the server sees none.

Extreme client dates (E18): `0001-01-01T00:00:00Z` and `9999-12-31T23:59:59.9999999Z` are written without error
(the driver stores them as `-infinity` and `infinity`), so the fallback was not needed.

## Mutation pass, slot 3

Each row: one edit to the code, the test run of the admin tests, the result, then the edit undone.

| # | Edit | Test | Failed |
|---|---|---|---|
| 1 | no session filter on the inner group | every route except login and logout needs a session | yes |
| 2 | no `X-Requested-With` comparison | every write route needs the header | yes |
| 3 | no JSON content-type check | every write route needs the header (415 arm) | yes |
| 4 | `PasswordMatches` returns true | bad logins, rotation, rules tests | yes |
| 5 | `Secure = false` | cookie is secure only over https | yes |
| 6 | no `HttpOnly` | login sets a safe cookie | yes |
| 7 | `SameSite = Lax` | login sets a safe cookie | yes |
| 8 | no `AdminLogins.TryTake` | sixth login is locked | yes |
| 9a | `expires_at >= @now` | a session lasts exactly eight hours (boundary arm) | yes |
| 9b | no `expires_at` condition | a session lasts exactly eight hours | yes |
| 10 | constant key in `SessionHash` | new password hash ends every old session; keyed hash rule | yes |
| 11 | logout does not delete the row | logout ends the session for good | yes |
| 12 | cookie appended above the INSERT | session insert fails, no cookie at all | yes |
| 13 | logout DELETE in a `try/catch` that goes on | delete fails, logout does not pretend | yes |
| 14 | no `admin_not_configured` check | no hash configured | yes |
| 15 | no Content-Security-Policy header | admin page is served with a content policy | yes |
| 16 | `innerHTML` in `admin.js` | served files use no markup writing | yes |
| 17 | no `Cache-Control: no-store` | every admin API answer is not cached | yes |
| 18 | no `LIMIT 50` | entries list stops at 50 | yes |
| 19 | `request_hash` in the entries SELECT and record | exactly five properties | yes |
| 20 | no status check | bad tip is refused with its own code | yes |
| 21 | no `updated_at = now()` in the UPDATE | last write wins; device gets the tip saved last | yes |
| 22 | `AddCors` and `UseCors` allow-any | preflight gets no cross-site permission | yes |
| 23 | `CreateTip` write in a `try/catch` that answers 201 | tips table gone | yes |
| 24 | password in a log line in `Login` | bad logins, no secret reaches the log | yes |

All 25 cycles failed on the test named for them. A run of 6 (15, 20 to 24) was lost to a full system disk and a
stopped Docker engine, and was run again after both were fixed; mutation 12 was run twice for the same reason.

Slot 3b took about 2 to 2.5 h and was not in the 30 h estimate. The estimate file is unchanged.

Rows 1 to 3, 15 to 17 and 22 above test the JSON front that slot 3b removed. Their equivalents are in the next table.

## Startup log with Razor Pages

Read on a throwaway compose stack. With Razor Pages added, the server starts the Data Protection key ring at
startup and logs two warnings: keys are stored in a folder inside the container and are lost when the container is
destroyed, and the key is stored unencrypted. Nothing else changes and nothing fails. No admin request depends on a
key: the session is the stored session, the built-in antiforgery token is off, and the only cookie the server sets
is `js_admin` (a test pins this). The warnings will show in the Railway log too.

## Mutation pass, slot 3b

Each row: one edit to the code, the test run of the admin and failure tests, the result, then the edit undone.
Rows O1 to O14 re-run the slot 3 safeguards against the rewritten tests, N1 to N12 are new for the page front, and
X1 to X5 were added during the pass.

| # | Edit | Test | Failed |
|---|---|---|---|
| O1 | no token filter in the session query | every path needs a session; new hash ends old sessions | yes |
| O2 | `Secure = false` | cookie is secure only over https | yes |
| O3 | no `HttpOnly` | login sets a safe cookie | yes |
| O4 | `SameSite = Lax` | login sets a safe cookie | yes |
| O5 | no `AdminLogins.TryTake` | sixth login is locked; lockout before the form is read | yes |
| O6a | `expires_at >= @now` | a session lasts exactly eight hours | yes |
| O6b | no `expires_at` condition | a session lasts exactly eight hours | yes |
| O7 | constant key in `SessionHash` | new hash ends old sessions; keyed hash rule | yes |
| O8 | logout DELETE in a `try/catch` that goes on | delete fails, logout does not pretend | yes |
| O9 | cookie appended above the INSERT | session insert fails, no cookie at all | yes |
| O10 | `AddCors` allow-any | preflight gets no cross-site permission | yes |
| O11 | tip insert in a `try/catch` that goes on | tips table gone | yes |
| O12 | password in a log line | bad logins; no secret reaches the log | yes |
| O13 | no `LIMIT 50` | entries list stops at 50 | yes |
| O14 | no `updated_at = now()` | last write wins; device gets the tip saved last | yes |
| N1 | Origin check removed | every admin post needs an origin | yes |
| N2 | missing Origin accepted | every admin post needs an origin | yes |
| N3 | any present Origin accepted | every admin post needs an origin | yes |
| N4 | session check removed from the gate | every path needs a session; session lifetime; logout | yes |
| N5 | exempt list by `StartsWith` | near-miss paths such as `/admin/loginx` | yes |
| N6 | `Html.Raw` on the entry text | entry shown unchanged, script text encoded | yes |
| N7 | bad `id` becomes null, as a bound `Guid?` would | an id that is not a guid creates nothing | yes |
| N8 | no `no-store` | every admin answer is not cached | yes |
| N9 | no content policy | every admin answer carries the policy | yes |
| N10 | form value limit removed | oversize value refused (login and tips) | yes, after the tests were tightened (see below) |
| N11 | `Html.Raw` on the echoed editor text | refused save puts the text back encoded | yes |
| N12 | `OnGet` that writes | a get never writes anything | yes |
| X1 | form read before the lockout | lockout answers before the form is read | yes |
| X2 | no form content-type guard on login | bad login is a 401 | yes |
| X3 | no form content-type guard on tips | a post that is not a form stores nothing | yes |
| X4 | `PasswordMatches` always true | bad logins, rules tests, rotation | yes |
| X5 | no status check | bad tip is a 422 with its own message | yes |

N10 first survived: the oversize tests accepted any refusal, and a 17 KiB tip text is also refused by the text
rule (422), so they could not tell the form limit from the text rule. They now expect the exact 500 that only the
framework's refusal produces, and the mutation fails them.

Two framework facts found while testing. A form value that contains a NUL (`%00`) is refused by the form decoder
itself, before any page code runs, and ends as the same 500 as an oversize value. Nothing is stored and the value
is not shown back. Control characters other than NUL reach the text rule and give a 422.

## Unity client (slice B)

Checks that settled facts the design depended on, all made against the local stack in the Editor:

- The Editor sends plain HTTP to `http://127.0.0.1` when "Allow downloads over HTTP" is set to "Allowed in
  Development Builds". That option is `insecureHttpOption: 1` in `ProjectSettings.asset`.
- A refused connection and a request that hits the 10 second timeout (server paused) both come back as
  `ConnectionError`. A reply with a status of 400 or more comes back as `ProtocolError` with `responseCode` set.
- `JsonUtility` leaves a missing list field empty and not null, so the check for the `entries` key is what catches
  a wrong shape (the last test in `StateFileTests`).
- Setting `TMP_InputField.text` from code is not cut at `characterLimit`. Typing is: 2001 typed characters give
  2000. A pasted or scripted text of 2001 reaches the send step and is refused there with "Too long".
- Port 8080 on the development machine is used by the Unity MCP server, so `docker compose up` could not bind it.
  The Play checks ran with the server published on 8081 through a local override file that is not in the repo.
  The config asset keeps the compose default, 8080.

Play checks against the local stack (P numbers are from the slice plan):

| # | Result |
|---|---|
| P1 | Online, tip shown. A first start with no token registered one device by itself (tip 401, register 201, tip 200) |
| P2 | Tip changed in the database, Refresh showed the new text. The change was made with SQL, not in the admin page |
| P3 | Entry Pending, then Synced; one row in the database |
| P4 | Server stopped: row stays Pending, status `Offline · 1 waiting`, `state.json` holds the entry with state 0 |
| P5 | Play stopped and started again with the server still down: the Pending entry was still listed |
| P6 | Server started: status `Online`, entry Synced, exactly one row in the database |
| P7 | Server paused: the ping timed out as `no answer`; after unpause the entry was Synced, one row in the database |
| P8 | 70 Synced entries and a deleted device row: one registration, 60 entries sent, loop stopped at the cap, status `Online · 10 waiting`, none Failed, 60 rows in the database |
| P8b | Server restarted: the last 10 went out within a tick, 70 rows, status `Online` |
| P10 | A 2000 character entry opens in the detail view; scrolled to the end the last line (`...END`) is on screen (screenshot) |
| P11 | 2001 typed characters give 2000 and the counter reads `2000 / 2000` |
| P12 | Hand-edited text on a Failed row: Retry shows `Failed · id_conflict`; Refresh leaves it Failed |
| P13 | Garbage token with the old device row kept: one registration, three old entries `Failed · id_taken`, the new entry Synced |

Not run: the second 401 within 60 seconds of a registration (the throttle path has no test and no Play step), and
the caret check on a phone.

Safeguards, each seen failing once with the safeguard removed (EditMode run, then the edit undone):

| # | Edit | Test that failed |
|---|---|---|
| 1 | `Record` without the Synced-is-final rule | `SyncedIsTerminalForEveryOutcome` |
| 2 | `AddPending` without the save before it returns | `AddPendingIsOnDiskBeforeItReturns`, `AFailingSaveTakesTheEntryOutAgain`, `AnotherInstanceOnTheSameFileSeesTheEntry` |
| 3 | `Json.TryParse` without the required key check | `AWrongShapeIsNotAnEmptySuccess`, the `{}` case and `ASecondCorruptionMakesASecondCopy` in `StateFileTests` |
| 4 | `Merge` without the text comparison | `MergeWithDifferentTextMarksFailedAndNeverSynced` |

Two defects found by looking at the running screen and fixed. The status row and the composer were far taller than
set, because a layout element with no flexible height set takes the group's value of 1; both now set it to 0. The
list view destroyed old rows at the end of the frame, so the layout rebuild still counted them; rows are now hidden
before they are destroyed.

## Backup and restore drill (local)

Run on 2026-10-29 against the local compose stack (PostgreSQL 16.15). Nothing here touched Railway.

What exists now:

- `scripts/backup.ps1 -OutFile <path>` dumps the database with `pg_dump -Fc`. It reads `PGHOST`, `PGPORT`,
  `PGUSER`, `PGPASSWORD`, `PGDATABASE`, the same names as the server. With none set it uses the local compose
  database and the password in `.env`. It asks the database for `SHOW server_version` and runs `pg_dump` from the
  `postgres:<major>-alpine` image with that major. If the version cannot be read, it stops with an error. It
  refuses to overwrite a file, and it checks that the dump is not empty and that `pg_restore --list` can read it.
- `scripts/restore-drill.ps1` puts a device and an entry into the source, dumps, restores into a scratch
  PostgreSQL of the same major (own container, port 5541, own volume), and compares the two databases. It then
  starts the server image on the restored database (port 8082) and runs `scripts/smoke.ps1` against it. The scratch
  container, the volume, the server container and the temp folder are removed at the end, also when a step fails.
- `scripts/common.ps1` holds the small helpers both scripts use.

The drill compares three things between the source and the restored copy: the list of tables, the row count of
every table (including the DbUp journal), and the SHA-256 of `COPY (SELECT * FROM entries ORDER BY id) TO STDOUT`.

Real numbers from the passing run (source: 78 entries, 9 devices, 1 tip, 4 journal rows):

| What | Value |
|---|---|
| Dump size | 15,503 bytes |
| `pg_dump` alone | 0.9 s |
| `backup.ps1` as the drill ran it (includes reading the version) | 2.7 s |
| Restore into the scratch database | 0.8 s |
| Whole drill (dump, restore, compare, server start, smoke, 401 steps, cleanup) | 15.5 s |

The first run also pulled the `postgres:alpine` image that is used only to read the version. That is not in the
times above.

Result of the passing run:

| Check | Result |
|---|---|
| Table lists equal | yes |
| Row counts equal, every table | yes |
| Entries SHA-256 equal | yes (`8532db34...e50b`) |
| `smoke.ps1` on the server running on the restored database | PASS |
| Device made before the dump keeps its token and sees its entry | yes, 200 |
| Device made after the dump is unknown | 401 |
| That client registers again and sends its entry again, same id | 201, 201 |
| Scratch container, volume and server container gone afterwards | yes |

What a restore does to a client: devices created since the dump get 401. The client registers again and sends
every entry it has not confirmed again. The entry id makes the second send safe. The reset of the local entries to
Pending is covered by the Unity EditMode test `OutboxTests.ResetAllToPendingLiftsSyncedAndFailed`. The loop of
register and resend in the controller is not an EditMode test; it was seen in Play check P13. The drill shows the
server half of it.

Both directions. A gate that has only said yes is not shown to be a gate, so each comparison was made to fail on a
copy of the script (the copies are deleted):

| Edit in a copy of the drill, after the restore | Output | Exit code |
|---|---|---|
| Delete one row of `entries` in the restored copy | counts 80 against 79, hashes differ, `FAIL compare : row counts differ; entries hash differs` | 1 |
| Change the text of one entry, row count unchanged | `FAIL compare : entries hash differs` | 1 |

Both failed runs still removed the scratch container and volume. Two more refusals were run by hand: `backup.ps1`
with a wrong password stops with the PostgreSQL error and a non-zero exit code, writes no file and prints no
password; `backup.ps1` with a file name that exists is refused.

### Bad migration, local rehearsal

A file `0005_rehearsal_broken.sql` was added to `server/JournalStack.Server/Migrations/` (not committed, deleted
afterwards). It adds a column and then selects from a table that does not exist:

```
ALTER TABLE entries ADD COLUMN mood integer;
SELECT * FROM table_that_does_not_exist;
```

Commands, from the repo root, with the compose stack up:

```
docker compose build migrate
docker compose run --rm --no-deps migrate
```

Observed:

| Check | Before | After |
|---|---|---|
| `migrate` exit code | | 1 (`Migration failed with PostgresException.`, 42P01) |
| `schemaversions` rows and newest script | 4, `0004_entries.sql` | 4, `0004_entries.sql` |
| Rows in entries, devices, tips | 83, 14, 1 | 83, 14, 1 |
| Column `entries.mood` exists | no | no (the script runs in one transaction, so the first line was rolled back) |
| The running server container | up, old image | still up, 13 minutes, `smoke.ps1` PASS against it |

After deleting the file and rebuilding `migrate`, a run printed "No new scripts need to be executed" and exited 0.
The migrate container prints `Cannot load library libgssapi_krb5.so.2` on every run, also the clean one. It does
not change the exit code.

This is the local half of failure mode 6. On Railway the same command is the pre-deploy step; that part is not run.

### Migration tests

`MigrationTests` already had a test that a second run changes nothing. Two were missing and are added:

- `The_last_script_applied_on_top_of_older_data_leaves_every_row_as_it_was`: builds schema N (every script but the
  newest), seeds a device and a tip, applies the newest script through `Migrator.Run`, and compares every seeded
  row and the journal count.
- `A_migration_that_only_adds_a_column_keeps_old_entries_and_the_old_request_shape_still_gets_201`: stores an entry
  through the API, applies a test-owned script that adds a nullable column, then checks the old row is identical,
  the old request body still answers 200 as a retry, and a new entry in the old shape answers 201.

Each was made to fail once: a temporary newest script that rewrites `tips` failed the first; an additive script
that also rewrites `entries` failed the second on the row comparison; one that adds a check constraint that refuses
every insert failed the second on the 201 (it got 500). The edits were undone and the full server suite ran: 176
passed.

Found on the way: `Migrate_creates_the_four_tables_and_a_second_run_changes_nothing` asserts exactly 4 journal
rows, so it fails the day a fifth script is added. Fix it in that commit.

### Not run

- A dump through the Railway TCP proxy, and the steps to open and close it.
- A Railway rollback, and what Railway does with the pre-deploy step on a rollback.
- Railway volume backups.
- A restore of a dump taken from a database whose major version differs from the local one.
- The scripts on Linux or macOS. `host.docker.internal` is how a container reaches the local database on Docker
  Desktop for Windows; other hosts may need a different address.
- No scheduled backup exists. The loss window is the time since the last dump.
