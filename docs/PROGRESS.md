# Progress

## Now

- Current slot: 3b (the admin front rebuilt as Razor Pages) is done. Slots 0, 1, 2 slice A (entries as text with the
  idempotency core) and 3 (admin panel) are in `main`.
- Done: slot 0, slot 1 (server, migrations 0001-0003, tests), slice A: migration 0004, `POST` and `GET`
  `/api/v1/entries`, entry cap, body cap, text checks, the tests and the mutation pass below. Slot 3: login, logout,
  session, tips and entries endpoints, the admin page, 54 new tests (169 in all) and the slot 3 mutation pass below.
  Slot 3b: the JSON admin API and the static page are replaced by three Razor Pages (sign in, tips, entries), the
  Origin check replaces the request-header check, 174 tests in all, and the slot 3b mutation pass below. The device
  API, the migrations and the data model did not change.
- Next step: slice B of the build order (minimal Unity client, then the first Railway deploy). The images slice
  (0005) and `DELETE /devices/me` come later.
- Slot 3 was checked against a separate compose stack with curl and SQL: cookie flags, lock after five tries,
  session survives a restart, old session ends on a new hash, 503 and not 401 with the database down. Slot 3b was
  built with `docker compose build` (the Razor views compile in the image) and smoke-checked on a throwaway stack:
  the login page and the stylesheet answer 200, `/admin` without a session redirects to the login page, and a POST
  without an `Origin` header is refused with 403.
  Not yet checked in a real browser: entry text that looks like script shown literally, entries dated year 0001 and
  9999, a 2000-character entry, a double click on Save (two drafts are the expected result), a form on another
  origin posting to `/admin`.
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
