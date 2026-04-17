# Arrow Thing - Technical Design Document

## Purpose

Capture technical design decisions for architecture, domain model structure, and rules implementation.

This document is the implementation-facing counterpart to [`GDD.md`](GDD.md).

## Goals

- Keep gameplay rules deterministic and testable.
- Isolate Unity-independent domain logic from Unity scene/view code.
- Make multiplayer/server-authoritative evolution feasible without rewriting core rules.

## Related Docs

- [`GDD.md`](GDD.md): game design goals and player-facing behavior.
- [`BoardGeneration.md`](BoardGeneration.md): generator algorithm, dependency graph maintenance, and cycle detection.
- [`OnlineRoadmap.md`](OnlineRoadmap.md): planned features (server, leaderboards, replays, accounts).
- [`AntiCheatDesign.md`](AntiCheatDesign.md): design history and PR-level details for score integrity work.

## Architecture Overview

- Domain layer (Unity-independent):
  - Location: `Assets/Scripts/Domain/`
  - Contains board state, arrow data, and generation logic.
  - Must be testable without Unity runtime dependencies (tests use Unity Test Framework / NUnit in `Assets/Tests/EditMode/`).
- Unity adapter layer (Unity-dependent):
  - Input handling, rendering, animation, scene wiring, and UI.
  - Should translate user actions to domain operations and reflect resulting state.
  - Should avoid owning gameplay rules.

## Core Types and Responsibilities

### `Cell` (`readonly struct`)

- Immutable value type with `X`, `Y`. Y increases upward.
- Implements `IEquatable<Cell>` for use in `HashSet<Cell>` and `Dictionary` keying.

### `Arrow.Direction` (`enum`)

- Values: `Up`, `Right`, `Down`, `Left`.
- Nested inside `Arrow`. Used for ray traversal and cycle detection.

### `Arrow` (`sealed class`)

- Represents one arrow as an ordered list of contiguous cells.
- Invariant: at least 2 cells.
- `HeadCell` is `Cells[0]`. `HeadDirection` is derived from the vector `Cells[0] → Cells[1]` and points **opposite** to that segment.
- `GetDirectionStep(Direction)` converts a direction to a `(dx, dy)` step for ray traversal.

### `Board` (`sealed class`)

- Grid dimensions (`Width`, `Height`) and `List<Arrow> Arrows`.
- Owns `Arrow[,] _occupancy`, a dependency graph (`_dependsOn`, `_dependedOnBy`), and a spatial ray index (per-row/per-column lists of arrow heads grouped by direction), all maintained atomically in `AddArrow`/`RemoveArrow`.
- `OccupiedCellCount` — incremental counter maintained by `AddArrow`/`RemoveArrow`. Tracks total occupied cells; available for diagnostics and density calculations.
- `InitialCandidateCount` / `RemainingCandidateCount` — candidate pool size at initialization and current remaining count. Useful for diagnostics and profiling.
- `Contains(Cell)` performs bounds checking.
- `GetArrowAt(Cell)` returns the arrow occupying a cell, or null.
- `IsClearable(Arrow)` returns true when the arrow's dependency set is empty (O(1)).
- `IsInRay(Cell, Cell, Direction)` is a public static helper for ray geometry.
- `InitializeForGeneration()` creates the candidate pool and bitset dependency storage for arrow generation. Allocates `_depsBitsFlat` (flat `ulong[]` for bitset-based BFS), flat geometry arrays (`_genHeadX`, `_genHeadY`, `_genDir`) for early-abort cycle detection, and populates `_availableArrowHeads`. Only needed when generating, not for deserialized boards.
- `AnyArrowWithRayThroughBitset(Cell, ulong[])` — internal query used by cycle detection during generation. Uses the spatial ray index to find arrows whose forward ray crosses a cell, testing membership via O(1) bit-check against a `ulong[]` bitset instead of `HashSet.Contains`.
- `RestoreArrowsIncremental(IReadOnlyList<Arrow>)` — coroutine for restoring a saved board from a snapshot. Phase 1 places arrows into occupancy and ray index (yielding after each for progress reporting). Phase 2 builds the dependency graph in one forward-ray pass (yielding after each arrow). Much faster than calling `AddArrow` individually because it avoids the per-arrow reverse-dependency scan.

### `GameTimer` (`sealed class`)

- Two-phase timer: inspection countdown followed by solve timer. Pure C# — no Unity dependency.
- Phases: `Inspection → Solving → Finished`. Driven by `Tick(double current)` for display updates.
- `StartSolve(current)` transitions from inspection to solving. `Finish(current)` ends the solve.
- `Resume(current, priorElapsed)` skips inspection and restores the timer to a previously saved solve-elapsed offset, used when loading a saved game.
- Display during play uses frame time (`Time.timeAsDouble`). Final precise time uses input-event timestamps (via `InputAction.canceled` callback) to avoid frame-boundary imprecision.
- Fires `PhaseChanged` event on transitions.

### `ReplayEvent` (`sealed class`)

- One entry in the save/replay event log. Fields vary by event type; unused fields are omitted from JSON.
- `seq` — monotonically increasing, defines event order (timestamps can tie at e.g. `start_solve + clear`).
- `type` — string constant from `ReplayEventType` (e.g. `"clear"`, `"session_leave"`).
- `posX`, `posY` — nullable world-space tap position (for `clear`, `reject`; omitted from JSON for other event types via Newtonsoft `NullValueHandling.Ignore`). Cell derived via `BoardCoords.WorldToCell`.
- `timestamp` — ISO 8601 UTC string. Present on all events. Solve-relative timing is derived by subtracting the `start_solve` timestamp, excluding `session_leave`→`session_rejoin` gaps.

### `ReplayEventType` (`static class`)

- String constants for all event types: `session_start`, `session_leave`, `session_rejoin`, `start_solve`, `clear`, `reject`, `end_solve`.
- Uses strings (not enum) for human-readable JSON serialization.

### `ReplayData` (`sealed class`)

- Full save/replay record for one game session.
- Contains: `version` (replay schema version — field defaults to 1; `ReplayRecorder.ToReplayData()` sets it to the current schema version for new replays, currently `4`), `gameId` (UUID), `seed`, board dimensions, `inspectionDuration`, `gameVersion` (application version at recording time, v3+), `boardSnapshot` (initial arrow configuration — all arrows before any clears), `List<ReplayEvent> events`, `finalTime` (-1 = in-progress).
- `version` history: v1 = initial; v2 = added `boardSnapshot`; v3 = added `gameVersion`; v4 = post-RNG rewrite (replays from <v4 clients cannot be regenerated deterministically on the current server and are rejected up-front by `ReplayVersionPolicy`). Bump this whenever a change to board generation, RNG, or verification semantics makes older clients' replays unverifiable — never reuse an old value.
- `boardSnapshot` — each inner list is one arrow's cells in head-to-tail order. On resume, the board is restored from this snapshot and clear events are replayed. Null for v1 legacy saves (falls back to seed-based regeneration).
- `ComputedSolveElapsed` — derived property that sums active solve intervals from event timestamps, excluding `session_leave`→`session_rejoin` gaps. Used by `GameTimer.Resume` to restore the timer.
- Serializes to JSON via `Newtonsoft.Json`. Stored at `Application.persistentDataPath/savegame.json`.

### `ReplayRecorder` (`sealed class`)

- Accumulates `ReplayEvent`s during play, auto-increments `seq`.
- Constructor overload accepts prior events + `nextSeq` for resuming a saved game.
- `ToReplayData(...)` returns a snapshot (copy) of all accumulated events as a `ReplayData`.
- Pure C# — no Unity dependency.

### `ReplayVerifier` (`static class`)

- Verifies a completed replay by regenerating the board from seed, comparing against the snapshot, and simulating all clear events.
- Algorithm: (1) regenerate board from seed+params, compare to snapshot; (2) walk clear events, resolve cell, check clearable, remove; (3) verify board empty; (4) compute solve time from event timestamps excluding pause gaps.
- Returns `VerificationResult` with `IsValid`, `Reason`, and `VerifiedTime`.
- Pure C# — used on both client (domain layer) and server (via shared `ArrowThing.Domain` project).

### `VerificationResult` (`sealed class`)

- Result of replay verification: `IsValid`, `Reason` (null on success), `VerifiedTime`.
- Factory methods: `Valid(verifiedTime)`, `Invalid(reason)`.

### `ClearResult` (`enum`)

- Return type of `BoardView.TryClearArrow`. Values: `Blocked = 0`, `Cleared`, `ClearedFirst`, `ClearedLast`.
- `Blocked = 0` so all success values are nonzero for easy truthiness-style checks.
- `ClearedFirst`/`ClearedLast` drive timer phase transitions in `InputHandler`.

### `LeaderboardEntry` (`sealed class`)

- One entry in the local leaderboard index. Stored in `leaderboard.json` (without replay data).
- Fields: `gameId` (maps to replay file), `seed`, `boardWidth`, `boardHeight`, `solveTime`, `completedAt` (ISO 8601 UTC), `isFavorite`, `gameVersion`.
- Constructor from `ReplayData` extracts board params, computes solve time, and captures timestamp and game version.

### `LeaderboardStore` (`sealed class`)

