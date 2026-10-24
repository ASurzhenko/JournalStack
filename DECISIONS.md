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

- Status: planned for slot 3.
- Choice: Cookie with `SameSite=Strict`, plus a required `Content-Type: application/json` and a fixed
  `X-Requested-With` header on every change.
- Why: One admin on one origin. A page on another site can send neither the header nor the content type.
- Cost: Less defence in depth than token-based protection.
- Revisit if: There is more than one admin, or the admin is served from another origin.

## D-008 A stored admin session instead of a framework cookie

- Status: planned for slot 3.
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
  all device endpoints, and a per-device cap of 60 entry posts per hour. All live in memory and reset on deploy.
- Why: The proxy address ranges are not known, and trusting the forwarded IP header would let anyone fake it.
- Cost: One abuser can use the whole registration cap. Limits reset on every deploy and do not span replicas.
- Revisit if: There is more than one replica, or real traffic.

## D-011 Tips have no version, last write wins

- Status: planned for slot 3.
- Choice: No version column on tips.
- Why: There is one admin.
- Cost: Two admins editing the same tip would overwrite each other.
- Revisit if: A second admin exists.

## D-012 Small admin

- Status: planned for slot 3.
- Choice: No statistics, no scheduled publishing, no paging beyond the newest 50 entries.
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
