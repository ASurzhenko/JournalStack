# Decisions

Each entry has four parts: Choice, Why, Cost, Revisit if. Status says whether the decision is already in the code.

## D-001 Stack: .NET 10, PostgreSQL, Dapper, DbUp

- Status: in use from slot 1.
- Choice: ASP.NET Core minimal API on .NET 10, PostgreSQL, Dapper with hand-written SQL, DbUp for migrations.
- Why: The app is Unity, so it's C#. Writing the server in C# too means one language for both sides. I write SQL by
  hand so I can read and explain every query. And PostgreSQL is the database I trust for this kind of thing.
- Cost: Dapper does not map or migrate anything for us. More SQL is typed by hand.
- Revisit if: The company's own server runs on a different stack and matching it becomes the goal.

## D-002 The people who review this are not developers of this stack

- Status: applies to every file.
- Choice: Keep the design small and plain. Nothing is added to look advanced.
- Why: Anyone who opens this should be able to follow it. I keep it small and simple enough that I can explain
  every part in normal words. Where I've left out something a bigger product would need, I say so.
- Cost: Some common production features are left out and listed under "not done".
- Revisit if: A technical reviewer joins the process.

## D-003 One Railway environment

- Status: planned for slot 4.
- Choice: One hosted environment. The safe place to try a change first is the local `docker compose` stack.
- Why: A second hosted copy costs money and this demo doesn't need it. I test every change locally first, with the
  same image and the same steps.
- Cost: A change is tried locally, not on a second hosted copy.
- Revisit if: The stand becomes a real product.

## D-004 Migrations run only as a separate step before the new version starts

- Status: compose in slot 0, Railway pre-deploy in slot 4.
- Choice: `dotnet JournalStack.Server.dll migrate` applies pending scripts and exits with an error code on
  failure. Compose runs it before the server. Railway runs it as the pre-deploy command. The server never
  migrates on startup.
- Why: If a migration fails I want the deploy to stop and the old version to keep working. Running migrations as a
  separate step before the server starts gives me that. If the server migrated itself on start, it could end up
  running on a half migrated database.
- Cost: If Railway's pre-deploy cannot be used on the chosen plan, this needs another answer.
- Revisit if: Slot 4 shows pre-deploy is not usable.

## D-005 Migrations only add

- Status: rule from slot 1.
- Choice: A release may add tables and nullable columns. Dropping or renaming waits for a later release.
- Why: If I roll back, I get the old code back but the database stays new. And people keep old versions of the app
  on their phones for a long time after I update the server. So a release can only add things, like new tables or
  optional columns. I remove something only when nothing old can still use it.
- Cost: Old columns stay for a while.
- Revisit if: No old client can still be in use.

## D-006 Connection by separate PG variables

- Status: in use from slot 1.
- Choice: `PGHOST`, `PGPORT`, `PGUSER`, `PGPASSWORD`, `PGDATABASE`. No `DATABASE_URL`.
- Why: These five variables are what Postgres tools already understand. The same names work on my machine and on
  the host, so the code is the same in both places.
- Cost: Five variables instead of one.
- Revisit if: The driver or the host changes.

## D-007 No antiforgery tokens in the admin

- Status: replaced by D-021.
- Choice: Cookie with `SameSite=Strict`, plus a required `Content-Type: application/json` and a fixed
  `X-Requested-With` header on every change.
- Why: One admin on one origin. A page on another site can send neither the header nor the content type.
- Cost: Less defence in depth than token-based protection.
- Revisit if: There is more than one admin, or the admin is served from another origin.

## D-008 A stored admin session instead of a framework cookie

- Status: in use from slot 3. Since D-033 the admin app holds the cookie and the server holds the row.
- Choice: Login creates a random token on the server. Only its hash is stored in `admin_sessions`. The admin app
  puts the token in a browser cookie and sends it to the server with every call.
