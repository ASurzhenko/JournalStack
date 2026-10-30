# What can go wrong

Each row is one way the stand can fail, what happens then, and the test or rehearsal that shows it. Server tests
are in `server/JournalStack.Server.Tests/`, app tests in `Assets/Tests/EditMode/`. Play checks (P1 to P13) and the
drills are written up in [PROGRESS.md](PROGRESS.md). Decisions (D-numbers) are in [DECISIONS.md](../DECISIONS.md).

## The failures

### 1. An entry does not reach the server

The phone is offline, the request times out, or the server answers 429, 502, 503 or 504.

What happens: the app saved the entry to its file before sending, so it is not lost. It stays Pending and the app
sends it again every 10 seconds with the same id. It turns Synced only after the server confirmed it. A refusal
makes it Failed with the reason, and the person taps Retry (D-024).

Shown by:
- App tests (`OutboxTests`): `AddPendingIsOnDiskBeforeItReturns`, `AFailingSaveTakesTheEntryOutAgain`,
  `SyncedIsTerminalForEveryOutcome`, `FailedCanGoBackToPendingForRetry`.
- App tests (`SendOutcomeTests`): `NoUsableAnswerCodes`, `NetworkErrorIsNoAnswerWhateverTheStatus`.
- Play checks P4 to P7: server stopped, app restarted while it was down, server paused. Each time the entry was
  sent once when the server came back, and the database held one row.

### 2. The same entry arrives twice, or someone else's id is used

The answer to a send was lost and the app sends again. Two copies arrive at the same moment. A different entry
reuses an id. Another device sends an id that is already taken.

What happens: the entry id is the key in the database. A second copy of the same request gets 200 and the stored
row, and nothing new is written. The same id with other content gets 409 `id_conflict`. The same id from another
device gets 409 `id_taken`, and nothing of the other device's entry is shown.

Shown by (`EntryTests`):
- `A_retry_after_a_lost_response_answers_200_with_the_same_row_and_stores_nothing_new`
- `Eight_identical_posts_at_once_give_one_201_and_seven_200_without_a_server_error`
- `A_post_whose_row_is_held_by_an_open_transaction_waits_and_then_answers_200`
- `The_same_id_with_different_content_answers_409_id_conflict_and_keeps_the_stored_row`
- `The_same_id_from_another_device_answers_409_id_taken_and_shows_nothing_of_the_stored_entry`
- `scripts/smoke.ps1` checks the retry and the conflict on a running server.

### 3. Junk or hostile input

A huge body, a body that is not JSON, wrong types, control characters, 2001 characters, text that looks like SQL
or like a script.

What happens: the server checks every field again, because the app on a phone can be changed by whoever owns the
phone. Each refusal has its own code: 400, 413, 415 or 422. SQL is always sent with parameters, so SQL-looking
text is stored as plain text. The database repeats the key checks (length, required fields, known device) in case
the server code misses one. In the admin, every piece of text is encoded and a content policy blocks scripts, so a
script-like entry is shown as text (D-018).

Shown by:
- `EntryTests`: `A_bad_body_is_refused_with_its_own_code_and_stores_nothing`,
  `A_body_over_16_KiB_answers_413_whether_it_declares_its_length_or_arrives_chunked`,
  `A_body_that_is_not_json_by_content_type_answers_415`,
  `A_body_with_invalid_utf8_inside_the_text_answers_400_and_stores_nothing`,
  `Sql_looking_text_is_stored_and_read_back_unchanged`.
- `EntryRulesTests` (server): `Text_is_refused`, `Id_is_refused`, `Created_at_is_refused`.
- `MigrationTests`: `The_database_rejects_text_over_2000_characters_and_unknown_status`,
  `The_database_rejects_an_entry_for_an_unknown_device_empty_or_long_text_and_a_null_hash`.
- `AdminContentTests`: `An_entry_posted_by_a_device_shows_in_the_admin_list_unchanged_and_newest_first`.
- `AdminPageTests`: `The_rendered_pages_contain_no_script_no_event_attribute_and_no_style_attribute`,
  `Every_admin_answer_is_not_cached_and_carries_the_content_policy`.
- Each of these safeguards was removed once on purpose, and the named test failed each time (mutation tables in
  [PROGRESS.md](PROGRESS.md)).

### 4. A database write fails

The database is down, a table is missing, or a check in the database refuses the row.

