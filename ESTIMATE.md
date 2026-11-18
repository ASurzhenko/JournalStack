# Estimate

Written on 2026-10-24, before the first line of server code. This part is never rewritten. The variance
note is added below it at the end.

## The number

Expected: **30 focused hours**. Working range: **24 to 44 hours**.

This is for one experienced developer who knows Unity, has the accounts ready and works from the plan.
About 3 hours of contingency are already inside the 30.

Focused hours are hours at the keyboard. Gaps between sittings do not count. Calendar time is focused hours
divided by hours per working day, so it is filled in once the typical day is known.

## Slots

| Slot | Work | Hours |
|---|---|---|
| 0 | Setup and docs first | 3 |
| 1 | Server A: migrations, devices, tips, health, limits | 3.5 |
| 2 | Server B: entries, idempotency, images | 3.5 |
| 3 | Admin panel | 3 |
| 4 | First Railway deploy | 2.5 |
| 5 | Unity client | 7 |
| 6 | Ops: backup, restore drill, rollback, bad migration | 3.5 |
| 7 | Docs and rehearsal | 4 |
| | Total | 30 |

## Build order

The scope is not reduced to fit a short deadline. The order changes, so a working stand exists early.

| Slice | Contents | Cumulative hours | Working stand at the end |
|---|---|---|---|
| A | Slot 0, slot 1, slot 2 without images, slot 3 | 11 to 12 | Admin changes a tip, a script posts entries, the admin sees them. All local |
| B | Slot 5 minimal, slot 4 | 19 to 21 | All four parts work on Railway, from a device |
| C | Slot 6, the two reported client defects, remaining failure-mode tests, slot 7 | about 30 | Full plan without images |
| D | Images | +3.5 | Full plan |

## Risks

- Railway behaves differently from its documentation (four facts are unverified).
- Testcontainers on this machine's Docker.
- `UnityWebRequest` over HTTPS on a real device.
- An input bug that only shows on Android. It has a 1.5 hour time box, then a fallback ships.

## Not in the number

Open-ended Unity device problems beyond the time boxes. Custom domain, end-user accounts, push messages,
analytics, real data. Elapsed time is longer than focused time, because first-time Railway setup and device
runs wait on real systems.

## Deadline

None yet. The call is scheduled after the build.

## Variance (added 2026-11-18)

I did not log focused minutes per session, so there is no exact total. My own count is about four to five
working days of focused time, close to the upper half of the range above. Calendar time ran from 2026-10-23 to
2026-11-09, part-time, plus small fixes and the docs after the first Railway deploys.

Work beyond this estimate: the admin pages rebuilt as Razor Pages (slot 3b, about 2 to 2.5 h), the admin split
into its own app (D-033, D-034), Delete my data (D-014, D-032), and the Local/Hosted switch in the Editor.
Not done from the plan: images (slice D).

The longest overrun was the first Railway setup, as listed under Risks: settings that looked right on screen but
reached the container differently (see PROGRESS, "Railway, first deploy").
