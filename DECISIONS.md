# Decisions

Each entry has four parts: Choice, Why, Cost, Revisit if. The Why lines are drafts and get rewritten by the
owner in his own words. Status says whether the decision is already in the code.

## D-001 Stack: .NET 9, PostgreSQL, Dapper, DbUp

- Status: in use from slot 1.
- Choice: ASP.NET Core minimal API on .NET 9, PostgreSQL, Dapper with hand-written SQL, DbUp for migrations.
- Why: The Unity client is C# too, so one language covers both halves. The SQL is written by hand, so every
  query can be read and explained.
- Cost: Dapper does not map or migrate anything for us. More SQL is typed by hand.
- Revisit if: The company's own server runs on a different stack and matching it becomes the goal.

## D-002 The people who review this are not developers of this stack

- Status: applies to every file.
- Choice: Keep the design small and plain. Nothing is added to look advanced.
- Why: The founder is not a developer. The recruiter is not a Unity developer and probably not a backend
  developer. Neither will follow every detail, so the stand has to work, and each part has to be explainable in
  plain words.
- Cost: Some common production features are left out and listed under "not done".
- Revisit if: A technical reviewer joins the process.

## D-003 One Railway environment

- Status: planned for slot 4.
- Choice: One hosted environment. The safe place to try a change first is the local `docker compose` stack.
- Why: A second hosted database costs money that this demo does not justify.
- Cost: A change is tried locally, not on a second hosted copy.
- Revisit if: The stand becomes a real product.

## D-004 Migrations run only as a separate step before the new version starts

- Status: compose in slot 0, Railway pre-deploy in slot 4.
- Choice: `dotnet JournalStack.Server.dll migrate` applies pending scripts and exits with an error code on
  failure. Compose runs it before the server. Railway runs it as the pre-deploy command. The server never
  migrates on startup.
- Why: A failed migration then stops the deploy and the old version keeps serving.
- Cost: If Railway's pre-deploy cannot be used on the chosen plan, this needs another answer.
- Revisit if: Slot 4 shows pre-deploy is not usable.

## D-005 Migrations only add

- Status: rule from slot 1.
- Choice: A release may add tables and nullable columns. Dropping or renaming waits for a later release.
- Why: Rolling back a deploy does not roll back the database, and an installed app outlives any deploy.
- Cost: Old columns stay for a while.
- Revisit if: No old client can still be in use.

## D-006 Connection by separate PG variables

- Status: in use from slot 1.
- Choice: `PGHOST`, `PGPORT`, `PGUSER`, `PGPASSWORD`, `PGDATABASE`. No `DATABASE_URL`.
- Why: The database driver does not accept a `postgresql://` URL. Railway and local compose use the same names.
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

- Status: in use from slot 3.
- Choice: Login creates a random token. Only its hash is stored in `admin_sessions`.
- Why: The framework's cookie depends on keys inside the container, which are lost on every deploy. The admin
  would be logged out during the live change.
- Cost: One more table and about 30 lines of code.
- Revisit if: Keys are stored outside the container.

## D-009 No CORS

- Status: in use from slot 1.
- Choice: CORS is not configured.
- Why: Unity is a native client and the admin pages come from the same origin.
- Cost: A browser app on another origin could not call the API.
- Revisit if: A web client on another origin is added.

## D-010 No per-IP limits

- Status: from slot 1.
- Choice: One global cap on device registration (30 per minute), a per-device cap of 600 requests per hour on
  all device endpoints, a per-device cap of 60 entry posts per hour, and 5 admin login attempts a minute for the
  whole server. All live in memory and reset on deploy.
- Why: The proxy address ranges are not known, and trusting the forwarded IP header would let anyone fake it.
- Cost: One abuser can use the whole registration cap. Limits reset on every deploy and do not span replicas.
- Revisit if: There is more than one replica, or real traffic.

## D-011 Tips have no version, last write wins

- Status: in use from slot 3.
- Choice: No version column on tips.
- Why: There is one admin.
- Cost: Two admins editing the same tip would overwrite each other.
- Revisit if: A second admin exists.

## D-012 Small admin

- Status: in use from slot 3.
- Choice: No statistics, no scheduled publishing, no paging beyond the newest 50 entries. No tip delete: a bad
  tip is set to draft. The tips list shows the newest 100.
- Why: None of it answers a question the founder asked.
- Cost: The admin cannot browse older entries.
- Revisit if: The number of entries grows.

## D-013 Images are checked and refused, not changed

- Status: planned for slot 2, last in the build order.
- Choice: The server checks type, size, dimensions and metadata, and refuses what fails. It does not re-encode.
  Images are stored in the database.