What happens: the server answers 503 when the database cannot be reached and 500 for other errors. It never
answers 200 or 201 for a write that did not happen, so the app keeps the entry and does not mark it Synced. The log
gets the error type and a request id. It gets no row data and no entry text.

Shown by (`FailureTests`, which stop the test database):
- `When_the_database_is_down_register_answers_503_and_never_succeeds`
- `When_the_database_is_down_an_entry_post_answers_503_and_never_2xx`
- `When_the_database_is_down_an_admin_call_answers_503_and_not_a_redirect_to_login`
- `When_the_database_is_down_health_stays_up_and_ready_says_503`
- `A_failed_constraint_leaves_no_row_data_in_the_response_or_the_log`
- `An_unhandled_exception_logs_its_type_and_a_request_id_but_not_its_message`
- Also `When_the_insert_fails_inside_the_handler_the_answer_is_500_and_never_2xx` (`EntryTests`) and
  `When_the_tips_table_is_gone_a_write_is_never_a_redirect_and_the_text_is_not_leaked` (`AdminContentTests`).

### 5. A bad deploy

A new server version is broken.

What happens: go back to the previous version on Railway. That works because migrations only add, so the older
server still finds what it reads (D-005). Steps: [RUNBOOK.md](RUNBOOK.md), section 2.

Shown by: not yet. FILL AFTER DEPLOY: the rehearsal, and what Railway does with the pre-deploy step on a rollback.
The local half is the test
`A_migration_that_only_adds_a_column_keeps_old_entries_and_the_old_request_shape_still_gets_201`
(`MigrationTests`).

### 6. A bad migration

A migration script has an error.

What happens: `migrate` stops with an error code, so the new server never starts and the old one keeps serving
(D-004). Each script runs in one transaction, so a half-applied script is rolled back. The fix is a corrected
script and a new deploy.

Shown by:
- `Migrate_exits_with_an_error_code_when_it_fails` (`MigrationTests`).
- Local rehearsal with a broken `0005` script: exit code 1, the journal still at `0004_entries.sql`, row counts
  unchanged, the added column rolled back, and the running server still passed the smoke test
  ([PROGRESS.md](PROGRESS.md), "Bad migration, local rehearsal").
- On Railway, the same command is the pre-deploy step. FILL AFTER DEPLOY: that a failing pre-deploy really stops
  the deploy there.

### 7. The database is lost

What happens: restore the last dump ([RUNBOOK.md](RUNBOOK.md), sections 4 and 5). Data written after that dump is
gone from the server. Devices registered after the dump get 401. The app then registers again and sends every entry
it still holds, and the entry ids make the resend safe (D-025).

Shown by:
- `scripts/restore-drill.ps1`, run on 2026-10-29: restore 0.8 s, whole drill 15.5 s; table list, row counts and the
  `entries` SHA-256 equal; smoke test `PASS` on the restored copy; old device 200, new device 401, registration and
  resend 201. The drill also failed correctly when one row was deleted or one text was changed in the copy.
- App test `ResetAllToPendingLiftsSyncedAndFailed` (`OutboxTests`), and Play check P13 for the register-and-resend
  loop.

### 8. A secret leaks

The database password or the admin password hash gets out.

What happens: change it on Railway and restart the service ([RUNBOOK.md](RUNBOOK.md), section 9). A new admin hash
ends every admin session at once (D-020). Secrets are only in `.env` locally (ignored by git) and planned as Railway
variables. CI scans the whole git history with gitleaks on every push to `main` and every pull request (D-031).

Shown by:
- `A_new_password_hash_ends_every_old_session`,
  `Neither_the_password_nor_the_token_nor_the_hash_nor_the_cookie_reach_the_log` (`AdminAuthTests`).
- `Register_returns_a_token_once_and_stores_only_its_hash` (`DeviceTests`).
- The CI `secrets` job, green on 2026-10-29 ([PROGRESS.md](PROGRESS.md), "CI").
- FILL AFTER DEPLOY: the rotation steps on Railway, done once.

### 9. An old app meets a new server

A phone still has an older build after the server and the database changed.

What happens: migrations only add optional columns, and the server ignores fields it does not know. The request
fingerprint only grows at the end, so an old app's retry still matches its stored entry (D-016).

Shown by:
- `The_first_shipped_request_shape_keeps_working_and_unknown_fields_change_nothing_stored` (`EntryTests`).
- `A_migration_that_only_adds_a_column_keeps_old_entries_and_the_old_request_shape_still_gets_201` and
  `The_last_script_applied_on_top_of_older_data_leaves_every_row_as_it_was` (`MigrationTests`).
