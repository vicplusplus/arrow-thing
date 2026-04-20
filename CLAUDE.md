# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository. **It's not a replacement for `docs/TechnicalDesign.md`** — the TDD is the single source of truth for architecture, class-by-class responsibilities, and design decisions. Read it when you need to understand a subsystem. This file is the thin layer on top: how to work on this repo, not what's in it.

## Project Overview

**Arrow Thing** — a minimalist speed-clearing puzzle game (Unity 2D URP). Players tap arrows on a grid to clear them; an arrow is clearable only when the ray extending forward from its head to the board boundary contains no other arrow body cells. The dependency graph between arrows must be acyclic (DAG) for a board to be solvable.

Free and open-source (MIT). Primary distribution is WebGL on Cloudflare Pages (https://arrow-thing.com/), deployed automatically via CD pipeline on published release. Active development; playable in browser today.

## Where to find things

- **`docs/TechnicalDesign.md`** — architecture, class responsibilities, server + client layering, invariants, testing strategy. Single source of truth for all technical decisions.
- **`docs/GDD.md`** — game design.
- **`docs/BoardGeneration.md`** — generator algorithm.
- **`docs/OnlineRoadmap.md`**, **`docs/CoopRoadmap.md`** — planned features.
- **`CONTRIBUTING.md`** — test expectations, coding conventions.
- **`docs/ReleaseChecklist.md`** — pre-release checklist; must pass on staging before tagging a prod release.
- **`docs/TODO.md`** — only exists during active feature work (see Feature Workflow below).

Two-layer split you'll hit constantly:
- **Domain layer** (`Assets/Scripts/Domain/`) — Unity-independent pure C#. Board state, arrow rules, clearability, generation. Testable without Unity runtime.
- **View / adapter** (`Assets/Scripts/View/`) — Unity-specific: input, rendering, animation, scene wiring. Does not own gameplay rules.

## Feature Workflow

New features follow a four-step workflow:

1. **Design** — create `docs/TODO.md` with the feature design, implementation plan, and open questions. Resolve open questions before implementation. When a `TODO.md` exists, treat it as the authoritative task list for the current feature. The plan must include a testing step — automated tests for domain classes (per `CONTRIBUTING.md`) and manual test cases for user-facing behavior.
2. **Implement** — build against `TODO.md`. Don't delete or simplify it mid-feature; it captures design decisions that inform the implementation.
3. **Test** — add manual test cases to `TODO.md` after implementation. Run them and record pass/fail before marking the feature complete.
4. **Clean up** — update stale docs, delete `TODO.md`, validate `docs/TechnicalDesign.md` reflects the new architecture.

## Pre-Commit / Pre-PR Checklist

Before committing or opening a PR, verify changes abide by `CONTRIBUTING.md`:

- Unity-independent domain classes have NUnit test coverage in `Assets/Tests/EditMode/`.
- UI changes (UXML/USS) are reflected in the PlayMode layout tests under `Assets/Tests/PlayMode/UILayout/`.
- `docs/TechnicalDesign.md` is updated if architecture or class structure changed.
- `docs/TODO.md` is deleted before the PR is merge-ready.
- No doc inconsistencies introduced.
- GitHub releases use `.github/release_template.md`. Title format: `v{x.y} — {Short descriptive title}`. Sections: "New features", "Bug fixes", "Performance", "Infrastructure" — include only what applies.

## Release Flow

Two environments, gated:

- **Staging** (`https://staging.arrow-thing.com` + `https://api-staging.arrow-thing.com`) auto-deploys on every push to `main` via `.github/workflows/deploy-staging.yml`.
- **Production** (`https://arrow-thing.com` + `https://api.arrow-thing.com`) deploys only when a GitHub release is published, via `deploy.yml` + `deploy-server.yml`.

Do not tag a prod release until the full `docs/ReleaseChecklist.md` passes on staging. The co-op v2.0 launch bypassed staging and broke in prod for a day — the checklist exists so that cannot happen again.

## Unity Editor Configuration

When a problem can be solved by assigning a reference, toggling a setting, or configuring an asset in the Unity Editor inspector (assigning an `InputActionAsset` to a `SerializeField`, adding a preset to the Preset Manager, changing texture import settings), tell the user to do it manually rather than writing code workarounds or editing `.unity`/`.asset` files. Unity scene and asset YAML is fragile — prefer editor-driven configuration over programmatic hacks.

## Key Design Rules

- Arrow minimum length: 2 cells. No hard maximum.
- Board occupancy is exclusive — one arrow per cell.
- Seeded RNG for reproducible boards.
- Replay system is event-log driven (JSON).
- Unity's C# is version 9.0 — avoid C# 12+ features like collection expressions.
- C# nullable annotations are not used (no `csc.rsp`). Reference types are nullable by default.