- Why: The framework cookie has the same key problem as the form tokens in D-021. The keys disappear on every
  deploy, which would log me out each time. My session is stored in the database, so it survives a redeploy.
- Cost: One more table and about 30 lines of code.
- Revisit if: Keys are stored outside the container.

## D-009 No CORS

- Status: in use from slot 1.
- Choice: CORS is not configured.
- Why: The Unity app isn't a web page, and the browser only talks to the admin app. The admin app calls the server
  from server to server, so CORS never applies. No other site needs to call either one.
- Cost: A browser app on another origin could not call the API.
- Revisit if: A web client on another origin is added.

## D-010 No per-IP limits

- Status: from slot 1.
- Choice: One global cap on device registration (30 per minute), a per-device cap of 600 requests per hour on
  all device endpoints, a per-device cap of 60 entry posts per hour, and 5 admin login attempts a minute for the
  whole server. All live in memory and reset on deploy.
- Why: I don't know the addresses of the hosting proxy, and the forwarded address is easy to fake. So limits are
  per device and for the whole server, not per IP.
- Cost: One abuser can use the whole registration cap. Limits reset on every deploy and do not span replicas.
- Revisit if: There is more than one replica, or real traffic.

## D-011 Tips have no version, last write wins

- Status: in use from slot 3.
- Choice: No version column on tips.
- Why: There's one admin, so I don't need to handle two people editing the same tip at the same time.
- Cost: Two admins editing the same tip would overwrite each other.
- Revisit if: A second admin exists.

## D-012 Small admin

- Status: in use from slot 3.
- Choice: No statistics, no scheduled publishing, no paging beyond the newest 50 entries. No tip delete: a bad
  tip is set to draft. The tips list shows the newest 100.
- Why: The admin does what I need for this project: publish tips and read entries. I haven't needed stats or paging
  yet, so I haven't added them.
- Cost: The admin cannot browse older entries.
- Revisit if: The number of entries grows.

## D-013 Images are checked and refused, not changed

- Status: planned for slot 2, last in the build order.
- Choice: The server checks type, size, dimensions and metadata, and refuses what fails. It does not re-encode.
  Images are stored in the database.
- Why: Rejecting a bad file takes a few simple checks. Trying to fix it means more code and more things that can go
  wrong. This part isn't built yet anyway.
- Cost: Bytes added after the end of the image are not checked. A bucket would scale better.
- Revisit if: Image size or backup time grows.

## D-014 Device deletion by cascade

- Status: in use from slice D1. Route `DELETE /api/v1/devices/me`, tests in `DeviceTests`, app flow in D-032.
- Choice: `DELETE /api/v1/devices/me` removes the device and, by foreign key cascade, its entries and images.
- Why: If someone asks to delete their data, I want to do it in one query. I delete the device and the database
  removes all its entries together with it. It's one statement, so either everything is gone or nothing changed. No
  half deleted state.
- Cost: There is no undo.
- Revisit if: Soft delete or an audit trail is needed.

## D-015 Migration numbers

- Status: 0004 in this slice.
- Choice: 0004 `entries`, 0005 reserved for `entry_images`, 0006 `add_entry_mood` is the live change. The rule:
  the images slice lands in `main` before the live change is first applied on Railway.
- Why: The runbook and the live change refer to the migration file by name. A fixed number keeps those steps the
  same and stops two branches from grabbing the same number.
- Cost: A reserved gap. If images are cut, the live change takes 0005 and the file is renamed before its first
  Railway run.
- Revisit if: Images are cut, or the order of slices changes.

## D-016 The request hash covers the fields as received

- Status: in use from this slice.
- Choice: SHA-256 over the raw text, a newline, the raw date, a newline, then optional parts appended in later
  slices. The format is frozen and a test pins it to digests computed outside the code. The insert runs in one
  transaction with `ReadCommitted` written in the code, because the images slice puts the image row in the same
  transaction.
