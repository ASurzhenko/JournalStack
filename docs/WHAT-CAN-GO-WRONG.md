# What can go wrong

Each row is one way the stand can fail, what happens then, and the test or rehearsal that shows it. Server tests
are in `server/JournalStack.Server.Tests/`, app tests in `Assets/Tests/EditMode/`. Play checks (P1 to P13, and P-D1 to P-D8 for delete) and the
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
- `AdminApiTests`: `An_entry_posted_by_a_device_is_listed_unchanged_newest_first_with_exactly_five_properties`.
- `AdminAppTests`: `An_entry_posted_by_a_device_shows_in_the_admin_list_encoded_and_newest_first`,
  `The_rendered_pages_contain_no_script_no_event_attribute_and_no_style_attribute`,
  `Every_admin_answer_is_not_cached_and_carries_the_content_policy`.
- In a browser on the local stack, an entry with `<script>` and `onerror` text was shown as that literal text, with no
  dialog. Entries dated year 0001 and 9999 and a 2000-character entry were listed in full ([PROGRESS.md](PROGRESS.md),
  "Delete (slice D1)").
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
- `When_the_database_is_down_an_admin_api_call_answers_503_and_not_a_401`
- `When_the_database_is_down_an_admin_page_answers_503_and_not_a_redirect_to_login`
- `When_the_database_is_down_health_stays_up_and_ready_says_503`
- `A_failed_constraint_leaves_no_row_data_in_the_response_or_the_log`
- `An_unhandled_exception_logs_its_type_and_a_request_id_but_not_its_message`
- Also `When_the_insert_fails_inside_the_handler_the_answer_is_500_and_never_2xx` (`EntryTests`),
  `When_the_tips_table_is_gone_a_write_is_a_server_error_and_the_text_is_not_leaked` (`AdminApiTests`) and
  `T4_when_the_tips_table_is_gone_a_write_is_never_a_redirect_and_the_text_is_not_leaked` (`AdminAppTests`).

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

The database password, the admin password hash or the service key gets out.

What happens: change it on Railway and restart the service ([RUNBOOK.md](RUNBOOK.md), section 9). A new admin hash
ends every admin session at once (D-020). A new service key keeps the sessions and needs the same value on the
server and on the admin app. Secrets are only in `.env` locally (ignored by git) and planned as Railway
variables. CI scans the whole git history with gitleaks on every push to `main` and every pull request (D-031).

Shown by:
- `A_new_password_hash_ends_every_old_session`,
  `Neither_the_password_nor_the_token_nor_the_hash_nor_the_key_reach_the_server_log` (`AdminApiTests`).
- `Neither_the_password_nor_the_token_nor_the_hash_nor_the_cookie_nor_the_key_reach_either_log`,
  `N13_a_full_session_leaves_no_secret_in_the_admin_app_log` (`AdminAppTests`).
- `A_short_key_makes_the_server_warn_by_name_without_printing_the_key` (`AdminApiTests`) and
  `N14_startup_refuses_a_missing_address_a_missing_key_and_a_short_key_and_never_prints_the_key` (`AdminAppTests`).
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

### 10. A person deletes their data, and the answer is lost or the server is down

The person taps Delete my data and confirms. The phone goes offline, the server is down, or the answer never
arrives.

What happens: the app saves a flag in its file before it sends the delete. While the flag is set, the app sends only
the delete. It sends no entries and makes no new registration. The server answers 204 and removes the device and its
entries. The app clears its token and entries only after that answer. A 401 in the server's own shape counts as the
same answer, because the server then holds nothing for this token. If there is no answer, or the server answers 500
(it may have deleted before it failed), the flag stays. The app sends the delete again every 10 seconds and at the
next start. A refusal clears the flag and changes nothing. The trap is D-025: a deleted device gets 401 on every old
request, and the app would register again and send everything it holds. The flag turns that off (D-032). Once the
delete is sent it cannot be called off, and the panel stays until the server answers.

One case the delete does not cover: if the app lost its token earlier and registered again, the old device row keeps
its entries. The app counts those entries (`Failed` with `id_taken`) and says that they stay on the server. The
admin removes them by hand ([RUNBOOK.md](RUNBOOK.md), section 9).

Shown by:
- Server tests (`DeviceTests`): `Delete_me_answers_204_and_removes_this_device_and_its_entries_only`,
  `After_delete_the_old_token_gets_401_on_every_route_and_stores_nothing`,
  `Delete_without_a_token_or_with_a_wrong_one_answers_401_and_deletes_nothing`,
  `After_delete_an_old_entry_id_is_free_so_only_the_app_can_stop_a_resend`,
  `When_the_delete_fails_inside_the_handler_the_answer_is_500_and_nothing_is_deleted`,
  `Two_deletes_at_once_give_no_server_error_and_the_device_is_gone`,
  `A_delete_whose_device_row_is_held_by_an_open_delete_waits_and_then_answers_204`,
  `A_capped_device_gets_429_on_delete_keeps_its_data_and_can_delete_when_the_window_reopens`. In `FailureTests`:
  `When_the_database_is_down_a_delete_answers_503_and_never_204`. Each server safeguard was removed once and a named
  test failed ([PROGRESS.md](PROGRESS.md), "Delete (slice D1)").
