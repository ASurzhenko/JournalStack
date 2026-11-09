# JournalStack

JournalStack is a small journal app. I built it myself and I run it myself, from the phone to the database. You
write short notes in a Unity app and get a tip of the day. Every note goes to my server and gets saved in
PostgreSQL. There is also a separate admin app where I log in, add tips and read what people wrote.

I wanted to show the whole thing working, including the boring stuff that keeps it alive. Database changes that
don't break older versions of the app. Retries that don't make duplicates. Backups that I have actually restored.
And a way for a person to delete all their data. All the data in the demo is fake.

## The five parts

```
+--------------+   HTTPS, /api/v1/*   +---------------------------+         +------------+
| Unity app    | -------------------> | Server (one Docker image) | ------> | PostgreSQL |
| (phone or    |  entries go in,      |  - device API  /api/v1    |   SQL   | database   |
|  Editor)     |  tip comes back      |  - admin API   /admin/api |         |            |
+--------------+                      +---------------------------+         +------------+
                                                   ^
+--------------+   HTTPS, /admin       +-----------------+   HTTP, private    |
| Browser      | --------------------> | Admin app       | ------------------+
|              |  sign in, tips,       | (its own image) |  service key + session
+--------------+  entries              +-----------------+
```

- **Unity app** (`Assets/`). One screen: a status line, the tip card, a text box and the list of the person's
  entries. Each entry shows Pending, Synced or Failed. A Delete my data button removes the person's data from the
  server and from the phone.
- **Server** (`server/JournalStack.Server/`). One program. It answers the app on `/api/v1/...` and the admin app
  on `/admin/api/...`. It is the only thing that talks to the database.
- **Admin app** (`admin/JournalStack.AdminApp/`). A second program with three pages: sign in, tips, entries. The
  browser talks to the admin app. The admin app asks the server's admin API for everything and has no database
  connection (D-033 in [DECISIONS.md](DECISIONS.md)). It proves itself to the server with a service key (D-034).
- **Database** (PostgreSQL 16 locally). Tables: `devices`, `tips`, `entries`, `admin_sessions`, plus
  `schemaversions`, which records which migrations have run.

## One entry, there and back

1. The person types an entry in the app and taps Send. The app saves it to a file on the phone first
   (`state.json`) and shows it as **Pending**. If the app closes now, the entry is still there next time.
2. The app sends `POST /api/v1/entries` with the entry's id, its text and the time on the phone. It signs the
   request with its device token. The app got that token the first time it started, from `POST /api/v1/devices`.
3. The server checks the token, the size of the request, the id, the text (1 to 2000 characters, no control
   characters) and the time format. Anything wrong is refused with a short error code.
4. The server writes one row into `entries`. If the same entry arrives twice (for example because the answer was
   lost on the way), the id is already there, so the server answers with the stored row and writes nothing new.
5. The app gets a 201 or 200 answer and marks the entry **Synced**. If there was no answer at all, it stays Pending
   and the app tries again every 10 seconds. If the server refused it, it becomes **Failed** with the reason, and
   the person can tap Retry.
6. The admin opens `/admin/entries` in the admin app. The admin app asks the server's admin API for the newest 50
   entries and shows their text as plain text.

And a tip going the other way:

1. The admin writes a tip on `/admin`, sets it to `published` and saves. The admin app sends it to the server's
   admin API, and the server writes it to `tips`.
2. The app asks `GET /api/v1/tip/today` when the server comes online for it and when the person taps Refresh
   (`RefreshAll` in `Assets/Scripts/UI/JournalController.cs`). The server answers with the published tip that was
   saved last.
3. The app shows it on the tip card.

## Run it on your own computer

You need Docker Desktop (running) and, for the app, Unity `6000.3.5f2`. Commands are for PowerShell, from the
repo root.

1. Make the settings file. Only `PGPASSWORD` must be filled in.

   ```powershell
   Copy-Item .env.example .env
   notepad .env
   ```

2. Optional, for the admin app: make a password hash and put it in `.env` as `ADMIN_PASSWORD_HASH`. Run this,
   type a password of at least 12 characters, press Enter, and copy the line it prints. Without a hash the
   server still runs, but admin sign-in is off.

   ```powershell
   dotnet run --project server/JournalStack.Server -- hash-password
   ```

   Also make a service key and put it in `.env` as `ADMIN_API_KEY`. The server and the admin app must have the
   same key. Without it the admin app does not start and the server refuses every admin call. The device stack
   still runs.

   ```powershell
   $bytes = New-Object byte[] 32; [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes); [Convert]::ToBase64String($bytes)
   ```

3. Start the database, the migrations, the server and the admin app:

   ```powershell
   docker compose up --build
   ```

   Compose starts PostgreSQL, then runs the `migrate` step, then starts the server only if `migrate` succeeded,
   and the admin app after the server. This keeps the window busy and shows the logs. Open a second PowerShell window for step 4.