- Why: When a phone sends the same entry again, the server needs to know if it's the same request or a different
  one with the same id. It compares a hash of exactly what was sent. I keep the hash format unchanged so a retry
  still matches a row saved by an older version of the server.
- Cost: `" a"` and `"a"` are different requests for the same id. A client must resend the same strings. The
  transaction holds one statement in this slice.
- Revisit if: Never for stored rows. A new format needs a new column, not a change to this one.

## D-017 Entry limits and checks

- Status: in use from this slice.
- Choice: 60 posts per device per hour, a 16 KiB body, 2000 characters (UTF-16 units), and `clientCreatedAt`
  checked for format only.
- Why: I expect a person to write a few entries a day. If something sends hundreds, it's probably a bug or a
  script, and the limits stop it. The date from the phone is only for display, so if the phone clock is wrong
  nobody loses an entry because of it.
- Cost: A burst above 60 an hour is refused with a retryable 429. Retries count against the cap. There is no
  `Retry-After`, the windows are fixed, the cap resets on deploy, and there is no per-IP limit. A request padded
  with outer whitespace past 16 KiB is refused with 413. A date in year 0001 or 9999 is stored as sent (the driver
  keeps it as `-infinity` or `infinity`) and is read back without a `Z`. An entry that conflicts on insert and then
  cannot be found gives a 500 instead of a dedicated code.
- Revisit if: Real usage shows up, there is more than one replica, or the admin needs sane dates.

## D-018 Admin pages are Razor Pages in the admin app, with a content policy

- Status: in use from slot 3b. The pages moved to the admin app in D-033.
- Choice: The admin is three pages (sign in, tips, entries) rendered by the admin app. There is no JavaScript. The framework encodes every piece of text it writes. Every admin answer carries
  `Cache-Control: no-store` and the policy `default-src 'self'; frame-ancestors 'none'; form-action 'self'`.
- Why: Entry text comes from phones and I show it in the admin. The pages encode all text and the browser is told
  not to run inline scripts. If an entry looks like a script, it just shows up as text.
- Cost: The pages live in the admin app project. A page that needs inline script or a file from another site cannot
  be added without loosening the policy.
- Revisit if: The admin needs something a plain form cannot do.

## D-019 Admin login limit

- Status: in use from slot 3. Since D-033 the counter is on the server, and the admin app forwards every attempt.
- Choice: 5 login attempts a minute for the whole server. A right password counts as an attempt too. The server
  takes the count before it reads the body.
- Why: There's only one admin, me. Five tries per minute is plenty for me and slows down a script guessing
  passwords. The counter goes up before the server reads the body, so a huge request can't get around the limit.
  The admin app reads the form first, because it has to forward the password, and it checks no password itself.
  Its 16 KiB form limit is not the protection. The counter on the server is.
- Cost: Someone who keeps failing locks the real admin out until the minute is over. The count resets on deploy.
  Through the admin app a huge sixth form is a 500 from the form limit and not a 429. At the server a huge sixth
  body is still a 429.
- Revisit if: There is more than one admin or more than one replica.

## D-020 Admin sessions: keyed hash, 8 hours, no renewal, no cleanup

- Status: in use from slot 3.
- Choice: The session token is random. The table stores a keyed hash of it, and the key is the password hash. A
  session lasts 8 hours from login. It is not renewed, and expired rows are not deleted.
- Why: If I change the admin password, all open sessions end right away, without any extra code. Eight hours is
  enough for a work day.
- Cost: Typed text is lost if the session ends at the moment of Save. The table gets one row per login.
- Revisit if: Sessions have to be listed or ended one by one.

## D-021 No antiforgery tokens: SameSite=Strict plus an Origin check

- Status: in use from slot 3b. Replaces D-007. The check lives in the admin app since D-033.
- Choice: The session cookie is `SameSite=Strict`. Every admin POST must carry an `Origin` header whose host equals
  the admin app's own host, or it is refused with 403 before any page runs. A GET never changes data. The
  framework's own antiforgery token is switched off in one line in the admin app's `Program.cs`. The server's admin
  API needs no such check, because a browser cannot call it without the service key (D-034).