- `scripts/smoke.ps1` deletes its own device on a running server and checks that the old token is then refused.
- App tests: `DeleteOutcomeTests` (which answers count as done, no answer or refused) and the delete tests in
  `OutboxTests` and `StateFileTests`. Each client safeguard was removed once and a named test failed
  ([PROGRESS.md](PROGRESS.md), "Delete (slice D1)").
- Play checks (local stack, Editor, [PROGRESS.md](PROGRESS.md), "Delete (slice D1)"):
  - P-D1: a delete with two entries gave `DELETE -> 204`, the device and entries were gone, and the app held no token.
  - P-D3: with the server down the app showed the waiting text, also after a restart, and sent no entry and made no
    new registration. When the server came back the delete went through.
  - P-D4: a lost answer (the device row deleted while the server was paused) ended with `DELETE -> 401` and Done.
    The Pending entry was never sent.
  - P-D5: with a database trigger that refuses the delete, the app got `500` every 10 seconds, kept its data and
    kept waiting. After the trigger was dropped the next try gave `204`.
  - P-D6: after a delete a new entry registers a new device and is sent once.
  - P-D7: while the waiting panel is up it blocks every tap, so no new entry can be written.
  - P-D8, one run: a tip request answered `401` while the delete flag was set. No new device was registered.

Reasoned, not tested:
- The guard in `TrySend`, the check after `RegisterAsync` in `HandleUnauthorized`, and the generation bump after
  the clear. No repeatable run reaches the timing they cover.
- The guard in `SendNewAsync`. The waiting panel blocks the input before it is reached (P-D7).
- The guard at the top of `HandleUnauthorized`. P-D8 ran with it in place and no device was registered, but the
  guard was never removed to see a registration appear, so the run does not show that the guard is the reason.
- A refusal by an older server without the route (P-D5b was not run). Only the `DeleteOutcomeTests` cover it.

### 11. The admin app cannot reach the server

The admin app is a separate program and calls the server's admin API over the private network. That call can fail
in several ways, and each one has its own answer. Only one of them signs the person out: the server saying the
session is gone.

What happens: the admin app decides by the `code` in the server's answer, never by the status number alone.

| Situation | What the person sees | Signed out? |
|---|---|---|
| The session ended | The sign-in page | Yes |
| The database is down, on any page or at sign-in | "The database is not reachable. Try again later." (503) | No |
| The server is stopped, or the address is wrong | "The server cannot be reached. Try again later." (503) | No |
| The server is slower than 10 seconds | "The server did not answer in time. A save may or may not have happened; check the list." (504) | No |
| The server fails on the call | "Something went wrong on the server." with the server's request id (502) | No |
| The service key is wrong | "The admin app is not set up to call the server." (502). A redirect would loop, because sign-in would fail the same way | No |
| The server has no good key | "The server has no admin key set." (503) | No |

A wrong key never reads as a wrong password, and a database outage never reads as "not set up". Typed text is lost
when a save fails, as before (D-022 cost, carried into D-033).

Shown by (`AdminAppTests`, the server replaced by a fake handler where it has to fail):
- `N8_when_the_server_cannot_be_reached_a_page_is_503_api_unreachable_and_not_a_redirect`
- `N9_when_the_server_is_slower_than_the_timeout_a_page_is_504_api_timeout`
- `N10_when_the_server_crashes_on_a_request_the_page_is_502_api_error_with_the_servers_request_id`
- `A_redirect_or_an_unmapped_answer_from_the_server_is_502_api_error_and_is_not_followed`
- `N11_a_wrong_service_key_is_502_admin_key_refused_never_a_wrong_password_and_never_a_sign_out`
- `A_server_without_a_key_is_503_admin_key_not_configured_for_the_admin_app`
- `N12_logout_while_the_server_is_down_sets_no_cookie_and_the_session_still_works_afterwards`
- `N16_with_the_database_down_sign_in_is_503_database_unavailable_never_a_redirect_and_never_not_set_up`
- `N19_logout_with_an_expired_session_redirects_to_sign_in_and_clears_the_cookie`
- `T3_when_the_session_insert_fails_sign_in_is_never_a_redirect_and_sends_no_cookie_at_all`
- Each of the safeguards behind these was removed once on purpose, and the named test failed (mutation table in
  [PROGRESS.md](PROGRESS.md)).