- `Register_ignores_whatever_body_an_older_or_odd_client_sends` (`DeviceTests`).

## Other risks, stated plainly

- **Guessing the admin password.** Five attempts a minute for the whole server, a right password counts too, and
  the count happens before the form is read (D-019). The cookie is `HttpOnly`, `SameSite=Strict`, `Secure` over
  HTTPS. Tests: `The_sixth_login_in_a_minute_is_locked_even_with_the_right_password_until_the_window_passes`,
  `Login_with_the_right_password_sets_a_safe_cookie_and_stores_only_a_keyed_hash` (`AdminAuthTests`). Limits:
  one shared admin, no second factor, and someone who keeps failing locks the real admin out for that minute.
- **A form on another site posting to the admin.** Every admin POST must carry this site's own `Origin`, or it is
  refused with 403 before any page runs (D-021). Test:
  `Every_admin_post_needs_an_origin_that_is_this_site_and_a_refused_post_changes_nothing` (`AdminAuthTests`).
- **Railway itself is down.** Not covered. The only copy outside Railway is the last dump on the owner's computer.
- **A wrong clock on the phone.** The phone's time is shown only. The server stores its own receive time beside it,
  and a strange phone date never costs the entry (D-017). Test:
  `An_extreme_client_date_is_stored_and_listed_never_refused` (`EntryTests`).
- **Too many requests from one source.** One cap for all registrations (30 a minute), 600 requests and 60 entries
  per device per hour. The caps are in memory and reset on every deploy. There is no per-address limit, because the
  proxy's addresses are not known and a forwarded address can be faked (D-010). Tests:
  `Registration_is_capped_for_the_whole_server_and_the_window_resets`,
  `One_device_is_capped_per_hour_and_other_devices_are_not_affected`,
  `Only_the_forwarded_scheme_is_trusted_never_the_forwarded_address` (`DeviceTests`),
  `The_61st_post_in_an_hour_answers_429_entry_limit_and_the_window_reopens` (`EntryTests`).

## Not done and why

**Railway.** Nothing has been deployed yet, so none of these has run:
- A deploy, and a failing pre-deploy step stopping it.
- A rollback, and what Railway does with the pre-deploy step on a rollback.
- A dump through the Railway TCP proxy, with the steps to open and close it.
- Railway volume backups. It is not known yet whether the plan includes them.
- Logs, restart and secret rotation on Railway.

These are planned for the first deploy and are marked FILL AFTER DEPLOY in [RUNBOOK.md](RUNBOOK.md).

**Backups.**
- No scheduled backup. A dump is taken by hand, so the loss window is the time since the last one. A schedule needs
  a second place to keep the database password (D-030).
- A restore from a dump of a different PostgreSQL major version has not been tested. The drill uses the same major
  on both sides.
- The scripts were only run on Windows with Docker Desktop. `host.docker.internal` may need another address on
  other systems.

**Tests.**
- The Unity EditMode tests (68) are not in CI. They need a Unity licence and a runner image with Unity, which is more
  setup than the stand is worth. They are run by hand before a client change is merged (D-031).
- The second 401 within 60 seconds of a registration has no test and no Play check.
- Checks in a real browser not done yet: script-like entry text shown as text, entries dated year 0001 and 9999, a
  2000-character entry, a double click on Save, and a form on another site posting to `/admin`. The server tests
  cover the same rules, without a browser.

**Phone.**
- The caret in the text box and reading a long entry in full were not checked on a device. They were skipped for lack
  of time. A 2000-character entry was read to the end in the Editor (P10), never on a phone.

**Features.**
- Images in entries are not built. Migration 0005 is kept for them (D-015).
- `DELETE /api/v1/devices/me` is not built, so a person cannot delete their own data from the app. The database part
  is there: deleting a device deletes its entries (D-014).
- No way to cancel a single device token.
- The admin shows only the newest 50 entries and the newest 100 tips. There is one admin account (D-012).
- No seed script. Demo data is typed in through the app and the admin.

**What real use would need on top.** All data here is made up. A journal with real people's text would need at
least: deletion on request in the app, a database user with only the rights the server needs, encrypted storage of
the token on the phone (today it is plain text in the app's private storage, D-023), a scheduled backup kept
somewhere other than the host, more than one admin with a second factor, and limits that survive a deploy.