- Pure C# leaderboard storage. Manages entries with per-config (50) and global (500) caps.
- Favorited entries are exempt from automatic pruning. When a cap is exceeded, the slowest non-favorited entry is pruned and its `gameId` returned for replay file cleanup.
- `AddEntry`, `GetEntries(w,h)`, `GetAllEntries`, `GetPersonalBest(w,h)`, `GetNeighborEntries(w,h,time,count)`, `SetFavorite`, `RemoveEntry`.
- `SortBy(entries, SortCriterion)` — static sort by `Fastest` (solveTime asc), `Biggest` (area desc), or `Favorites` (favorited first, then solveTime).
- JSON serialization via `Newtonsoft.Json` (`ToJson`/`FromJson`).

### `ReplayPlayer` (`sealed class`)

- Pure C# replay playback engine. Takes `ReplayData`, provides time-based playback with speed control.
- Filters to timed events (clear/reject), computes relative timestamps excluding pauses.
- `Advance(deltaTime)` returns fired events. `SeekTo(normalizedTime)` returns `SeekResult` with `EventsToApply` (forward) and `EventsToUndo` (backward) for incremental board state changes.
- `LeadInSeconds` (0.5s) offsets all event times so early clears are visible. `ExitPaddingSeconds` (1.0s) extends total duration for last arrow animation.
- `DisplayDuration` = `TotalDuration - ExitPaddingSeconds` — used for UI slider/time labels. `NormalizedTime` and `SeekTo` clamp to `DisplayDuration`.
- Speed steps: 0.5×, 1×, 2×, 4×. `CycleSpeed()` cycles through them.
- Tracks `ClearedEventIndices` for backward seek (re-add arrows in reverse order).

### `BoardGeneration` (`static class`)

- Procedurally fills a `Board` with acyclic arrows.
- Public entry points: `FillBoardIncremental(...)` (coroutine, yields once per arrow for caller-driven frame budgeting; post-process compaction merges trivial collinear chains; yields `CompactionMarker` then `FinalizationMarker` between phases) and `GenerateArrows(...)`.
- Stateless — all persistent state (dependency graph, candidate pool) lives on `Board`.
- Tail construction uses `GreedyWalk` — a linear-time random walk with no backtracking. Ray cells pre-marked in visited grid.
- Cycle detection uses an early-abort BFS (`ComputeReachableSetEarlyAbort`) that integrates cycle checks inline: each newly discovered arrow is immediately tested via flat geometry arrays, aborting as soon as a cycle is found rather than computing the full transitive closure first.
- **RNG**: `FillBoardIncremental` accepts an `int seed` and derives all randomness via `PortableRandom` (xorshift32). `System.Random` must not be used in generation or any domain code that requires cross-platform determinism — Mono (Unity) and .NET produce different sequences from the same seed. `System.Random` in domain code is a code smell; `PortableRandom` is the correct choice for any path that affects board layout or replay verification.
- See [`BoardGeneration.md`](BoardGeneration.md) for full algorithm details.

## Rule and Data Invariants

- Cells in an arrow are orthogonally connected.
- Board occupancy is exclusive (one arrow per cell).
- An arrow is clearable only when no occupied cell exists on its forward head ray to the board boundary.
- New arrow placements must not create cyclic clear dependencies.
- Generation must only emit arrows that satisfy the acyclicity invariant.

## Board Interaction Flow (Intended)

1. Generate board state in domain (`BoardGeneration` fills a `Board`).
2. Unity layer renders domain state.
3. Player selects arrow in Unity layer.
4. Unity layer queries a domain rules class for clearability and removes the arrow if valid.
5. Unity layer plays success/failure feedback based on the result.

## View Layer (`Assets/Scripts/View/`)

### Main Menu (`MainMenu` Scene)

- **`MainMenuController`** — extends `NavigableScene`. Drives a nested state-machine menu with four states (`Root`, `Play`, `Singleplayer`, `Multiplayer`), each rendered as a `menu-panel` container toggled via `screen--hidden`. Static `_persistedState` survives scene reloads so returning from Game or Leaderboard restores the correct sub-menu.
  - **Root** — Play (green accent), Settings, Quit (desktop only), GitHub/Discord social links.
  - **Play** — Singleplayer and Multiplayer buttons side by side. Back button returns to Root.
  - **Singleplayer** — board-size preset grid (Small/Medium/Large/XLarge/Custom) with responsive layout detection via `GeometryChangedEvent`, custom width/height `SnapSlider` panel, Start and Continue (when save exists) side by side, Leaderboard trophy button (top-right). Restores previous size selection from `GameSettings`. Folded from the former `SoloSizeSelectController` (removed).
  - **Multiplayer** — Co-op button (disabled, "Coming soon" label). Back button returns to Play.
  - Escape/Backspace pops back one level at each state. Nav graphs rebuilt per state via `RebuildNavigator()`.
- **`SettingsController`** — singleton (`DontDestroyOnLoad`, `RuntimeInitializeOnLoadMethod(AfterSceneLoad)`). Exposes `Open()`, `Close()`, `Toggle()`, `IsOpen`, `JustClosed`. Two-column keyboard navigation: icon sidebar tabs (Account, Gameplay, Keybinds, Data, About) linked Left/Right to content items. Every content item in a section has a Left edge back to its tab. `PreUpdate` hook pauses nav when theme dropdown is open (delegates to `CustomDropdown.UpdateKeyboard`). Saves/restores `FocusNavigator.Active` and `KeybindManager.ActiveContext` across open/close. Rebuilds navigator on account form changes (`AccountManager.FormChanged`). `KeybindSettingsSection` builds rebindable keybind rows as a 2×N grid. Keep-trail-after-clear toggle in Gameplay section.
- **`GameSettings`** (static class, domain layer) — holds `Width`, `Height`, `MaxArrowLength`, `IsSet`, `IsResuming`, and `ResumeData`. Also holds `PlayerPrefs` key constants and defaults for persisted settings (drag threshold, zoom speed, arrow coloring, display name, input binding overrides, keep-trail-after-clear). `DisplayName` (string, in-memory) is loaded from PlayerPrefs at startup by `LeaderboardManager.AutoCreate` and written by `AccountManager`. `GameController` reads from it when `IsSet` is true. `Apply()` sets board params for a new game; `ResumeFromSave()` flags a deferred resume (save loaded later by `GameController`); `SetResumeData(ReplayData)` populates resume data after loading; `Reset()` clears all. Replay viewer support: `IsReplaying`, `ReplaySource` with `StartReplay(replayData)` / `ClearReplay()` methods. Scene transitions use `SceneNav` instead of `ReturnScene` — the stack handles return navigation automatically.
- **`AccountManager`** — manages the account forms embedded in the settings screen. Supports display name editing offline via `EditableLabel` (saved to PlayerPrefs and `GameSettings.DisplayName`). When a server is reachable, also handles login, register, verify code, forgot/reset password, change email, confirm email change, and change password flows. All form fields are `LabeledField` instances. Navigation between forms is managed internally; `MainMenuController` calls `CancelEditing()` on settings close.
- **`SaveManager`** (static class, view layer) — saves/loads/deletes the in-progress game JSON at `Application.persistentDataPath/savegame.json`. Wraps `Newtonsoft.Json` serialization. `LoadAsync` coroutine runs file I/O and deserialization on a background thread (falls back to synchronous on WebGL). Safe: catches I/O exceptions, logs warnings, auto-deletes on corruption.

### Scene Wiring