- Why: Standard form tokens depend on keys that live inside the container, and every deploy loses them. A form
  opened before a deploy would then fail on Save, which is an annoying thing to run into during a live change. Here
  I use a strict cookie and check that the request came from my own site, without depending on those keys.
- Cost: There is less depth than with tokens. The check relies on the browser sending `Origin` on form posts, and
  on the proxy passing the original `Host`. If Railway changes `Host`, every admin POST is a visible 403. A
  `Referrer-Policy: no-referrer` header added later would make browsers send `Origin: null` on form posts, and
  every admin POST would be 403. The alternative, `Sec-Fetch-Site: same-origin`, needs no `Host` but is missing in
  Safari before 16.4.
- Revisit if: There is more than one admin, or the admin is served from another origin.

## D-022 The admin reads and writes through the server's data code, with no admin JSON API

- Status: replaced by D-033.
- Choice: The admin panel is a layer inside the server. It reads and writes through the same data code the API
  uses, and the browser never talks to the database. The device path is still API to database: a phone posts an
  entry to `/api/v1/entries` and the server writes the SQL.
- Why: The admin is inside the server, so it just uses the same data code directly. Calling its own API over HTTP
  would mean another address to configure and another place for requests to fail. I don't need that here.
- Cost: The admin is not a client of the API. Typed text is lost when the database fails at Save, because the
  answer is an error body and not the form. A double click on Save of a new tip can create two drafts.
- Revisit if: A second client, such as a mobile admin or scripts, needs admin operations.

## D-023 The client keeps one file: token and outbox together

- Status: in use from slice B.
- Choice: `state.json` in the app's private storage holds the device id, the token and every local entry. It is
  written to a temporary file and moved over the old one. A file that cannot be read is copied aside, never
  deleted, and the app starts empty with a visible warning.
- Why: One file means one write, so the token and the entries can't get out of sync. Also resetting the demo is
  easy, just delete the file.
- Cost: The token sits in plain text in app-private storage. Someone with an unlocked or rooted phone can read it.
- Revisit if: The token needs to be protected, or the data grows large enough to need a database.

## D-024 An entry is Pending when the server gave no usable answer, Failed when it refused or crashed

- Status: in use from slice B.
- Choice: No usable answer is a network error, a timeout, or a status of 429, 502, 503 or 504. The entry stays
  Pending and the 10 second tick sends it again. The tick stops at the first Pending outcome, so a blocked server
  costs one request per tick. Any other answer, including 500, makes the entry Failed with the server's `code`, and
  only the user's Retry sends it again.
- Why: If the server is down or overloaded, it's fine to wait and try again. If the server said no, retrying again
  and again only eats the hourly limit. When there's no usable answer, the app tries again later on its own. When
  the request is refused or fails, it shows the problem to the person and lets them press Retry.
- Cost: A 500 or a refusal needs a tap on Retry. The status line counts failed entries, and that is the only alarm.
- Revisit if: A real deploy shows the proxy answers something else during a restart.

## D-025 After a 401 the client sends everything it holds again

- Status: in use from slice B.
- Choice: A 401 means the server does not know this token, so the client registers a new device and marks every
  local entry Pending, including the ones it had marked Synced. A second 401 within 60 seconds of a registration is
  final: that entry is Failed `unauthorized` and no new registration starts.
- Why: Say I restore the database from an older backup. Then the server doesn't know about devices and entries made
  after that backup, but the phone still has them. A 401 tells the phone "I don't know you", so the phone registers
  again and sends all it has. Every entry has its own id, so sending it twice won't create a copy.