- Why: A server that repairs files has more code to explain, and refusing location data is easy to show.
- Cost: Bytes added after the end of the image are not checked. A bucket would scale better.
- Revisit if: Image size or backup time grows.

## D-014 Device deletion by cascade

- Status: cascade foreign key in 0004 and tested. The route comes in a later slice.
- Choice: `DELETE /api/v1/devices/me` removes the device and, by foreign key cascade, its entries and images.
- Why: It is the plain answer to a request to delete a person's data.
- Cost: There is no undo.
- Revisit if: Soft delete or an audit trail is needed.

## D-015 Migration numbers

- Status: 0004 in this slice.
- Choice: 0004 `entries`, 0005 reserved for `entry_images`, 0006 `add_entry_mood` is the live change. The rule:
  the images slice lands in `main` before the live change is first applied on Railway.
- Why: RUNBOOK and the call script name the live-change file, and a fixed number keeps them stable and stops two
  branches from claiming one number.
- Cost: A reserved gap. If images are cut, the live change takes 0005 and the file is renamed before its first
  Railway run.
- Revisit if: Images are cut, or the order of slices changes.

## D-016 The request hash covers the fields as received

- Status: in use from this slice.
- Choice: SHA-256 over the raw text, a newline, the raw date, a newline, then optional parts appended in later
  slices. The format is frozen and a test pins it to digests computed outside the code. The insert runs in one
  transaction with `ReadCommitted` written in the code, because the images slice puts the image row in the same
  transaction.
- Why: A retry must match a row stored by an older version of the server, and the trimming rules may change.
- Cost: `" a"` and `"a"` are different requests for the same id. A client must resend the same strings. The
  transaction holds one statement in this slice.
- Revisit if: Never for stored rows. A new format needs a new column, not a change to this one.

## D-017 Entry limits and checks

- Status: in use from this slice.
- Choice: 60 posts per device per hour, a 16 KiB body, 2000 characters (UTF-16 units), and `clientCreatedAt`
  checked for format only.
- Why: A person writes a few entries a day and a script a few dozen, and a stuck loop should stop. The body cap is
  the largest legal request without padding. The date is display only, so a wrong phone clock must not cost the
  entry.
- Cost: A burst above 60 an hour is refused with a retryable 429. Retries count against the cap. There is no
  `Retry-After`, the windows are fixed, the cap resets on deploy, and there is no per-IP limit. A request padded
  with outer whitespace past 16 KiB is refused with 413. A date in year 0001 or 9999 is stored as sent (the driver
  keeps it as `-infinity` or `infinity`) and is read back without a `Z`. An entry that conflicts on insert and then
  cannot be found gives a 500 instead of a dedicated code.
- Revisit if: Real usage shows up, there is more than one replica, or the admin needs sane dates.

## D-018 Admin pages are Razor Pages in the server project, with a content policy

- Status: in use from slot 3b.
- Choice: The admin is three pages (sign in, tips, entries) rendered by the server, in the server project. There
  is no JavaScript. The framework encodes every piece of text it writes. Every admin answer carries
  `Cache-Control: no-store` and the policy `default-src 'self'; frame-ancestors 'none'; form-action 'self'`.
- Why: Entry text comes from phones and is shown in the admin. Output encoding plus a policy that blocks inline
  script means a script-like entry is shown as plain text and cannot run.
- Cost: The pages live in the server project. A page that needs inline script or a file from another site cannot
  be added without loosening the policy.
- Revisit if: The admin needs something a plain form cannot do.

## D-019 Admin login limit

- Status: in use from slot 3.
- Choice: 5 login attempts a minute for the whole server. A right password counts as an attempt too. The count is
  taken before the form is read.
- Why: One admin, so a guessing script should be slow, and there is no trusted client address to count per IP.
- Cost: Someone who keeps failing locks the real admin out until the minute is over. The count resets on deploy.
- Revisit if: There is more than one admin or more than one replica.

## D-020 Admin sessions: keyed hash, 8 hours, no renewal, no cleanup

- Status: in use from slot 3.
- Choice: The session token is random. The table stores a keyed hash of it, and the key is the password hash. A
  session lasts 8 hours from login. It is not renewed, and expired rows are not deleted.
- Why: Changing the password hash in the settings ends every session at once, with no extra code.
- Cost: Typed text is lost if the session ends at the moment of Save. The table gets one row per login.
- Revisit if: Sessions have to be listed or ended one by one.

## D-021 No antiforgery tokens: SameSite=Strict plus an Origin check

- Status: in use from slot 3b. Replaces D-007.
- Choice: The session cookie is `SameSite=Strict`. Every admin POST must carry an `Origin` header whose host equals
  the server's own host, or it is refused with 403 before any page runs. A GET never changes data. The framework's
  own antiforgery token is switched off in one line in `Program.cs`.
