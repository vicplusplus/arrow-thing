# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

**Arrow Thing** — a minimalist speed-clearing puzzle game (Unity 2D URP). Players tap arrows on a grid to clear them; an arrow is clearable only when the ray extending forward from its head to the board boundary contains no other arrow body cells. The dependency graph between arrows must be acyclic (DAG) for a board to be solvable.

The game is free and open-source (MIT). Primary distribution is WebGL on Cloudflare Pages (https://arrow-thing.com/), deployed automatically via CD pipeline on published release.

**Current status**: Active development. Playable at https://arrow-thing.com/. See `docs/OnlineRoadmap.md` for the broader plan.

Docs: `docs/GDD.md` (game design), `docs/TechnicalDesign.md` (architecture — single source of truth for all technical decisions), `docs/BoardGeneration.md` (generator algorithm), `docs/OnlineRoadmap.md` (planned features). See **Feature Workflow** below for how `docs/TODO.md` is used during feature development.

## Architecture

The codebase is split into two layers:

- **Domain layer** (`Assets/Scripts/Domain/`) — Unity-independent pure C#. Contains board state, arrow rules, clearability logic, and generation. Must be testable without Unity runtime.
- **Unity adapter layer** — input handling, rendering, animation, scene wiring. Translates player actions into domain operations and reflects resulting state. Should not own gameplay rules. Unity is used for graphics only.

The board interaction flow: `BoardGeneration` fills `Board` → Unity renders it → player selects arrow → Unity queries `Board.IsClearable` → Unity plays feedback.

View layer scripts live in `Assets/Scripts/View/`:

- **`MainMenuController`** — drives main menu UI (UI Toolkit). Extends `NavigableScene`. Play, Continue, Settings, Leaderboard, and Quit buttons. Desktop-only quit button with confirmation modal. Shows "Continue" button when a saved game exists. Board size selection is in a separate scene (`Solo Size Select`).
- **`SoloSizeSelectController`** — scene entry point for the board-size selection screen. Extends `NavigableScene`. Preset grid (Small/Medium/Large/XLarge/Custom) with responsive layout detection via `GeometryChangedEvent`. Custom panel with width/height `SnapSlider`s. Back and trophy (leaderboard) buttons. Restores previous selection from `GameSettings` on load.
- **`GameController`** — scene entry point. Orchestrated by `GenerateAndSetup` coroutine which delegates to focused helper methods. Creates `Board`, runs generation or snapshot restore, spawns `BoardView` with incremental arrow display, wires `CameraController`, `InputHandler`, and `VictoryController`. Shows loading overlay with progress bar during generation/restore; cancel button opens confirmation modal. Loading overlay rendering decoupled from work (Update-driven). Reads from `GameSettings` when coming from menu; uses inspector fields otherwise.
- **`InputHandler`** — unified PC/mobile input via Unity Input System. Left-click/touch is disambiguated into tap (select arrow) vs drag (pan camera) by a configurable screen-space distance threshold (set on `GameController`, passed via `Init`). Scroll wheel and pinch-to-zoom for camera zoom. `SetInputEnabled` suppresses all input during the victory sequence. Records all tap events to `ReplayRecorder`; fires `onArrowCleared` callback for autosave. Keyboard shortcuts (R reset, T trail, Space click-hovered, S save) via `KeybindManager` actions.
- **`CameraController`** — orthographic camera with `Pan`/`Zoom`/`PinchZoom`/`ZoomToFit` methods. Fits to board on init; max zoom derived from initial fit. Clamped to board bounds.
- **`GameTimerView`** — drives a `GameTimer` each frame and updates the HUD timer label. Grey countdown during inspection (turns red near zero), white count-up during solving, precise millisecond display on finish.
- **`VictoryController`** — handles board-cleared sequence: zoom-to-fit → grid fade-out → victory popup with randomized message, final solve time, Play Again / Menu / View Leaderboard buttons. Detects personal best (gold timer styling). Submits score to server via `ScoreSubmitter` during animation; shows toast with retry on failure. Records result to `LeaderboardManager`. Input is disabled for the entire sequence. Font auto-scales for long messages.
- **`BoardView`** — owns `Dictionary<Arrow, ArrowView>`. Supports incremental arrow spawning via `AddArrowView`/`ApplyColoring` (used during generation/restore) or batch spawning via `Init(spawnArrows: true)`. `RemoveArrowView` removes without animation (resume/seek). `ClearArrowAnimated` plays pull-out animation (replay viewer). `TryClearArrow` checks clearability, returns `ClearResult`, removes or flashes reject. Fires `LastArrowClearing` and `BoardCleared` events. `UpdateClearableHighlights`/`ClearAllHighlights` manage electric cyan tint for replay viewer. `KeepTrailAfterClear` property (from Settings) suppresses trail auto-hide on clear.
- **`BoardSetupHelper`** — static utility extracted from `GameController` for reuse in `ReplayViewController`. Creates board+view, sets up camera, restores board from snapshot with frame-budget-aware yielding.
- **`BoardGridRenderer`** — renders background dot grid as a single tiling quad. `FadeOut` coroutine fades to transparent.
- **`ArrowView`** — procedural mesh body + arrowhead child GameObject. Reject flash, pull-out animation, and bump animation. `SetHighlight(bool)` applies/removes electric cyan tint for clearable highlighting.
- **`ArrowMeshBuilder`** — static builder for polyline body mesh with arc-length UV windowing.
- **`VisualSettings`** — `ScriptableObject` with colors, widths, animation curves, and durations.
- **`BoardCoords`** — static coordinate mapping (cell ↔ world space).
- **`SnapSlider`** — reusable slider row: custom track+handle, value label, +/- buttons, optional lock button (snap-to-grid toggle). Used for custom board-size pickers and settings sliders. `KeyboardStep(dir, useSmallStep)` for arrow key control. Focus ring overlay (`snap-slider__focus-ring`) is absolutely positioned inside the track for zero layout impact; uses `CustomFocusVisual` flag to opt out of `kb-focusable` border.
- **`SaveManager`** — static class (view layer). Saves/loads/deletes the in-progress game JSON at `persistentDataPath/savegame.json`. `LoadAsync` runs file I/O on a background thread (synchronous fallback on WebGL). Auto-deletes on corruption.
- **`LeaderboardManager`** — singleton (view layer), auto-bootstraps via `RuntimeInitializeOnLoadMethod`, persists across scenes via `DontDestroyOnLoad`. Wraps `LeaderboardStore` with file I/O: index at `leaderboard.json`, replays as GZip-compressed JSON at `replays/{gameId}.json.gz`. `RecordResult`, `LoadReplay`, `IsPersonalBest`, `SetFavorite`, `RemoveEntry`.
- **`LeaderboardScreenController`** — scene entry point for the Leaderboard scene. 5 size tabs, 3 sort modes, Local/Global toggle, scrollable entry list with context menu, favorite toggle, replay launch, auto-scroll via `GameSettings.LeaderboardFocusGameId`. Global tab fetches from server, shows player panel with rank context, refresh button, and replay playback (decompresses gzip-base64 snapshots for top-50, regenerates from seed for others).
- **`ReplayViewController`** — scene entry point for the Replay scene. Restores board from snapshot or regenerates from seed (for non-top-50 global replays without snapshot). Drives frame-based playback via `ReplayPlayer`. Supports seek, speed cycling, play/pause, controls bar toggle, and clearable highlighting. Saves regenerated snapshots locally via `LeaderboardManager.RecordResult` for instant subsequent loads.
- **`SettingsController`** — singleton (`DontDestroyOnLoad`) settings panel. Works in any scene. Wires drag threshold/zoom speed sliders, keep-trail toggle, theme dropdown, icon sidebar nav, keybinds section (`KeybindSettingsSection`), clear-scores and external-link confirmation modals. Two-column keyboard navigation: tabs (Left) ↔ content (Right), with dynamic rebuild on account form changes. Saves/restores `FocusNavigator.Active` and `KeybindManager.ActiveContext` across open/close. `JustClosed` flag prevents Escape from propagating to the underlying scene on the same frame.
- **`ThemeManager`** — static class bootstrapped at `BeforeSceneLoad`. Loads `ThemeRegistry` from Resources, restores saved theme from PlayerPrefs. `Apply(VisualSettings)` fires `ThemeChanged` for runtime theme switching.
- **`ThemeRegistry`** — `ScriptableObject` at `Resources/ThemeRegistry`. Lists all available `VisualSettings` theme assets and designates a default.
- **`UIThemeApplier`** — `[RequireComponent(typeof(UIDocument))]` MonoBehaviour. Subscribes to `ThemeManager.ThemeChanged` and hot-swaps the active USS theme stylesheet. Attach to every UIDocument GameObject.
- **`CustomDropdown`** — custom UI Toolkit dropdown that builds its own popup injected into `panel.visualTree`. Keyboard navigation via `PopupKeyboardNav` (`UpdateKeyboard()` called by owning controller). Used for theme selector.
- **`ConfirmModal`** — reusable UI Toolkit wrapper for confirm/cancel modals. Keyboard navigation via `FocusNavigator.PushModal`/`PopModal` — default focus on Cancel, Left/Right toggles, Enter activates, Escape cancels. Optional `isDismissable` adds X button with separate `Dismissed` event (Escape dismisses instead of cancelling). Used by all confirmation dialogs.
- **`EditableLabel`** — inline-edit UI component: readonly TextField that switches to editable on Enter (keyboard) or click (pointer). Auto-selects text on edit start. Enter commits, Escape reverts. Generation counter prevents stale `FocusOutEvent` races. Sets `KeybindManager.TextFieldFocused` during editing.
- **`LabeledField`** — labeled TextField wrapper with bold label above input. `OnFocused`/`OnBlurred` callbacks auto-focus the TextField when keyboard-navigated. `OnSubmit` callback for Enter-to-submit form flow. Sets `KeybindManager.TextFieldFocused` during editing. Used for `AccountManager` form fields.
- **`ExternalLinks`** — static class for WebGL-safe URL opening with confirmation modal. Raises `LinkRequested` event on WebGL.
- **`AccountManager`** — manages account forms in the settings panel: login, register, verify, forgot/reset password, change email/password, display name editing.
- **`ApiClient`** — HTTP client wrapper for server API. Attaches JWT, handles errors, stores auth state in PlayerPrefs. Auth endpoints (register/login/verify/me/display name/forgot password/reset password/change email/change password) plus leaderboard endpoints (submit score, get leaderboard by size and all, get player entry, get replay).
- **`ScoreSubmitter`** — static helper for score submission. `TrySubmitAsync(ReplayData)` checks login state, serializes, calls `ApiClient.SubmitScoreAsync`, returns response or null on failure.
- **`TapIndicator`** / **`TapIndicatorPool`** — expanding/fading ring indicators shown during replay playback. Pool of 10; procedural ring sprite (no asset file). White for clears, red for rejects.
- **`NavigableScene`** — abstract `MonoBehaviour` base class for scene controllers with keyboard navigation. Handles `OnEnable` lifecycle (UIDocument root retrieval, `BuildUI`/`BuildNavGraph` hooks), `Update` guards (null checks, `SettingsController.IsOpen`, `PreUpdate` hook), Cancel handling with `ConsumesCancel`/`JustClosed`. `RebuildNavigator(preserveFocus)` helper for dynamic UI changes. Static `ShouldHandleCancel(nav)` for controllers that don't inherit.
- **`FocusNavigator`** — graph-based keyboard focus navigation for UI Toolkit. Manages a list of `FocusItem`s with a directed navigation graph (`Link`/`LinkBidi`/`LinkChain`/`LinkRow`/`LinkBreak`). `kb-focusable`/`kb-focused` CSS classes for visual focus ring. DAS repeaters for held keys. Modal push/pop stack for `ConfirmModal` overlay. `WasKeyboardActive` carries ring state across scene transitions. Suppresses Unity's built-in `NavigationMoveEvent`/`NavigationSubmitEvent`/`NavigationCancelEvent` in TrickleDown.
- **`KeybindManager`** — singleton (`DontDestroyOnLoad`, `RuntimeInitializeOnLoadMethod`). Creates its own `InputActionAsset` at runtime with Navigation (always active), Shortcuts_ModeSelect, Shortcuts_Gameplay, and Shortcuts_Leaderboard action maps. Context-based map enabling via `ActiveContext`. `TextFieldFocused` suppresses shortcuts during text input. Binding override persistence via `PlayerPrefs`. Interactive rebinding with conflict detection. Disables Unity's built-in EventSystem navigation.
- **`SceneNav`** — static scene navigation stack. Every transition fully unloads the previous scene and loads the target fresh (single-mode). `Push(scene)` remembers the current scene on a stack and loads the target. `Pop()` loads the previous scene from the stack. `Replace(scene)` swaps current (for Play Again / Quick Reset). DontDestroyOnLoad singletons survive all transitions.
- **`DASRepeater`** — Delayed Auto Shift utility. Fires once on initial press, waits `initialDelay` (0.4s default), then repeats at `repeatInterval` (0.05s default). `WasInitialPress` distinguishes first press from repeats (used by `LinkBreak`). `Suppress()` absorbs held state without firing.
- **`PopupKeyboardNav`** — reusable keyboard navigation for popup menus (dropdowns, context menus). Up/Down highlighting, Enter selection, Escape/Left dismissal via `KeybindManager` actions. Used by `CustomDropdown` and leaderboard context menu.
- **`KeybindSettingsSection`** — builds the keybind rebinding UI dynamically. Each action shown as `[Label] [Key Button] [Reset Button]`. Uses `InputSystem.PerformInteractiveRebinding()` for key capture. Conflict detection within same-context actions. `GetFocusItems()`/`LinkNavigation()` for 2×N grid keyboard nav.

## Core Types (`Assets/Scripts/Domain/Models/`)

- **`Cell`** — immutable `(X, Y)` value struct with `IEquatable<Cell>`. Y increases **upward** (Unity convention): `Direction.Up → dy = +1`, `Direction.Down → dy = -1`.
- **`Arrow`** — immutable ordered list of `Cell`s. `Cells[0]` is the head; `HeadDirection` is derived from the vector `Cells[0]→Cells[1]` and points **opposite** to that first segment (e.g., if next is to the right of head, the arrow faces Left).
- **`Board`** — mutable container. Arrows are private; mutate only via `AddArrow`/`RemoveArrow`. `Arrows` is exposed as `IReadOnlyList<Arrow>`. Owns `Arrow[,] _occupancy` and a dependency graph (`_dependsOn`, `_dependedOnBy`), both maintained atomically in `AddArrow`/`RemoveArrow`. `GetArrowAt(Cell)` returns the arrow at a cell (or null). `IsClearable(Arrow)` returns true when the arrow's dependency set is empty (O(1)). `IsInRay` is a public static helper for ray geometry. `InitializeForGeneration()` creates the candidate pool for generation (not needed for deserialized boards). `RestoreArrowsIncremental` coroutine restores a saved board from a snapshot in two phases (placement + dependency graph), yielding for progress reporting.

- **`GameSettings`** — static class holding board parameters chosen in the menu (`Width`, `Height`, `MaxArrowLength`) and `PlayerPrefs` key constants for persisted settings (drag threshold, zoom speed, arrow coloring, input binding overrides, keep-trail-after-clear). `IsSet` flag tells `GameController` whether to use menu values or inspector defaults. Also carries resume state (`IsResuming`, `ResumeData`), replay viewer state (`IsReplaying`, `ReplaySource`), and `LeaderboardFocusGameId` for auto-scroll after a game completes. Scene transitions use `SceneNav` instead of passing return-scene state through `GameSettings`.

Model classes are intentionally minimal and self-contained. Generation logic lives in `BoardGeneration`; clearability and dependency tracking are on `Board` since they're direct graph queries.

## Board Generation (`Assets/Scripts/Domain/BoardGeneration.cs`)

Static class, purely algorithmic — all persistent state lives on `Board`. Key design points:

- **Candidate pool** — owned by `Board`, initialized via `InitializeForGeneration()`. `CreateInitialArrowHeads` populates the candidate list. Stale candidates (head/next cell occupied) are pruned lazily via swap-and-pop in `TryGenerateArrow`. They are also removed when the 2-cell form causes a cycle, or when the greedy walk produces a tail shorter than `MinArrowLength` (2).
- **Tail construction** — `GreedyWalk` performs a linear-time random walk from `[head, next]`, using a pooled `bool[,]` visited grid and reusable path list from `GenerationContext`. The candidate's forward ray cells are pre-marked in `visited` to eliminate per-step `IsInRay` checks. At each step, the 4 cardinal directions are Fisher-Yates shuffled and the first valid neighbor (in bounds, not visited, not occupied, not cycle-causing) is taken. No backtracking — the walk stops when it reaches `targetLength` or hits a dead end.
- **Cycle detection** — uses bitset-based reachability with early abort. `ComputeForwardDeps` walks the candidate's ray to find all arrows it would depend on, storing them in a `ulong[]` bitset. If no forward deps exist, cycle detection is skipped entirely. If all forward deps are leaves (`_hasAnyDeps` check), BFS is skipped and the reachable set is just the forward deps. Otherwise, `ComputeReachableSetEarlyAbort` does BFS through `Board._depsBitsFlat` using bitwise operations (processing 64 arrows per word), checking each newly discovered arrow inline via flat geometry arrays (`_genHeadX`, `_genHeadY`, `_genDir`) and aborting immediately if a cycle is found. All bitsets and the BFS frontier are pooled in `GenerationContext` to eliminate per-candidate allocations.

## Testing

Tests use Unity Test Framework (NUnit) in `Assets/Tests/EditMode/`. Run via Unity's **Test Runner** window (Window > General > Test Runner, EditMode tab). Performance tests are marked `[Explicit]` and only run when manually selected. Coverage: head-direction derivation, `GetDirectionStep`, `Board` mutation/bounds, generation correctness, determinism under fixed seeds, no-overlap, min-length enforcement, no-tail-in-own-ray, full solvability verification (50 seeds + counterexample), external AddArrow compatibility, and a 100-iteration timing gate; leaderboard store (add/get/sort/cap/favorites/personal best/neighbor entries/serialization); replay player (advance/seek/speed/boundary conditions). Explicit perf tests include multi-seed solvability stress tests (500×10x10, 100×20x20, 20×50x50). PlayMode UI layout tests cover 21 UI states across 5 aspect ratios (105 test cases) in `Assets/Tests/PlayMode/UILayout/` (split across `MainMenuLayoutTests`, `GameHudLayoutTests`, `VictoryLayoutTests`, `LeaderboardLayoutTests`, `ReplayHudLayoutTests`). `NavigationCoverageTests` uses state-based validation: each scene declares its UI states (loading, playing, modal-open, etc.) with expected navigable and background buttons; per-state tests verify visible buttons match the declaration exactly, per-scene tests verify every UXML button is navigable in at least one state. Adding a button to UXML without adding it to a state declaration fails the test. `CSSResolutionTests` verifies that every CSS hidden class (`screen--hidden`, `modal--hidden`, `lb--hidden`, `victory--hidden`) actually resolves to `display: none` in each document that uses it, and that responsive CSS selectors (e.g., leaderboard compact mode) apply correctly. `SceneNavStackTests` (EditMode) covers scene transition flows — every real user path through the scene graph is modeled and verified. Additional PlayMode tests: `ApiClientTests`, `GameTimerViewTests`. Server integration tests (xUnit) in `server/ArrowThing.Server.Tests/`. Unity C# is version 9.0 — avoid C# 12+ features like collection expressions.

## Feature Workflow

New features follow a three-phase workflow:

1. **Design** — Create `docs/TODO.md` with the feature design, implementation plan, and open questions. Resolve open questions before moving to implementation. When a `TODO.md` exists, treat it as the authoritative task list for the current feature. The plan must include a testing step — both automated tests for domain classes (per `CONTRIBUTING.md`) and manual test cases for user-facing behavior.
2. **Implement** — Build the feature against the plan in `TODO.md`. Do not delete or simplify `TODO.md` mid-feature; it captures design decisions and context that inform implementation.
3. **Test** — Add manual test cases to `TODO.md` after implementation. Run them and record pass/fail results before marking the feature as complete.
4. **Clean up** — Update stale documentation, delete `TODO.md`, validate `docs/TechnicalDesign.md` reflects the current architecture. The TDD is the single source of truth for all technical decisions.

## Pre-Commit / Pre-PR Checklist

Before committing or opening a PR, verify changes abide by `CONTRIBUTING.md`:

- Unity-independent domain classes have NUnit test coverage in `Assets/Tests/EditMode/`.
- UI changes (UXML/USS) are reflected in PlayMode layout tests in `Assets/Tests/PlayMode/UILayout/` — add new elements to the relevant `AssertElements` call in the appropriate test class.
- `docs/TechnicalDesign.md` is updated if architecture or class structure changed.
- `docs/TODO.md` is deleted before the PR is merge-ready.
- No docs inconsistencies introduced.
- GitHub releases use the template in `.github/release_template.md`. Title format: `v{x.y} — {Short descriptive title}`. Use section names like "New features", "Bug fixes", "Performance", "Infrastructure". Only include sections that apply.

## Unity Editor Configuration

When a problem can be solved by assigning a reference, toggling a setting, or configuring an asset in the Unity Editor inspector (e.g., assigning an `InputActionAsset` to a `SerializeField`, adding a preset to the Preset Manager, changing texture import settings), tell the user to do it manually rather than writing code workarounds or trying to edit `.unity`/`.asset` files. Unity scene and asset files are fragile YAML — prefer editor-driven configuration over programmatic hacks.

## Key Design Rules

- Arrow minimum length: 2 cells. No hard maximum; practical caps are per-mode tuning variables.
- Board occupancy is exclusive — one arrow per cell.
- Seeded RNG must be supported for reproducible boards.
- Replay system is event-log driven (JSON format).
- C# nullable annotations are not used (no `csc.rsp`). Reference types are nullable by default.