- Cost: If the token was damaged while the old device row still exists, the old row owns the entries and every
  resend is refused as `id_taken`. Those rows stay Failed and Retry cannot fix them. The data is safe on the
  server under the old device. A restore from a dump taken after the device was registered raises no 401, and
  entries newer than the dump are lost without a sign. A delete in progress turns this off, see D-032.
- Revisit if: The restore drill needs the second case caught.

## D-026 Two server addresses in one config asset

- Status: in use from slice B.
- Choice: The config asset has an address for the Editor and an address for a device. One line picks by
  `Application.isEditor`.
- Why: The phone and the Editor need different server addresses. I keep both in one config asset and the app picks
  the right one automatically. I don't have to edit anything before each build.
- Cost: Two fields to keep right.
- Revisit if: There is a third place to run the client, such as a staging server.

## D-027 No art in the first client

- Status: in use from slice B.
- Choice: Plain uGUI, default font and flat colours.
- Why: First I needed the data to go all the way to the server and back. Art can come later and it doesn't change
  any logic.
- Cost: The first demo looks plain.
- Revisit if: The first demo needs to look finished.

## D-028 The client polls only `/health`

- Status: in use from slice B.
- Choice: Every 10 seconds the client asks `/health`. Device endpoints are called on events: start, the server
  coming back, after a send, on resume and on the Refresh button.
- Why: Every 10 seconds the app checks if the server is up. This check doesn't count toward the device limit, so
  waiting for the server never uses up requests the person actually needs.
- Cost: `/health` does not touch the database, so the client can show Online while every post answers 503. The
  status line then shows the waiting entries.
- Revisit if: The server gets a cheap authenticated ping.

## D-029 Move the server to .NET 10 LTS

- Status: in use from the move.
- Choice: The server and its tests target .NET 10. The build and runtime images are the 10.0 tags. D-001 now says
  .NET 10; it started on .NET 9.
- Why: .NET 9 support ends in November 2026. .NET 10 is the long term release, so I moved before that and ran all
  tests again.
- Cost: The .NET 10 SDK has to be installed. Any change in behavior needs a second look; the tests and a compose
  run were repeated after the move.
- Revisit if: The next LTS ships.

## D-030 Backups are `pg_dump -Fc` from a same-major image, and a restore is proven

- Status: in use from the local drill.
- Choice: `scripts/backup.ps1` runs `pg_dump -Fc` inside a `postgres:<major>-alpine` container. The major comes
  from `SHOW server_version` on the database being dumped, never from a constant. A restore drill puts the dump
  into a scratch PostgreSQL of the same major, compares row counts of every table and the SHA-256 of `entries` in
  a fixed order, and runs the smoke test on a server that uses the restored copy.
- Why: I don't trust a backup until I've restored it. So I restore every dump into a clean database and compare row
  counts and a hash of the entries. pg_dump has to match the database version, so the script asks the database
  which version it is instead of guessing.
- Cost: Docker has to run on the machine that takes the dump. The first run pulls a second image to read the
  version. There is no scheduled backup, so the loss window is the time since the last dump.
- Revisit if: A scheduled backup is added, or the database moves to a provider with its own point-in-time restore.

## D-031 CI runs the server tests and a history secret scan

- Status: in use. The workflow ran green on a pull request and on the push to `main` (2026-10-29).
- Choice: One GitHub Actions file with two jobs. `server` runs the server test suite. `secrets` runs gitleaks over
  the full history with a pinned version and a checksum check. It runs on a push to `main` and on pull requests.
  The Railway "Wait for CI" switch stays off until the first deploy, because Railway is not connected yet. The
  Unity tests are not in CI.
- Why: Every push to main runs the server tests and scans the whole git history for secrets. That gives me a chance
  to catch a broken migration or a leaked key before deploy. Unity tests need a Unity licence on the build machine.
  That's more setup than I want for this project, so I run them by hand.
- Cost: The Unity client is checked by hand. A Docker or runner change on GitHub could fail the server job for a
  reason that is not in the code.
