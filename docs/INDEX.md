# Docs index

Read this first. Every Markdown file under `docs/` and `server/docs/`
is listed below with a status label, so you can tell at a glance which
docs are kept current and which are reference-only.

Adding a new doc? Add an entry here in the same PR. The doc-gate CI
check fails otherwise — see `.github/workflows/doc-gate.yml`.

## Authoritative

These are kept current with the code. If the code disagrees with one of
these, the doc is the bug.

- [`TechnicalDesign.md`](TechnicalDesign.md) — architecture, class
  structure, public contracts. Source of truth for technical decisions.
- [`GDD.md`](GDD.md) — game design intent and player-facing behavior.
- [`BoardGeneration.md`](BoardGeneration.md) — generator algorithm,
  dependency graph, cycle detection, loading-progress heuristic.

## Operations

How-to guides for setting up the dev environment and running things.

- [`../server/docs/LocalServerSetup.md`](../server/docs/LocalServerSetup.md) —
  local server bring-up (Docker Compose, Postgres, Redis, worker).
- [`../server/docs/ServerRotation.md`](../server/docs/ServerRotation.md) —
  VPS rotation, deployment flow.
- [`AndroidTesting.md`](AndroidTesting.md) — Android build + on-device
  testing guide.
- [`ReleaseChecklist.md`](ReleaseChecklist.md) — what to verify before
  cutting a release tag.

## History

Reference snapshots of design decisions. Not kept current — read for
the "why" of past choices, not the current state.

*(currently empty — `OnlineRoadmap.md`, `CoopRoadmap.md`,
`AntiCheatDesign.md`, `GameModeRefactor.md` were deleted once their
content moved into TechnicalDesign.md or became obsolete.)*

## Active

Living docs for in-progress work. Deleted or promoted to Authoritative
when the work lands.

*(currently empty — the per-feature `TODO.md` workflow was killed in
PR #151. Larger features now live as GitHub issues plus linked PRs.)*