- Why: The built-in token is encrypted with keys that live inside the container, and those keys are lost on every
  deploy. A form opened before a deploy would then fail on Save, which is exactly the live-change scene.
- Cost: There is less depth than with tokens. The check relies on the browser sending `Origin` on form posts, and
  on the proxy passing the original `Host`. If Railway changes `Host`, every admin POST is a visible 403. A
  `Referrer-Policy: no-referrer` header added later would make browsers send `Origin: null` on form posts, and
  every admin POST would be 403. The alternative, `Sec-Fetch-Site: same-origin`, needs no `Host` but is missing in
  Safari before 16.4.
- Revisit if: There is more than one admin, or the admin is served from another origin.

## D-022 The admin reads and writes through the server's data code, with no admin JSON API

- Status: in use from slot 3b.
- Choice: The admin panel is a layer inside the server. It reads and writes through the same data code the API
  uses, and the browser never talks to the database. The device path is still API to database: a phone posts an
  entry to `/api/v1/entries` and the server writes the SQL.
- Why: A server that calls its own API over HTTP needs a base address, a client, a forwarded cookie and two error
  paths, only to keep one sentence true.
- Cost: The admin is not a client of the API. Typed text is lost when the database fails at Save, because the
  answer is an error body and not the form. A double click on Save of a new tip can create two drafts.
- Revisit if: A second client, such as a mobile admin or scripts, needs admin operations.

## D-023 The client keeps one file: token and outbox together

- Status: in use from slice B.
- Choice: `state.json` in the app's private storage holds the device id, the token and every local entry. It is
  written to a temporary file and moved over the old one. A file that cannot be read is copied aside, never
  deleted, and the app starts empty with a visible warning.
- Why: One file means one write, and one step to reset a demo: delete the file.
- Cost: The token sits in plain text in app-private storage. Someone with an unlocked or rooted phone can read it.
- Revisit if: The token needs to be protected, or the data grows large enough to need a database.

## D-024 An entry is Pending when the server gave no usable answer, Failed when it refused or crashed

- Status: in use from slice B.
- Choice: No usable answer is a network error, a timeout, or a status of 429, 502, 503 or 504. The entry stays
  Pending and the 10 second tick sends it again. The tick stops at the first Pending outcome, so a blocked server
  costs one request per tick. Any other answer, including 500, makes the entry Failed with the server's `code`, and
  only the user's Retry sends it again.
- Why: A silent retry loop against a refusal would only use up the 60 posts an hour. A stopped or busy server is
  the case where waiting and trying again is right.
- Cost: A 500 or a refusal needs a tap on Retry. The status line counts failed entries, and that is the only alarm.
- Revisit if: A real deploy shows the proxy answers something else during a restart.

## D-025 After a 401 the client sends everything it holds again

- Status: in use from slice B.
- Choice: A 401 means the server does not know this token, so the client registers a new device and marks every
  local entry Pending, including the ones it had marked Synced. A second 401 within 60 seconds of a registration is
  final: that entry is Failed `unauthorized` and no new registration starts.
- Why: A database restored from an older dump has lost what the device stored after the dump. Entry ids are
  unique, so sending again is safe.
- Cost: If the token was damaged while the old device row still exists, the old row owns the entries and every
  resend is refused as `id_taken`. Those rows stay Failed and Retry cannot fix them. The data is safe on the
  server under the old device. A restore from a dump taken after the device was registered raises no 401, and
  entries newer than the dump are lost without a sign. A future `DELETE /devices/me` has to clear the local file
  first, or the client would send again what the user just deleted.
- Revisit if: The restore drill needs the second case caught.

## D-026 Two server addresses in one config asset

- Status: in use from slice B.
- Choice: The config asset has an address for the Editor and an address for a device. One line picks by
  `Application.isEditor`.
- Why: A phone cannot reach `127.0.0.1`, and one field would need an edit before every device build.
- Cost: Two fields to keep right.
- Revisit if: There is a third place to run the client, such as a staging server.

## D-027 No art in the first client

- Status: in use from slice B.
- Choice: Plain uGUI, default font and flat colours.
- Why: The client has to prove the data path first. Art is polish and can come later without changing logic.
- Cost: The first demo looks plain.
- Revisit if: The first demo needs to look finished.

## D-028 The client polls only `/health`

- Status: in use from slice B.
- Choice: Every 10 seconds the client asks `/health`. Device endpoints are called on events: start, the server
  coming back, after a send, on resume and on the Refresh button.
- Why: A device may make 600 requests an hour, and `/health` is not counted against it.
- Cost: `/health` does not touch the database, so the client can show Online while every post answers 503. The
  status line then shows the waiting entries.
- Revisit if: The server gets a cheap authenticated ping.
