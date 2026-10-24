# Progress

## Now

- Current slot: 2, slice A (entries as text with the idempotency core) is coded and tested on `feat/server-core`.
  It waits for the owner's review and commit. Slots 0 and 1 are in `main`.
- Done: slot 0, slot 1 (server, migrations 0001-0003, tests), and slice A: migration 0004, `POST` and `GET`
  `/api/v1/entries`, entry cap, body cap, text checks, the tests and the mutation pass below.
- Next step: slot 3 (admin). The images slice (0005) and
  `DELETE /devices/me` come later.
- Open questions: the recruiter's answer about their Railway stack; the exact wording of the 2024 production claim;
  whether to keep the reserved migration number 0005 (D-015).

## Sessions

One line per session: date, focused minutes, slot, minutes to get started again.

| Date | Focused min | Slot | Start-up min |
|---|---|---|---|
| 2026-10-24 | | 0 | |
| 2026-10-24 | | 2 (slice A) | |

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