4. Check that it answers:

   ```powershell
   powershell -File scripts/smoke.ps1 -BaseUrl http://127.0.0.1:8080
   ```

   It prints one `ok` line per step and `PASS` at the end. It deletes its test device and entry at the end.

5. Admin app: open `http://127.0.0.1:8090/admin` in a browser and sign in with the password from step 2.

6. The app: open the repo folder in Unity, open `Assets/Scenes/Main.unity` and press Play. In the Editor the app
   talks to `http://127.0.0.1:8080` (set in `Assets/Config/ServerConfig.asset`).

If port 8080 is taken on your computer, a file `docker-compose.override.yml` (ignored by git) can publish the
server on another port. The admin app uses 8090. The app's Editor address then has to match.

Tests:

- Server: `dotnet test server/JournalStack.Server.sln`. The tests start their own PostgreSQL in Docker, so
  Docker must be running. The last full run passed 252 of 252 ([docs/PROGRESS.md](docs/PROGRESS.md)).
- App: Unity, Window > General > Test Runner > EditMode. 93 tests, all passing on the last run. They are run by hand
  before a client change is merged.

## What runs where

| Part | Local | Hosted |
|---|---|---|
| Server | Docker container from `server/Dockerfile`, port 8080 | Nothing is hosted yet. Planned: one Railway service built from the same Dockerfile (D-003) |
| Database | `postgres:16-alpine` container, port 5440 on this computer only | Planned: Railway PostgreSQL |
| Migrations | `migrate` step in `docker-compose.yml`, before the server | Planned: Railway pre-deploy command (D-004) |
| Admin app | Docker container from `admin/Dockerfile`, `http://127.0.0.1:8090/admin` | Nothing is hosted yet. Planned: a second Railway service, with a private address to the server |
| Unity app | Editor Play mode | A device build pointing at the hosted address |
| Tests and secret scan | `dotnet test`, gitleaks | GitHub Actions on every push to `main` and every pull request (D-031) |

FILL AFTER DEPLOY: the hosted server address, the Railway project and service names, the PostgreSQL major version
Railway runs, and the device address in `ServerConfig.asset`.

How to deploy, back up and restore: [docs/RUNBOOK.md](docs/RUNBOOK.md). What can fail and what is not done:
[docs/WHAT-CAN-GO-WRONG.md](docs/WHAT-CAN-GO-WRONG.md). Why things are the way they are:
[DECISIONS.md](DECISIONS.md).

## Words used in this repo

| Word | Meaning |
|---|---|
| API | The set of web addresses the app calls to send and fetch data. Here everything under `/api/v1`. |
| Server | The program that answers the app and the admin app and is the only thing that talks to the database. |
| Admin app | A second program with the admin pages. It asks the server's admin API for everything and has no database connection. |
| Admin API | The web addresses under `/admin/api` that only the admin app calls. Every call carries the service key. |
| Service key | A long random secret that the admin app and the server both hold. The server refuses admin calls without it. |
| Database, PostgreSQL | Where the data is kept. PostgreSQL is the database program. |
| Table, row, column | The database keeps data in tables. A row is one record, a column is one field of it. |
| SQL | The language used to read and write the database. The server's SQL is written by hand. |
| Migration | A numbered SQL file that changes the shape of the database, for example adds a column. They live in `server/JournalStack.Server/Migrations/` and run in order, once each. |
| Docker, container, image | Docker runs a program in a sealed box (a container) built from a recipe (an image). The same image runs locally and on the host. |
| docker compose | Starts several containers together from `docker-compose.yml`. Here: database, migrations, server, admin app. |
| Railway | The hosting service planned for the server and the database. |
| Deploy | Putting a new version of the server live. |
| Rollback | Going back to the previous version of the server. |
| Pre-deploy | A step the host runs before the new version starts. Here it runs the migrations. |
| Device, token | Each app install registers once as a device and gets a secret token. The server keeps only a hash of it. |
| Hash | A fixed-length fingerprint of some data. The original cannot be read back from it. |
| Idempotent | Sending the same request twice has the same effect as sending it once. Entries are, because each has its own id. |
| Pending, Synced, Failed | An entry's state in the app: not yet confirmed by the server, confirmed, or refused by the server. |
| 200, 201, 401, 409, 413, 422, 429, 503 | Answer codes. 2xx means it worked, 4xx means the request was refused, 5xx means a server or database problem. |
| ProblemDetails, code | The shape of an error answer. It carries a short fixed `code` such as `id_conflict`, which the app reads. |
| Session, cookie | After admin sign-in the server makes a random session token. The admin app keeps it in a browser cookie and sends it to the server with every call. The server keeps a hash of it in `admin_sessions` for 8 hours. |
| Backup, dump | A file copy of the whole database, made with `pg_dump`. |
| Restore | Loading a dump back into a database. |
| Smoke test | A short script that checks the main path works on a running server: `scripts/smoke.ps1`. |
| CI | Checks GitHub runs on every push: the server tests and a secret scan. |
| gitleaks, secret scan | A tool that looks through every commit for passwords and keys. |
| EditMode test | A Unity test that runs in the Editor without pressing Play. |