- **`GameController`** — scene entry point. Orchestrated by `GenerateAndSetup` coroutine which delegates to focused helper methods: `ResolveParameters`, `ResolveHudElements`, `ShowLoading`/`HideLoading`, `CreateBoardAndView`, `SetupCamera`, `GenerateBoard`/`RestoreBoard`, `SetupTimer`, `WireHud`, `WireInput`, `WireVictory`. Creates the `GameTimer` domain model and passes it to both `InputHandler` (for input-precision timestamps) and `VictoryController` (for final time display). Creates a `ReplayRecorder` and passes it to `InputHandler` to capture all tap events. During multi-frame generation or restore, shows a loading overlay with a progress bar and percentage label; arrows are displayed incrementally as they are placed. The HUD X button opens a cancel confirmation modal during generation (timer and trail toggle are hidden until generation/restore completes). Loading overlay rendering is decoupled from work — `Update()` drives fade and progress bar from shared `_loadProgress` state; work coroutines only set that field. Progress is based on arrow count against an estimated total (see `docs/BoardGeneration.md` § "Loading Progress Heuristic"). Reads board parameters from `GameSettings`; when `IsResuming`, the save file is loaded asynchronously after the loading overlay is visible (deferred resume), then the board is restored from the saved initial snapshot via `Board.RestoreArrowsIncremental` (no generation), clear events are replayed to reconstruct current state, and the timer is restored via `GameTimer.Resume()` using `ReplayData.ComputedSolveElapsed`. **Autosave**: when no other game's save would be overwritten (no save on disk, or resuming the same game), the game autosaves every 10 clears. The X button always opens a modal: "Save game?" with Yes/No/X-close when arrows have been cleared (with a "replace save" warning if a different game's save exists); "Leave game?" with Yes/No when no arrows cleared. Board completion records `end_solve` and deletes the save file.
- **`InputHandler`** — unified PC/mobile input via Unity Input System. Left-click/touch is disambiguated into tap (select arrow) vs drag (pan camera) by a configurable screen-space distance threshold (set on `GameController`, passed via `Init`). Scroll wheel and pinch-to-zoom for camera zoom. Exposes `SetInputEnabled` to suppress all input during the victory sequence. `HandleSelectAndPan` manages the press/drag/release state machine; tap resolution extracted into `HandleTap(Vector2 screenPos)`. On each tap: records `start_solve` (if transitioning from inspection), then `clear` or `reject` to the optional `ReplayRecorder`. On non-final clears, fires an `onArrowCleared` callback (used by `GameController` for autosave). Timer phase transitions driven by input-precision wall-clock timestamps.
- **`CameraController`** — orthographic camera with `Pan`/`Zoom`/`PinchZoom`/`ZoomToFit` methods. Fits to board on init; max zoom is derived from the initial fit (not configurable). Clamped to board bounds. `ZoomToFit` smoothly returns to the initial view with a SmoothStep coroutine. Exposes `CameraChanged` event (fired on every Pan/Zoom/PinchZoom/ZoomToFit frame) and `GetVisibleWorldRect()` for viewport culling consumers.
- **`GameTimerView`** — drives a `GameTimer` each frame and updates the HUD timer label. During inspection: grey whole-second countdown, turns red at a configurable warning threshold. During solving: white whole-second count-up. On finish: precise millisecond display.
- **`VictoryController`** — handles the board-cleared sequence. On last arrow clear, `OnLastArrowClearing` starts the camera zoom-to-fit in parallel with the pull-out animation and fires `ScoreSubmitter.TrySubmitAsync` in the background. After both complete, `OnBoardCleared` triggers grid fade → victory popup with a randomized playful message, final solve time, and Play Again / Menu / View Leaderboard buttons. Records the result to `LeaderboardManager`, detects personal best (gold timer via `victory-time--gold` CSS class). Awaits submission result before showing the popup; on failure shows a low-prominence toast in the top-right of the victory overlay with a Retry button. Font size auto-scales for long messages. Hides the game HUD when the popup appears. Receives a `buildReplayData` delegate from `GameController` to construct the completed `ReplayData` with `finalTime` set.
- **`LeaderboardManager`** (singleton, view layer) — wraps `LeaderboardStore` with file-based persistence. Auto-bootstraps via `RuntimeInitializeOnLoadMethod(BeforeSceneLoad)`, which also loads `GameSettings.DisplayName` from PlayerPrefs so the display name is available even if the settings panel is never opened. Persists across scenes via `DontDestroyOnLoad`. Index stored as `leaderboard.json`; replays stored individually as GZip-compressed JSON at `replays/{gameId}.json.gz` under `Application.persistentDataPath`. `RecordResult(ReplayData)` builds entry, saves index + replay, prunes slowest non-favorited if caps exceeded. `LoadReplay(gameId)` tries GZip first, falls back to plain JSON. `IsPersonalBest`, `SetFavorite`, `RemoveEntry` delegate to store + save.
- **`LeaderboardScreenController`** — scene entry point for the Leaderboard scene. Manages 5 size tabs (Small/Medium/Large/XLarge/All), 3 sort buttons (Fastest/Biggest/Favorites), Local/Global scope toggle, scrollable entry list with context menu (delete, favorite toggle, replay launch — favorite and play are inline on wide screens, overflow to context menu on narrow/compact screens), and auto-scroll from victory screen via `GameSettings.LeaderboardFocusGameId`. Tab labels switch to abbreviated form (S/M/L/XL/All) on narrow viewports via `GeometryChangedEvent` to prevent the refresh button from clipping. On the All tab, Fastest is hidden (small boards always win) and Biggest is shown; on size tabs, the reverse. "Biggest" sort secondary tiebreaker: area → time → date. Top-3 entries in current sort receive gold/silver/bronze medal tints (suppressed in Favorites sort). Context menu flips above the row when it would overflow the bottom. Drag-to-scroll on content area. Context menu auto-dismisses on scroll. Global tab fetches top-50 entries and player entry from the server in parallel; stale results discarded if user switches away from Global view before response arrives. Player panel below the list shows rank context ("Your best: #N of T"), clickable login link (opens Settings), or descriptive error messages per HTTP status. Refresh button re-fetches. Global replays launch via `ApiClient.GetReplayAsync`. Compact mode (`lb-screen--compact`) activates below 500px panel width, hiding inline favorite/play buttons (available via context menu). Entry row icon buttons share a `.lb-row-btn` base class.
- **`ScoreSubmitter`** — static helper class for submitting scores. `TrySubmitAsync(ReplayData)` checks login state, serializes replay, calls `ApiClient.SubmitScoreAsync`. Returns a `SubmitResult` with either success data or a descriptive user-facing error message (maps HTTP status codes: 0 → no connection, 401 → session expired, 413 → file too large, 429 → rate limited, 500+ → server error; verification failures include the server's reason).
- **`BoardSetupHelper`** (static utility) — extracted shared logic from `GameController` for reuse in `ReplayViewController`. Static methods: `CreateBoardAndView(width, height, visualSettings)` returns `(Board, BoardView)`; `SetupCamera(camera, board, zoomSpeed?)` returns `CameraController`; `RestoreBoardFromSnapshot(board, boardView, snapshot, frameBudgetMs)` coroutine restores arrows with frame-budget-aware yielding.
- **`ReplayViewController`** — scene entry point for the Replay scene. Restores board from snapshot via `BoardSetupHelper`, creates `ReplayPlayer` for time-based playback. Frame-driven via `Update()`: advances `ReplayPlayer`, executes clear/reject events on `BoardView` (animated pull-out for clears, bump for rejects), spawns tap indicators. Supports seek (pauses during drag, resumes on release via `_wasPlayingBeforeSeek`), speed cycling (0.5x/1x/2x/4x/10x), play/pause, controls bar toggle, and clearable highlighting toggle (shows electric cyan tint + trail lanes on clearable arrows, updated in real-time during playback and seek). Keyboard nav: Space play/pause globally, T toggle highlight, arrow Left/Right seek with DAS on seek handle, vertical nav between exit/seek/play/speed/highlight/toggle buttons. Exit via `SceneNav.Pop()`.
- **`TapIndicator`** — expanding/fading ring MonoBehaviour used during replay playback. `Play(position, color, duration, maxScale, onComplete)` with quadratic alpha fade. Managed by `TapIndicatorPool`.
- **`TapIndicatorPool`** — object pool (size 10) with procedural ring sprite generation at runtime (no asset file needed). `Spawn(worldPos, isReject)` — white rings for clears, red for rejects.

### Keyboard Navigation (`Assets/Scripts/View/Input/`)

- **`NavigableScene`** — abstract `MonoBehaviour` base class for scene controllers with keyboard navigation. Handles `OnEnable` lifecycle (UIDocument root retrieval → `BuildUI` → navigator creation → `BuildNavGraph` → `ActiveContext`). `Update` drives guards (null, settings open), `PreUpdate` hook (return false to skip navigator update), `OnUpdate` for scene keybinds, and Cancel handling with `ConsumesCancel`/`JustClosed`. `RebuildNavigator(preserveFocus)` for dynamic UI changes. Static `ShouldHandleCancel(nav)` for controllers that don't inherit (GameController, ReplayViewController).
- **`FocusNavigator`** — directed-graph keyboard focus navigation for UI Toolkit elements. Items are `FocusItem` structs with `Element`, `OnActivate`, `OnHorizontal`, `OnFocused`/`OnBlurred` callbacks, and a `CustomFocusVisual` flag. Navigation graph built with `Link`/`LinkBidi`/`LinkChain`/`LinkRow`/`LinkBreak`/`ClearLink`. `LinkBreak` edges only fire on initial key press (not DAS repeat) — used at region boundaries (e.g. last leaderboard entry → sort tabs). Visual ring via `kb-focusable`/`kb-focused` CSS classes. Modal push/pop stack for `ConfirmModal` overlays. `WasKeyboardActive` static flag carries ring state across scene transitions. Suppresses Unity's `NavigationMoveEvent`/`NavigationSubmitEvent`/`NavigationCancelEvent` in TrickleDown.
- **`KeybindManager`** — singleton (`DontDestroyOnLoad`, `RuntimeInitializeOnLoadMethod(BeforeSceneLoad)`). Creates its own `InputActionAsset` at runtime — no `.inputactions` file dependency. Action maps: Navigation (always active: Navigate, Submit, Cancel, Tab, ToggleSettings, Point, Select, Zoom), Shortcuts_ModeSelect (OpenLeaderboard), Shortcuts_Gameplay (QuickReset, ToggleTrail, ClickHovered, QuickSave), Shortcuts_Leaderboard (TabSmall–TabAll, ToggleFavorites, SwapGlobal). Context-based map enabling via `ActiveContext`. `TextFieldFocused` suppresses shortcut maps during text input. Binding overrides persisted in PlayerPrefs with stable GUIDs and a version key for migration. Creates an `EventSystem` with `InputSystemUIInputModule` (move/submit/cancel cleared) to prevent Unity's DefaultEventSystem from processing WASD as navigation.
- **`SceneNav`** — static scene stack. Every transition fully unloads the previous scene and loads the target fresh (single-mode). `Push(scene)` remembers the current scene on a stack and loads the target. `Pop()` loads the previous scene from the stack. `Replace(scene)` swaps the current scene without touching the stack. DontDestroyOnLoad singletons survive all transitions.
- **`DASRepeater`** — Delayed Auto Shift: fires once on initial press, waits `initialDelay`, then repeats at `repeatInterval`. `WasInitialPress` distinguishes first press from repeats. `Suppress()` absorbs held state without firing (used after dropdown/modal close).
- **`PopupKeyboardNav`** — reusable keyboard nav for floating popup menus. Up/Down highlighting with DAS, Enter selection, Escape/Left dismissal. Shared by `CustomDropdown` and leaderboard context menu.
- **`KeybindSettingsSection`** — dynamic rebind UI. Each action: `[Label] [Key Button] [Reset Button]`. Uses `PerformInteractiveRebinding()` for capture. Conflict detection within same-context actions. `GetFocusItems()`/`LinkNavigation(nav, start, above, below)` for 2×N grid keyboard nav.

### Board and Arrow Rendering

- **`BoardView`** — owns `Dictionary<Arrow, ArrowView>` (small boards) or `Dictionary<int, BoardChunkRenderer>` (large boards). `Init` accepts a `spawnArrows` flag; when false, arrows must be added incrementally via `AddArrowView` followed by `ApplyColoring` when complete (used during generation/restore for real-time board display). `RemoveArrowView` removes an arrow without animation (used during resume clear replay and seek). `ClearArrowAnimated(arrow)` plays pull-out animation. `TryClearArrow` checks clearability, returns `ClearResult`, and dispatches to `PlayBlockedFeedback` (bumps toward blocker, applies persistent tints) or the clearable path (pull-out animation). `PlayBlockedFeedback` computes the contact arc-length by walking the ray cell-by-cell. Tracks clear count to distinguish `ClearedFirst` / `ClearedLast`. `LastArrowClearing` event fired via `NotifyLastArrowClearing()`. Manages trail highlight state; fires `TrailAutoOff` on successful clear. `UpdateClearableHighlights` / `ClearAllHighlights` for replay viewer. **Viewport culling**: when `Width*Height >= 3600` (60×60), arrows are batched into chunked combined meshes (one `BoardChunkRenderer` per 16×16 cell chunk via `BoardSpatialIndex` math). Each visible chunk renders one combined mesh containing all arrows touching that chunk (bodies + heads, vertex colors per arrow). Individual `ArrowView` instances are only spawned for click interactions (clear / blocked animations) via `PromoteToInteractionView`, rendered above chunks via bumped `sortingOrder`. `ClearPreviousTints` demotes blocked-feedback views back to chunks. `Update()` re-evaluates visible chunks each frame, handling both pan/zoom and arrows added mid-frame during generation. Trails are not rendered in culling mode.
- **`BoardSpatialIndex`** — pure C# bucket-based spatial index (16×16 cells per bucket). `Add` / `Remove` / `QueryRect` for fast rectangle-overlap queries. Used by `BoardView` for chunk grid math. Tested via `BoardSpatialIndexTests` (NUnit EditMode).
- **`BoardChunkRenderer`** — single-chunk combined-mesh renderer (one `MeshFilter` + `MeshRenderer`). `AddArrow` / `RemoveArrow` / `SetArrowColor` / `HideArrow` / `UnhideArrow` mark the chunk dirty; `RebuildIfDirty` rebuilds the combined mesh on the next call (driven from `BoardView.Update()`). Uses a shared runtime `Material` instance with the `ArrowThing/ArrowBodyBatched` shader. Vertex colors are manually converted to linear color space (Unity does not auto-convert vertex colors like material `_Color` uniforms).
- **`ArrowBodyBatched`** shader — vertex-color variant of `ArrowBody.shader` for batched chunk meshes. Reads `COLOR` semantic instead of a per-material `_Color`. `_HighlightStrength` defaults to 0 for a flat solid look (the dome highlight in the original shader does not appear in batched mode); interaction views also disable the highlight via material instance for visual consistency.
- **`BoardGridRenderer`** — renders the background dot grid as a single quad with a tiling texture. UV coordinates are scaled to tile once per cell; uses `Sprites/Default` shader for `_Color` support (fade-out on board clear).
- **`BoardCoords`** — static coordinate mapping between cell indices and world-space positions. Cell (0,0) maps to world origin (bottom-left corner); each cell is 1×1 Unity unit.
- **`ArrowView`** — procedural mesh body + arrowhead child GameObject. Manages reject flash and clear/bump animations. `SetHighlight(bool)` applies/removes electric cyan tint (`#00DFFF`) for clearable highlighting in the replay viewer. Owns a `TrajectoryLine` child GameObject (hidden by default) built from the already-computed extended path — the mesh window `[0, extensionDist]` renders a thin line from the exit point back to the arrow head, making the clearability ray visible to the player. `Reinit` / `Deactivate` exist as remnants of an earlier pool design but are not currently used (interaction views in culling mode are spawned fresh and destroyed on completion).
- **`ArrowMeshBuilder`** — static builder that generates a polyline mesh for the arrow body with arc-length UVs and a sliding visibility window.
- **`VisualSettings`** — `ScriptableObject` with visual tuning parameters: colors, widths, animation curves, and durations. The `themeUIStyleSheet` field (UI Theme header) holds the runtime-injected USS theme file (`ThemeColors.uss` by default). Swapping it via the inspector swaps the entire UI color palette without touching any UXML or C# code.
- **`ThemeManager`** — static class bootstrapped via `RuntimeInitializeOnLoadMethod(BeforeSceneLoad)`. Loads the `ThemeRegistry` ScriptableObject from `Resources/`, populates `Available` (all `VisualSettings` assets in the registry) and restores the saved theme from PlayerPrefs (or falls back to the registry's `defaultTheme`). `Apply(VisualSettings)` sets `Current`, saves the name to PlayerPrefs, and fires `ThemeChanged`. Scene objects subscribe to `ThemeChanged` to react to runtime theme switches.
- **`ThemeRegistry`** — `ScriptableObject` placed at `Resources/ThemeRegistry`. Lists all available `VisualSettings` theme assets and designates one as `defaultTheme`. `ThemeManager` loads this at startup.
- **`CustomDropdown`** — custom UI Toolkit dropdown (no Unity `DropdownField`) that builds its own visual tree. Trigger button uses the `custom-dropdown` CSS class; `Root` VisualElement is exposed for placement. On click, injects a backdrop + popup list directly into `Root.panel.visualTree` so the popup floats above all other UI. `PositionPopup` uses `worldBound` to place the popup below the trigger. Exposes `ValueChanged` event and `SetValueWithoutNotify` for programmatic updates.
- **`UIThemeApplier`** — `[RequireComponent(typeof(UIDocument))]` MonoBehaviour. Subscribes to `ThemeManager.ThemeChanged` in `OnEnable`; removes the previously applied sheet and adds the new `VisualSettings.themeUIStyleSheet` to the UIDocument root. Attach to every UIDocument GameObject. Required so all USS `var(--...)` references resolve, and so the UI palette updates immediately when the player switches themes at runtime.
- **`SnapSlider`** — reusable UI Toolkit slider row: custom track+handle, value label, +/- step buttons, and an optional lock button (pill layout). The lock toggles snap-to-grid mode: when locked, drag snaps in `snapStep` increments; +/- always step by `smallStep`. Track/handle are manually driven (pointer capture) instead of Unity's built-in `Slider`, for reliable drag behavior. Used by `MainMenuController` for custom board-size pickers (snap-to-10, with lock) and for settings sliders (no lock, continuous).
- **`ConfirmModal`** — reusable UI Toolkit wrapper for a confirm/cancel modal element. Constructor takes the root `VisualElement` of the modal plus title, confirm label, cancel label, and optional subtitle/isDanger flag. Exposes `Show()`, `Hide()`, `Confirmed`, `Cancelled` events. All modals in the project (quit, clear scores, external link, logout, cancel generation, leave game) use this class.
- **`EditableLabel`** — inline-edit UI component: shows a Label with an edit icon button; clicking switches to a TextField with save/cancel icons. Used for the display name field in the settings account section.
- **`LabeledField`** — labeled `TextField` wrapper with a bold label above the input. Used to standardize form fields in `AccountManager` (email, password, code, etc.), replacing ad-hoc query-and-wire patterns.
- **`ExternalLinks`** — static class with a `LinkRequested` event and `Open(url)` method. On WebGL, raises the event instead of opening the URL directly; `SettingsController` subscribes to `LinkRequested` and shows a confirmation modal before navigating. On other platforms, calls `Application.OpenURL` immediately.

### UI Stylesheets (`Assets/UI/`)

All UI colors are CSS custom properties (USS variables) defined in `ThemeColors.uss` and injected at runtime by `UIThemeApplier`. Individual screen stylesheets reference `var(--...)` only — no literal colour values.

- **`ThemeColors.uss`** — single source of truth for the entire UI palette (~90 variables). Groups: Text (`--text-white/primary/secondary/heading/dim/danger/success/gold/link`), Surfaces (`--bg-screen/overlay/panel/settings/entry/track/item-hover`), Buttons, Inputs, Navigation tabs/toggles/filters, Presets, Accents (blue/green/teal/danger/info/favorite/warning/progress), Toggle/slider controls, Borders, Scrollbars. Swapping this file (via `VisualSettings.themeUIStyleSheet`) changes the entire UI theme.
- **`Shared.uss`** — shared component styles used across multiple screens: `screen--hidden`, `screen-title`, modal overlay/box/buttons, `confirm-btn--danger`, back-button, icon-button, settings section header, entry row, scrollbar skin, empty-state label, loading overlay. Each screen's USS imports only screen-specific overrides.

### Fonts (`Assets/Fonts/`)

Noto Sans font family (SIL OFL 1.1) provides broad Unicode coverage for display names and lobby names. Font files committed as raw `.ttf`/`.otf`; Font Assets created in-Editor with Dynamic atlas population mode (SDF, runtime-populated).

| File | Coverage |
|------|----------|
| `NotoSans-Regular.ttf` | Latin, Greek, Cyrillic (primary) |
| `NotoSans-Bold.ttf` | Latin, Greek, Cyrillic (bold variant) |
| `NotoSansJP-Regular.otf` | CJK Japanese subset |
| `NotoSansArabic-Regular.ttf` | Arabic |
| `NotoSansHebrew-Regular.ttf` | Hebrew |
| `NotoSansThai-Regular.ttf` | Thai |
| `NotoSansDevanagari-Regular.ttf` | Devanagari |
| `NotoEmoji-Regular.ttf` | Monochrome emoji |

Configuration is via `PanelTextSettings` (not USS). The primary `NotoSans-Regular` Font Asset has a fallback chain containing all other Font Assets. `PanelTextSettings` references the primary Font Asset and is assigned to all three `PanelSettings` assets (main, settings overlay, global toast). No USS or UXML changes required — all UI text inherits the font automatically.

### Arrowhead Separation

The arrowhead is a separate child GameObject with its own material instance, not part of the body mesh:

- Procedural triangle mesh (3 verts) — resolution-independent at any zoom.
- Uses the same `ArrowBody` shader as the body, so the reject flash drives `_FlashT` on both materials in sync.
- During animations, the arrowhead position is set by sampling the path at the window's leading edge. No mesh rebuild needed for the arrowhead.

### Animation System

All animations apply only to the tapped arrow. No other arrow on the board moves during a clear attempt.

#### Arc-Length Windowing

`ArrowMeshBuilder.Build` accepts `windowStart` and `windowEnd` parameters that clip the visible body mesh to a sub-range of the arrow's total arc length. Both parameters advance by the same `slideOffset` each frame, keeping the visible body length constant (the arrow slides along its path without stretching).

This approach is necessary because arrows are polylines with bends — a rigid `transform.position` offset would shift all vertices uniformly, causing bent arrows to move sideways at their middle segments instead of sliding along their own shape.

#### Pull-Out (Clearable Arrow)

- `Board.RemoveArrow` is called immediately before the animation starts, so other arrows become clearable right away.
- The path is extended at init with a synthetic exit point along the head direction to ensure the arrow fully exits the viewport.
- `slideOffset` advances from `0` along the extended path, driven by `clearSlideCurve`. Both window edges move in lockstep.
- Once the arrowhead exits the visible area, `windowEnd` stops and `windowStart` continues (tail-drain), shrinking the visible body to zero. The GameObject is destroyed when `windowStart >= windowEnd`.

#### Bump (Blocked Arrow)

- `Board.GetFirstInRay` finds the blocking arrow. The contact point is the midpoint of the blocker's first ray-intersecting cell.
- **Slide phase**: `slideOffset` advances to `contactArcLength` via `bumpSlideCurve`.
- **Bump phase**: `slideOffset` overshoots slightly past contact and springs back, driven by `bumpCurve`. The reject flash fires at contact.
- **Return phase**: `slideOffset` returns to `0` via `bumpReturnCurve`.
- No domain state changes — the arrow stays on the board throughout.

## Server Auth (`server/ArrowThing.Server/Auth/`)

- **`AuthService`** — all auth operations. Returns `(Response?, StatusCode, Error?)` tuples. Endpoints mapped in `Program.cs`. Methods: `RegisterAsync`, `LoginAsync`, `GetMeAsync`, `UpdateDisplayNameAsync`, `VerifyCodeAsync`, `ResendVerificationAsync`, `ForgotPasswordAsync`, `ResetPasswordAsync`, `ChangePasswordAsync`, `ChangeEmailAsync`, `ConfirmEmailChangeAsync`, `VerifyDeviceAsync`, `LockAccountAsync`, `UnlockAccountAsync`. All email flows use 6-digit codes entered in-app (no browser pages).
- **`JwtHelper`** — HMAC-SHA256 JWT generation (30-day expiry) with `sub` (user ID), `display_name`, and `security_stamp` claims. Validation parameters exposed for middleware.
- **`PasswordHasher`** — static BCrypt hash/verify wrapper.
- **`IEmailService` / `EmailService`** — transactional email via Resend HTTP API. Six methods: verification code, already-registered notification, password reset code, email change code, email change notification, new-device OTP code. API key stored in user secrets (`Resend:ApiKey`).
- **`AuthDtos`** — C# records for all request/response types. JSON property names are camelCase via ASP.NET defaults.
- **`UserDevice`** — table of trusted device fingerprints per user. Each row stores a bcrypt hash of the client's `X-Device-Id` header plus first/last-seen timestamps and the UA string. Rows older than 90 days (`LastSeenAt`) are treated as unknown.

### New-device OTP

On successful password verification, `LoginAsync` looks up the `X-Device-Id` header against the user's `UserDevices` (90-day `LastSeenAt` window, in-memory bcrypt scan — device count per user is expected to be single digits). Unknown device → generate a 6-digit code, email it, persist `DeviceOtpCode` / `DeviceOtpCodeExpiresAt` / `DeviceOtpPendingDeviceIdHash` on the user, return `{ requiresDeviceOtp: true }` without issuing a JWT. Same device → bump `LastSeenAt` and return the normal `{ token, displayName }`.

The pending OTP is bound to the specific device id that requested it, so a concurrent login attempt from a different device can't invalidate Alice's code by triggering a new one. Rate-limited by `LastDeviceOtpEmailAt` with the 5-minute `EmailCooldown`.

`VerifyDeviceAsync` (`POST /api/auth/verify-device`) re-verifies the password (defense in depth — the email code alone can't grant access), checks the OTP hash, and requires the same `X-Device-Id` that requested the code. On success it inserts the `UserDevice` row and issues a JWT. `VerifyCodeAsync` (the post-registration email verification) also auto-trusts the current device so the very first login after registration doesn't hit a second OTP round-trip. The `Phase 1B` migration adds the `UserDevices` table and four OTP columns on `Users`; existing users hit the OTP challenge on their next login.

Client-side, `ApiClient.GetOrCreateDeviceId` generates a 256-bit random token, persists it in `PlayerPrefs` under `auth_device_id`, and attaches it as `X-Device-Id` to every auth request. `AccountManager` reuses the existing verify form for the new-device OTP branch (`_pendingDeviceVerificationPassword` flag steers `OnVerifyCode` to `VerifyDeviceAsync`).

### SecurityStamp Middleware

Registered in `Program.cs` after JWT authentication. On every authenticated request, extracts the `security_stamp` claim from the JWT and compares it to the user's current `SecurityStamp` in the database. Returns 401 if they don't match — this invalidates all existing tokens when the stamp is bumped (e.g., account lock).

### Admin Endpoints

Protected by `X-Admin-Key` header (compared against `Admin:ApiKey` configuration). Not JWT-authenticated — admin operations are server-to-server. Lock/unlock operate by email address.

### Audit Logging

All auth operations are tracked via `AuditLog` records in PostgreSQL. `AuditLogService` dual-writes each event to both the database and structured logs (via `ILogger<AuditLogService>`), so audit data is queryable in both PostgreSQL (via Grafana SQL datasource) and Loki (via log search). 14 event types cover registration, login (success/failure), password changes, email changes, account lock/unlock, session invalidation, and display name updates. Each record captures timestamp, event type, user ID, email, client IP (from `X-Forwarded-For`), and optional detail string.

### Reliability

**Redis is optional at runtime.** `IConnectionMultiplexer` is registered with `AbortOnConnectFail=false`, so a Redis outage at startup doesn't crash the API — the multiplexer reconnects in the background. Surfaces that require Redis gate on `RedisExtensions.IsAvailable` and return `503 { error: "Service temporarily unavailable." }` instead of throwing. Currently gated: score submission (`POST /api/scores`), score status (`GET /api/scores/{id}/status`), lobby create + retry-generation (`POST /api/lobbies`, `POST /api/lobbies/{id}/retry-gen`). Read paths like the leaderboard fall back to Postgres when the cache misses, so they keep working during a Redis outage.

**Global exception middleware** (`ExceptionHandlingMiddleware`, first in the pipeline) stamps every request with an `X-Correlation-Id` header (preferring the client-supplied value if present, otherwise `Guid.NewGuid("N")`) and pushes it onto the Serilog `LogContext`. Unhandled exceptions turn into `500 { error, correlationId }` with the ID echoed in the response header so clients can quote it. Intentional 4xx/5xx responses from endpoints pass through untouched.

**Email sends are tiered.** Critical paths (`SendVerificationCodeAsync` on register/resend, `SendPasswordResetCodeAsync` on forgot/unlock, `SendEmailChangeCodeAsync` on change-email, `SendDeviceOtpCodeAsync` on new-device login) return `503 "Failed to send email. Please try again."` on Resend failure after clearing the persisted code + cooldown timestamp so the user can retry immediately instead of being stuck in the 5-minute window. Non-critical notifications (`SendAlreadyRegisteredEmailAsync`, `SendEmailChangeNotificationAsync`) keep the log-and-swallow behavior because failing them would block a legitimate flow for no security benefit.

### Observability Stack

Structured logging via **Serilog** (console + Grafana Loki push). HTTP request logging via `UseSerilogRequestLogging()`. Metrics via **OpenTelemetry** (ASP.NET Core + .NET runtime instrumentation) exposed at `/metrics` for **Prometheus** scraping. All telemetry flows to **Grafana** (localhost:3000, SSH tunnel access only) which has three auto-provisioned datasources: Loki (logs), Prometheus (metrics), PostgreSQL (direct SQL queries against users and audit tables).

Infrastructure services (`loki`, `prometheus`, `grafana`) run as Docker containers alongside the existing `api`, `worker`, `db`, `redis`, and `nginx` services. None are publicly exposed — Prometheus, Loki, and Redis are internal-only (`expose:`), Grafana binds to `127.0.0.1:3000`. Pre-provisioned dashboards cover server health, admin actions, and score submission flow.

## Score Integrity

### Threat Model

The game's board state is fully observable and deterministic. Clearability is computable, and optimal solve order is trivially derivable — a bot can auto-solve any board instantly. No amount of server-side validation can prove a human was in the loop. The anti-cheat system targets **casual cheaters** (browser console, memory editing, trivial scripts), not sophisticated bots. A determined attacker with domain knowledge can always cheat; this is accepted.

### Trust Boundary

**The client is untrusted.** Every field of a submitted `ReplayData` — `gameId`, `seed`, `boardWidth`, `boardHeight`, event list, event timestamps — must be treated as attacker-controlled and validated or re-derived before it touches the leaderboard. Concretely:

- `VerificationWorker` re-simulates the board from `seed` using `PortableRandom` and rejects any replay whose events don't match. The verified solve time is computed from verified event timestamps, not taken from `ReplayData.ComputedSolveElapsed`.
- The idempotency key for a score submission is `gameId`, guarded by a Redis `SET NX` lock (`verify:lock:{gameId}`) so a client retry cannot enqueue two verification jobs for the same replay.
- Locally-stored replay snapshots (top-50 gzipped on the server, regenerated-from-seed for the rest) exist only for playback UX. They are never trust anchors; the server regenerates deterministically whenever a score's validity is in question.
- JWTs are signed with a Production secret that is length-checked and blocklist-checked at startup (`ValidateProductionSecret` in `Program.cs`). Admin endpoints use a separate `AdminKey` authentication scheme with a constant-time comparison.

### Architecture

Score submission is async. The API performs cheap pre-verification checks synchronously, enqueues the job to Redis, and returns 202. A dedicated `VerificationWorker` process consumes the queue, runs full board regeneration + clear simulation, persists verified scores, and writes results back to Redis for client polling.

```
Client → POST /api/scores → Pre-verify → Redis queue → 202 Accepted
                                                ↓
                                         VerificationWorker
                                         (Board regen + simulate)
                                                ↓
Client ← GET /api/scores/{id}/status ← Redis result (TTL 1h)
```

### Pre-Verification (synchronous, O(1)/O(n))

Run on the request thread before enqueuing:

| Check | Action on failure |
|-------|-------------------|
| `ReplayData.version < ReplayVersionPolicy.MinReplayVersion` | Reject 426 Upgrade Required (no flag) |
| `User.Flagged` | Reject 403 |
| Board dimensions outside [2, 400] | Flag user, reject 403 |
| Solve time < `clearCount * 0.08s` (skipped if ≤5 clears) | Flag user, reject 403 |
| Same `(seed, width, height)` from different user | Flag user, reject 403 |
| Rate limit (per-user, per-hour) | Reject 429 |

The replay-version gate runs **first** — before any check that can flag the user — so
clients on an outdated build (e.g. one that predates an RNG change) are told to update
instead of being accused of cheating when their replays no longer regenerate against
the current server code. Bump `ReplayVersionPolicy.MinReplayVersion` and the version
literal in `ReplayRecorder.ToReplayData()` together whenever a breaking change lands.

### Full Verification (async, in worker)

1. Regenerate board from seed using `PortableRandom` (xorshift32 — deterministic across Unity Mono and .NET).
2. Compare board snapshot (arrow count + cell lists).
3. Simulate all clear events in sequence, verifying each arrow is clearable.
4. Verify board is fully cleared.
5. Compute solve time from event timestamps (subtracting pause gaps).

### User Flagging

Account-level flag (`User.Flagged`, `User.FlagReason`). Flagged users are excluded from all leaderboard queries and blocked from submitting scores. Admin endpoints: `GET /api/admin/flagged-users`, `POST /api/admin/users/{id}/unflag`, `POST /api/admin/scores/{id}/remove`.

### Client Integration

`ScoreSubmitter` is fire-and-forget (`async void`). On 202, polls status 3 times at 2s intervals. Retryable errors (network, 5xx, 429) show a persistent toast with Retry button; permanent errors (401, 403, rejection) show Dismiss only. `GlobalToast` is a `DontDestroyOnLoad` singleton that survives scene transitions.

### Cross-Platform Determinism

`System.Random` produces different sequences on Mono (Unity) vs .NET from the same seed. All board generation randomness uses `PortableRandom` exclusively. `GenerationFingerprintTests` (both Unity EditMode and server xUnit) verify identical output for the same seeds across runtimes.

### What's Not Covered (and Why)

- **Server-issued seeds** — rejected. Auto-solving is instant for any seed; issuing server-side just adds one HTTP call to a bot's workflow.
- **Server-side timing** — rejected. Incompatible with async play (multi-session games over days/weeks). Pause gaps are client-reported, so a bot can fabricate them to match wall time.
- **Behavioral analysis** — diminishing returns. A bot can simulate plausible human click patterns.
- **Statistical outlier detection** — requires large player population to be meaningful.

See `docs/AntiCheatDesign.md` for full design history and PR-level implementation details.

## Known Limitations

### Mobile UI Scaling

The menu UI (UI Toolkit) is designed and tested for desktop resolutions only. On mobile devices, the UI renders oversized and vertically cropped due to fixed pixel font sizes and padding that don't adapt to mobile DPI or aspect ratios.

**What's broken:** buttons and title overflow the viewport on portrait mobile screens. The game scene (world-space rendering) scales fine since `CameraController` fits to board bounds — only the screen-space UI is affected.

**Why it's deferred:** fixing this properly requires either responsive USS (viewport-relative units, media-query-like breakpoints) or a PanelSettings scale mode tuned per platform. Both approaches need dedicated design and testing across device sizes — it's a separate UX pass, not a quick CSS fix. The GDD targets mobile-first for shipping, but desktop is sufficient for MVP gameplay validation.

**Unblocks:** all gameplay and input (including touch/pinch) work correctly on mobile. Only the menu UI is affected.

## Testing Strategy

- Domain logic must be testable without Unity runtime dependencies.
- Tests use Unity Test Framework (NUnit) in `Assets/Tests/EditMode/`.
- Priority test areas:
  - head-direction derivation
  - clearability / ray obstruction logic
  - generation validity, correctness, and determinism under fixed seeds
  - occupancy and bounds invariants
  - generation performance benchmarks (to catch regressions)
  - leaderboard store: add/get/sort/cap enforcement/personal best/favorites/neighbor entries/serialization
  - replay player: advance/seek/speed/boundary conditions
  - replay storage sizing (`[Explicit]`): raw and GZip-compressed sizes across board configurations

### PlayMode Tests (`Assets/Tests/PlayMode/`)

UI layout tests verify that all UI elements are visible and not clipped across multiple aspect ratios. Tests load UXML assets programmatically (via `AssetDatabase`) onto a runtime `UIDocument`, simulate different screen sizes by modifying `PanelSettings.referenceResolution`, and assert element bounds.

- **`UILayoutTestHelper`** — reusable utilities: `AspectRatio` struct, `SetPanelReferenceResolution`, `AssertElementFullyVisible`, `WarnElementFullyVisible`, `AssertAllVisibleChildren`, `WaitForLayoutResolve`.
- **UI layout test classes** (`Assets/Tests/PlayMode/UILayout/`) — split by screen: `MainMenuLayoutTests`, `GameHudLayoutTests`, `VictoryLayoutTests`, `LeaderboardLayoutTests`, `ReplayHudLayoutTests`. Each inherits from `UILayoutTestBase`. 21 UI states tested across 5 aspect ratios (16:9, 4:3, 21:9, 9:16, 1:1) = 105 test cases. Portrait (9:16) failures are reported as warnings (not hard failures) since fixed-pixel CSS is a known limitation.
- PanelSettings is saved/restored in SetUp/TearDown to avoid polluting other tests.
- **`NavigationCoverageTests`** — state-based navigation coverage validation. Each scene declares its possible UI states (e.g., loading, playing, modal-open). Each state specifies which buttons should be navigable (keyboard-reachable) and which are visible but behind a modal overlay (background). Per-state tests verify: (1) every navigable button is visible, (2) no uncovered visible button exists. Per-scene tests verify every named Button in the UXML is navigable in at least one state. Adding a button to UXML without wiring it into a state declaration fails the test.
- **`CSSResolutionTests`** — smoke tests verifying CSS hidden classes (`screen--hidden`, `modal--hidden`, `lb--hidden`, `victory--hidden`) resolve to `display: none` in each UXML document that uses them. Also tests responsive CSS selectors: leaderboard compact mode hides inline fav/play buttons when `lb-screen--compact` is applied, and documents the compact threshold (root width < 500px) across standard aspect ratios.
- **`SceneNavStackTests`** (EditMode) — unit tests for `SceneNavStack`, the pure stack logic extracted from `SceneNav`. Models every real user flow through the scene graph (Play, Continue, Quick Reset, Victory → Menu, Victory → Leaderboard → Back, nested Replay paths) and verifies the correct scene is returned on each Pop. Includes regression tests documenting previously fixed stack bugs.

## CI/CD

### Formatting

[CSharpier](https://csharpier.com/) (Roslyn-based, opinionated) owns all C# formatting. Configured as a local dotnet tool (`.config/dotnet-tools.json`, pinned version). Respects `.editorconfig` for `indent_size`, `indent_style`, and `max_line_length`.

IDE0055 (the IDE's built-in formatting diagnostic) is disabled in `.editorconfig` to avoid conflicting with CSharpier's output.

Unity's Roslyn analyzer pipeline does not read `.editorconfig` during compilation — only `.ruleset` files. For IDE-time analysis, `.editorconfig` works normally in VS/Rider.

### Git Hooks (`.githooks/`)

Activated via `git config core.hooksPath .githooks`. Setup: `dotnet tool restore && git config core.hooksPath .githooks`.

- **Pre-commit**: CSharpier formatting check on staged `.cs` files, 100 MB file size gate (GitHub's limit), Asset `.meta` file sync (added/removed files must have matching `.meta`).
- **Post-merge**: removes empty directories under `Assets/` to prevent Unity from generating orphan `.meta` files.

### GitHub Actions (`.github/workflows/ci.yml`)

Four jobs run in parallel:

- **`format`**: CSharpier check, file size validation, meta file sync. Uses `dotnet tool restore` — no Unity license needed.
- **`test-server`**: Server integration tests via `dotnet test`. Requires .NET 10 SDK. Uses Testcontainers (Docker).
- **`test`**: EditMode tests via [`game-ci/unity-test-runner@v4`](https://github.com/game-ci/unity-test-runner). Requires `UNITY_LICENSE`, `UNITY_EMAIL`, `UNITY_PASSWORD` secrets.
- **`test-playmode`**: PlayMode tests (UI layout, API client) via `game-ci/unity-test-runner@v4`. Runs without `-nographics` to ensure UI Toolkit resolves layout correctly.

### Branch Protection

`main` requires PRs, disallows force pushes and branch deletion. `enforce_admins` is off and `required_approving_review_count` is 0 so the sole contributor can merge their own PRs.

### Git Configuration

- **`.gitattributes`**: LF normalization, `diff=csharp` for `.cs` files, Unity YAML merge driver (`unityyamlmerge`) for scenes/prefabs/assets, `linguist-generated` markers to collapse Unity files in GitHub diffs, comprehensive binary type coverage. Based on [NYU Game Center's Unity-Git-Config](https://github.com/NYUGameCenter/Unity-Git-Config).
- **`.gitignore`**: Unity-generated folders, IDE files, build outputs. Includes `![Aa]ssets/**/*.meta` safety rule to prevent accidentally ignoring Asset meta files.
- **SmartMerge** (optional): `git config merge.unityyamlmerge.driver '<path>/UnityYAMLMerge merge -p %O %A %B %P'` for better Unity YAML conflict resolution.

### WebGL Deployment (`.github/workflows/deploy.yml`)

Continuous deployment to Cloudflare Pages. Triggers on published GitHub release or manually via `workflow_dispatch`. The Discord announcement workflow triggers after a successful deploy that was itself triggered by a release (not by `workflow_dispatch`).

- **`build-webgl`**: Checks out the repo, builds WebGL via [`game-ci/unity-builder@v4`](https://github.com/game-ci/unity-builder), and uploads the build as an artifact. Uses `allowDirtyBuild: true` because two pre-build steps intentionally modify the worktree: the git commit hash is written to `Assets/Resources/git-commit.txt`, and `bundleVersion` in `ProjectSettings/ProjectSettings.asset` is derived from the latest git tag via `sed`. These are build-time injections only — nothing is pushed back to the repository.
- **`deploy`**: Deploys the artifact to Cloudflare Pages via `cloudflare/wrangler-action@v3` (`pages deploy`).

WebGL player settings: Gzip compression, JS decompression fallback enabled, hash-based filenames for cache busting.

### Server Deployment (`.github/workflows/deploy-server.yml`)

Continuous deployment of the API server. Triggers on published GitHub release or manually via `workflow_dispatch`.

- **`build-and-push`**: Builds the Docker image from `server/Dockerfile` and pushes to `ghcr.io/vicplusplus/arrow-thing-api`.
- **`deploy`**: SSHs to the VPS, pulls the new image, runs `docker compose up -d api`, and performs a health check against `https://api.arrow-thing.com/health`.

### Discord Announcement (`.github/workflows/discord-announce.yml`)

Posts release notes to a Discord webhook. Triggers after a successful WebGL deploy workflow that was itself triggered by a release (not by `workflow_dispatch`). Uses the `DISCORD_WEBHOOK_URL` secret.

## Decision Log

- 2026-02-28: Adopted split between Unity-independent domain logic and Unity adapter layer.
- 2026-02-28: Defined `BoardModel` as authoritative source for occupancy and legality checks.
- 2026-02-28: Defined `BoardGenerator` as reusable source for initial fill and single-arrow generation.
- 2026-02-28: Standardized this document as the source of truth for architecture and class-structure changes.
- 2026-03-06: `generation-rewrite` branch refactored away from `BoardModel`/`BoardGenerator` toward minimal model classes (`Cell`, `Arrow`, `Board`) with game logic in static classes (`BoardGeneration`). Model classes are now intentionally minimal and self-contained.
- 2026-03-13: Occupancy and `IsClearable` moved into `Board`. View layer added: `GameController`, `CameraController`, `BoardView`, `BoardGridRenderer`, `ArrowView`, `InputHandler`, `BoardCoords`. Tests migrated from standalone .NET project to Unity Test Framework (`Assets/Tests/EditMode/`).
- 2026-03-15: Added start menu (UI Toolkit). `MainMenuController` in `MainMenu` scene, `GameSettings` static class for scene-transition parameter passing, random seed by default with inspector override.
- 2026-03-15: Deferred mobile UI support. See **Known Limitations > Mobile UI Scaling** for rationale.
- 2026-03-15: Added board clear screen. `VictoryController` drives zoom-to-fit → grid fade → victory popup sequence, connected via `BoardView.BoardCleared` event. Input is disabled during the entire sequence.
- 2026-03-16: Camera max zoom derived from board fit; removed configurable `maxOrthoSize`. Drag threshold moved to `GameController` inspector field. `MainMenuController` preserves selected preset when returning from game.
- 2026-03-16: Added PlayMode UI layout tests. 35 test cases across 7 UI states and 5 aspect ratios catch clipping/overflow regressions. Portrait (9:16) failures tracked as warnings pending responsive CSS work. `UILayoutTestHelper` utility makes adding tests for new screens trivial.
- 2026-03-16: Added in-game HUD (`GameHud.uxml`) with back-to-menu button (with leave confirmation modal) and solve timer. `GameTimer` domain model tracks inspection/solve phases with input-precision timestamps for final time. `ClearResult` enum replaces `bool` return from `TryClearArrow`. `GameTimerView` drives the HUD label. Victory popup now shows final solve time.
- 2026-03-16: License changed from Source-Available v2.0 to MIT. Game is free and open-source, distributed via WebGL on Cloudflare Pages. Added CD pipeline (`deploy.yml`) — builds WebGL on published release (or manual trigger), deploys to Cloudflare Pages automatically.
- 2026-03-13: Replaced geometric ray-hopping cycle detection with explicit dependency graph on `Board`. The old algorithm followed only the first hit per ray, missing multi-dependency cycles that surfaced after intermediate arrows were cleared. The new algorithm builds a reachability set from forward deps and checks each candidate cell against it. Generation cache (`boardCacheDict`) merged into `Board` to eliminate desync fragility. `Board.Version` removed (no longer needed without external cache). See [`BoardGeneration.md`](BoardGeneration.md) for the current algorithm.
- 2026-03-17: Added trajectory highlight toggle for playability on large boards. Trajectory lines reuse the already-computed extended path in `ArrowView` (window `[0, extensionDist]`), requiring no new geometry code. Auto-disables on successful clear to avoid stale lines.
- 2026-03-16: MVP (v0.1) declared complete. v0.2 planning started: authoritative ASP.NET Core server sharing domain code via monorepo shared `.csproj`, input-based replay system with sequence-numbered events, size-partitioned leaderboards (local + global), email-based account system (email/display name/JWT). Offline-first design — game always playable without server. See [`OnlineRoadmap.md`](OnlineRoadmap.md).
- 2026-03-20: Added `SnapSlider` reusable UI component. Replaces Unity's built-in `Slider`/`SliderInt` with a custom track+handle (pointer-captured drag), pill-shaped +/- and lock buttons, and PNG lock icons. Custom board-size sliders extracted from the preset card into a toggled panel below the grid. Start and Back buttons placed side by side to save vertical space in portrait.
- 2026-03-18: Added save-game and cancel-generation QoL features. Save uses the same event-log format as the planned replay system (`ReplayEvent`, `ReplayData`, `ReplayRecorder`) — the save file doubles as a partial replay. `SaveManager` persists to `Application.persistentDataPath/savegame.json`; `LoadAsync` runs file I/O on a background thread (synchronous fallback on WebGL). Resume restores the board from the saved initial snapshot via `Board.RestoreArrowsIncremental` (no generation step), replays clear events to reconstruct current state, and restores the solve timer via `GameTimer.Resume()`. Save file loading is deferred to the game scene (after the loading overlay is visible) to avoid menu lag. Cancel generation shows a confirmation modal. Leave-game modal always shown: "Save game?" when arrows cleared (with replace warning if a different save exists); "Leave game?" when no arrows cleared. Autosave writes every 10 clears when no conflicting save exists. `InputHandler` records all tap events to the `ReplayRecorder` and fires `onArrowCleared` for autosave. `end_solve` event recorded on board completion. `GameController` refactored into focused helper methods; loading overlay rendering decoupled from work coroutines (Update-driven). Arrows displayed incrementally during generation and restore. `BoardGridRenderer` rewritten as single tiling quad. `GameSettings` mutable properties replaced with `PlayerPrefs` key constants — consumers read `PlayerPrefs` directly.
- 2026-03-21: Added local leaderboard system (Phase 1-2). Domain layer: `LeaderboardEntry` model, `LeaderboardStore` (pure C# with per-config/global caps, favorite exemption, 3 sort criteria), `ReplayPlayer` (playback engine with speed control and incremental seek). `ReplayData` bumped to v3 with `gameVersion` field. `Board.GetDependents()` exposed for targeted clearable highlight updates. View layer: `LeaderboardManager` singleton (auto-bootstrap, file I/O with GZip-compressed replays, split index+replay storage). `VictoryController` records results, shows "New Best!" on personal best, adds "View Leaderboard" button. Replays viewable only from leaderboard screen (not victory popup). `GameSettings` extended with `StartReplay`/`ClearReplay` for replay viewer scene transition.
- 2026-03-23: Added email verification, password reset, email change, and password change flows. All email flows use 6-digit codes entered in-app (no browser pages — pure API server). Resend HTTP API for transactional email. Rate limiting: 5-minute per-user cooldown for email operations (app layer), `/api/*` 60 req/10s per IP (Cloudflare edge rule), 30 req/min general API (nginx). SecurityStamp on User model included in JWT and validated via middleware — bumping invalidates all sessions. Admin lock/unlock tooling: lock sets `LockedAt` + bumps stamp + clears codes; unlock sends password reset code. Email change sends code to new email + notification to old email (referencing Discord for support). Registration is non-revealing (duplicate emails get notification to owner, same 200 response). Login rejects unverified accounts. Username removed entirely — email is the sole login identifier. 38 integration tests (37 auth + 1 health check).
- 2026-03-21: Added leaderboard UI and replay viewer (Phase 3-4). Leaderboard scene: `LeaderboardScreenController` with 5 size tabs, 3 sort modes, Local/Global toggle, scrollable entry list, context menu, favorite toggle, auto-scroll from victory via `LeaderboardFocusGameId`. Replay viewer: `BoardSetupHelper` extracted from `GameController` for shared board/view/camera setup. `ReplayViewController` drives frame-based playback with `ReplayPlayer.Advance()`, animated clears/bumps, seek (pause-during-drag pattern), speed cycling, controls bar toggle, and clearable highlighting (electric cyan `#00DFFF`). `TapIndicatorPool` spawns procedural ring sprites (no asset needed). `ReplayPlayer` enhanced with 0.5s lead-in, 1.0s exit padding, `DisplayDuration` for UI clamping. Biggest sort tiebreaker: area → time → date.
- 2026-04-07: Added post-process compaction to board generation. `CompactBoardInPlace` iteratively merges trivial collinear same-direction adjacent arrow chains after generation, reducing visual clutter without affecting solvability. `FillBoardIncremental` accepts `compact` parameter; loading progress uses a three-phase model (generation → compaction → finalization) with `CompactionMarker` and `FinalizationMarker` sentinels. `GameController` rebuilds ArrowViews after compaction. `RemoveArrowForGeneration` on `Board` enables in-place modification during generation phase. Standalone benchmark project (`generation-benchmark/`) used during development and removed after benchmarking concluded.
- 2026-04-10: Fixed cross-platform board generation determinism. `FillBoardIncremental` now accepts `int seed` directly and derives all randomness via `PortableRandom` (xorshift32). Previously used `System.Random` to derive the `PortableRandom` seed, but Mono (Unity) and .NET produce different sequences from the same seed, causing server-side replay verification to always fail (snapshot mismatch). `System.Random` is now banned from domain code that affects board layout or verification. Added `GlobalToast` singleton (DontDestroyOnLoad) for persistent cross-scene error toasts. `ScoreSubmitter` rewritten as fire-and-forget with retryable error classification and 202 polling. Victory popup keyboard shortcuts (R=Play Again, L=Leaderboard, Escape=Menu). Escape blocked during victory animation.
- 2026-04-11: Finalized score integrity system. Three PRs: pre-verification with account flagging (#81/#82), Redis infrastructure (#83), async verification worker (#84/#85). Moved authoritative spec from standalone `AntiCheatDesign.md` into TDD § Score Integrity. Server-issued seeds and server-side timing evaluated and rejected — game state is fully observable, so no server-side measure can prove human presence. Anti-cheat targets casual cheaters; determined attackers are accepted as unstoppable.
- 2026-03-29: Complete UI overhaul. (1) Shared component library: `Shared.uss` extracts reusable styles (modals, back-button, icon-button, entry rows, loading overlay) used across all screens, eliminating duplication. (2) CSS variable theming: all UI colours moved to `ThemeColors.uss` as custom properties; all screen USS files reference `var(--...)` only. `VisualSettings.themeUIStyleSheet` holds the active theme; `UIThemeApplier` injects it into every UIDocument at runtime — swapping the asset in the inspector swaps the full palette. (3) Runtime theme switching: `ThemeManager` static class initialises from `ThemeRegistry` (`Resources/ThemeRegistry`) at `BeforeSceneLoad`, fires `ThemeChanged` event on `Apply()`; `UIThemeApplier` rewritten to subscribe to `ThemeChanged` and hot-swap the active stylesheet. `CustomDropdown` (custom popup injected into `panel.visualTree`) replaces Unity `DropdownField` for the theme selector. (4) Reusable C# components: `ConfirmModal` wrapper used by all confirm/cancel dialogs; `EditableLabel` (inline-edit label+icon); `LabeledField` (labeled TextField); `ExternalLinks` (WebGL-safe URL routing with confirmation modal). (5) Account panel redesign: offline display name always editable via `EditableLabel`; `LabeledField` standardises all form inputs. (6) `LeaderboardManager.AutoCreate` now bootstraps `GameSettings.DisplayName` from PlayerPrefs on startup, so display names appear in leaderboard entries even when the settings panel is never opened. (7) Leaderboard improvements: top-3 medal tints (gold/silver/bronze), Fastest/Biggest sort visibility swapped correctly per tab, Favorites sort secondary area tiebreaker. (8) View layer refactoring: `MainMenuController.OnEnable` split into Wire* helpers; `InputHandler.HandleSelectAndPan` extracts `HandleTap`; `BoardView.TryClearArrow` extracts `PlayBlockedFeedback`. (9) Icons: all circular HUD/nav buttons use PNG icons (WebGL-safe); lock, play, trophy, close, info, logout use filled icon variants. (10) Folder restructure: `Scripts/View/` split into Account/, Board/, Components/, Data/, HUD/, Scene/, Theme/ subfolders; `Art/` split into Icons/ and Sprites/; `UI/` split into Shared/, MainMenu/, Game/, Leaderboard/, Replay/. (11) `SettingsController` extracted from `MainMenuController` as a standalone MonoBehaviour; attach to any scene's UIDocument to get a fully functional settings panel including keyboard shortcut, account management, and external-link confirmation modal.
