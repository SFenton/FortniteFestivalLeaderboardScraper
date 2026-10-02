---
status: decision
owner: data
last_verified: 2026-10-02
last_verified_commit: 598965ef
sources:
  - docs/database/SnapshotGenerationArchiveRetirementRunbook.md
  - tools/postgres-snapshot-archive-retire.py
  - deploy/systemd/fst-snapshot-retire-auto.service
  - deploy/systemd/fst-snapshot-retire-auto.timer
update_triggers:
  - Automatic retirement windows, cycle selection, failure handling, or scheduling change.
---

# ADR 0011: Retire each scrape's agreeing cycle automatically

## Context

[ADR 0010](0010-snapshot-archive-retirement.md) made archive retirement an
operator-run tool bound to one cycle hash. Cycle `95` reclaimed 1.13 TiB.
Afterwards every scrape still left about 9 GiB of newly unreferenced children
(cycles `97`–`112` grew from 76 to 152 GiB of candidates over 17 scrapes), so
the FST drive grew about 36 GiB per day again. That growth only stays bounded
if someone runs the tool during every scrape's fetch window, and storage work
had already been delayed for weeks waiting on operator time.

## Decision

A host systemd user timer runs the same tool's `auto` mode every 10 minutes.
`auto` acts only while `/api/service-info` reports network-bound leaderboard
fetching (an explicit allow-list of sub-operations), takes the newest
observed, report-only, oracle-agreeing cycle recorded for the current
publication, binds that cycle's own candidate hash, and runs the unchanged
`retire` sequence, re-checking the window before every child. Live-safety
refusals (load, health, disk, contention) exit successfully and retry on the
next tick. Any integrity failure writes an `AUTO_DISABLED` tripwire that
keeps automation idle until an operator clears it.

## Consequences

- Space from each scrape's newly unreferenced generations is reclaimed during
  the next scrape's fetch, without operator involvement.
- Per-cycle authorization moves from a human-supplied hash to the recorded
  cycle gates (planner/oracle agreement, no blockers, current-publication
  binding) plus the per-child liveness re-proof, archive verification, and
  restore drills that ADR 0010 already required.
- Retirement never overlaps flushes, index builds, or post-processing, because
  every non-fetch state, including an unreachable API, closes the window.
- Archives keep accumulating (about 8% of reclaimed bytes) and are never
  deleted automatically; archive retention remains an operator decision.
- The worker stays unaware of retirement; disabling is one file or one
  `systemctl --user disable --now`.