- Run on the local stack with `docker compose stop server`: the page answered 503, and after the server was
  started again the same cookie still worked ([PROGRESS.md](PROGRESS.md), "Admin app split").

## Other risks, stated plainly

- **Guessing the admin password.** Five attempts a minute for the whole server, a right password counts too, and
  the server counts the attempt before it reads the body (D-019). The admin app forwards every attempt to the
  server, so the counter holds whatever the admin app does. The cookie is `HttpOnly`, `SameSite=Strict`, `Secure`
  over HTTPS. Tests: `The_sixth_login_in_a_minute_is_locked_even_with_the_right_password_until_the_window_passes`,
  `The_lockout_answers_before_the_body_is_read_so_a_huge_sixth_attempt_is_a_429_and_not_a_413`
  (`AdminApiTests`), `Login_with_the_right_password_sets_a_safe_cookie_that_the_server_knows_by_a_keyed_hash`
  (`AdminAppTests`). Limits: one shared admin, no second factor, and someone who keeps failing locks the real admin
  out for that minute.
- **The admin sign-in page is public.** Anyone who finds the admin app's address can use up the five attempts a
  minute through it. The service key closes the direct way to the server's admin API: without the key every admin
  route answers 401 before any password is checked or any attempt is counted (D-034). Tests:
  `Every_admin_route_without_the_key_is_401_admin_key_and_changes_nothing`,
  `Keyless_logins_are_not_counted_so_a_keyed_login_in_the_same_minute_succeeds`,
  `Only_the_exact_key_in_the_key_header_is_accepted` (`AdminApiTests`).
- **The hop between the admin app and the server is plain HTTP.** The service key and the session token travel
  over the private network between the two containers. Locally that is the compose network. Whether Railway's
  private network encrypts the traffic has not been checked.
- **A database that stops answering.** A new connection gives up after 5 seconds and the admin shows the database
  message (test `A_database_host_that_does_not_answer_gives_503_after_the_connect_timeout`). A pooled connection
  that is already open does not use that timeout, and a query that hangs after the connection opened waits for the
  30 second command timeout. The admin app gives up after 10 seconds first, so those two cases show as 504.
- **A form on another site posting to the admin.** Every admin POST must carry this site's own `Origin`, or it is
  refused with 403 before any page runs (D-021). Test:
  `Every_admin_post_needs_an_origin_that_is_this_site_and_a_refused_post_changes_nothing` (`AdminAppTests`). Also
  tried in a browser on the local stack: a form on a page at another address posted to `/admin`, the answer was 403
  `csrf_origin`, and no tip was created.
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
- A delete does not reach backups. A dump taken before the delete still holds the entries until that file is
  removed.
- The scripts were only run on Windows with Docker Desktop. `host.docker.internal` may need another address on
  other systems.

**Admin app.**
- The constant-time comparison of the service key is checked by reading the code. No test can tell it from a plain
  comparison, because both accept and refuse the same inputs. The tests show that only the exact key is accepted.
- A private-only port for the admin API, so that the internet cannot reach it at all, was considered. It depends on
  Railway private networking behaviour that has not been checked, and the key works the same locally, in tests and
  on Railway (D-034).
- The admin app does not retry a failed call. A person presses the button again.
- When a call to the server fails, the admin app answers with the JSON error body and its code, the codes in failure
  11. A formatted error page for the person at the screen was planned and is not built.
- The form limit in the admin app is a pin on behaviour. The proof that no password check escapes the lockout is the
  server's test that a huge sixth body is refused with 429.

**Tests.**
- The Unity EditMode tests (93) are not in CI. They need a Unity licence and a runner image with Unity, which is more
  setup than the stand is worth. They are run by hand before a client change is merged (D-031).
- The second 401 within 60 seconds of a registration has no test and no Play check.
- A double click on Save in the tip form was tried once in a browser and gave one draft. The page reloaded after the
  first click, so the second click missed the button. A real double submit has not been produced in a browser.

**Phone.**
- The caret in the text box and reading a long entry in full were not checked on a device. They were skipped for lack
  of time. A 2000-character entry was read to the end in the Editor (P10), never on a phone.

**Features.**
- Images in entries are not built. Migration 0005 is kept for them (D-015).
- No way to cancel a single device token.
- The admin shows only the newest 50 entries and the newest 100 tips. There is one admin account (D-012).
- No seed script. Demo data is typed in through the app and the admin.

**What real use would need on top.** All data here is made up. A journal with real people's text would need at
least: an audit record of deletions and a way to remove a person's data from old backups, a database user with only the rights the server needs, encrypted storage of
the token on the phone (today it is plain text in the app's private storage, D-023), a scheduled backup kept
somewhere other than the host, more than one admin with a second factor, and limits that survive a deploy.