- Revisit if: The first run fails, or the client grows past what a manual EditMode run before a merge can cover.

## D-032 Deleting is finished by the app, not called off

- Status: in use from slice D1.
- Choice: The app saves `deleteRequested` in `state.json` before it sends the delete, and while it is set it sends
  only the delete: no sync, no new registration after a 401, no new entries. A 204, or a 401 with code
  `unauthorized`, means done: the app clears the token, the device id and every local entry, and is a fresh install
  after that. A refusal (a `4xx` other than our `401`) clears the flag and changes nothing. No answer, and a `500`,
  keep the flag, and the tick and the next start send the delete again. A `500` is kept because it can come after
  the delete already happened.
- Why: After the server deletes a device, any old request from that phone gets 401. Usually 401 makes the app
  register again and send everything it has. Without a guard, the deleted data would just come back a few seconds
  later and nobody would notice. That's why the app saves a flag before it sends the delete request. The flag stays
  after a restart, so even if the answer got lost the app still finishes the delete and doesn't upload anything
  back.
- Cost: Once sent, a delete cannot be called off. Offline, no new entry can be written until the server answers. If
  the server never answers again, the app stays on the waiting panel for good, and the only way out is to delete
  `state.json` (RUNBOOK section 6). A server that keeps answering `500` keeps the panel up too. When D-025 has left
  entries `Failed` with `id_taken` under an older device, the delete removes only the current device. The app says
  how many entries stay on the server, and the old row is removed by hand (RUNBOOK section 9). If the token itself
  is damaged and the delete is the first request, the delete gets `401` and the app reports done with no count. The
  same happens when a damaged `state.json` was copied aside and the app started empty: it cannot see the older
  data. The delete counts against the 600 requests an hour like any call. A registration that finished just after
  the person confirmed leaves an empty device row. Server backups taken before the delete still hold the entries.
- Revisit if: Real people use it (backups, an audit record of deletions).

## D-033 The admin is its own app and a client of the API

- Status: in use from the admin app split.
- Choice: The admin pages are a separate program, `admin/JournalStack.AdminApp/`, with its own Docker image. It
  holds no database connection and no password hash. It asks the server's admin API (`/admin/api/...`) for
  everything: sign in, sign out, tips, entries. The server keeps the password check, the lockout, the session table
  and all the SQL. The browser talks only to the admin app. The device API does not change.
- Why: The admin should use the API the same way the app does. A fault in the admin pages cannot reach the
  database, because the admin app has no way to open a connection.
- Cost: One more service and one more hop to configure and watch. The two small records for tips and entries are
  written on both sides of the hop. Typed text is still lost when a save fails. A failed call can now fail in more
  ways, and each one has its own answer (WHAT-CAN-GO-WRONG, failure 11).
- Revisit if: The admin needs data the API does not offer.

## D-034 The admin app proves itself with a service key and passes the session through

- Status: in use from the admin app split.
- Choice: Every call from the admin app to the server carries `X-Admin-Key`, a shared secret of 32 characters or
  more, in the `ADMIN_API_KEY` variable on both services. Calls after sign-in also carry the session token in
  `X-Admin-Session`. The server checks the key first on every admin route, in constant time, before any password is
  checked or any attempt is counted. A key that is missing or shorter than 32 characters counts as not set, and the
  server then refuses every admin route with its own code. The password check and the session stay on the server,
  so a redeploy of either service does not sign the admin out.
- Why: The server's address is public because the app needs it. Without the key anyone who finds the address could
  try the admin routes. With the key a stranger gets 401 on every one of them.
- Cost: One more secret to keep and rotate. The admin sign-in page is still public, so the cost in D-019 stays: a
  stranger can use up the five attempts a minute through it. The hop between the two services is plain HTTP on the
  private network. Whether Railway's private network encrypts the traffic has not been checked.
- Revisit if: The admin API gets a second caller, or the private hop has to be encrypted.
