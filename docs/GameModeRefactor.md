# Per-Mode Encapsulation Refactor

> **Living doc** for the multi-phase refactor that turns
> `Assets/Scripts/View/Scene/GameController.cs` from a 1800-line catch-all
> into a thin orchestrator that delegates to per-mode `IGameMode`
> implementations. Each mode (Classic, Coop, future Endless / PvP) owns its
> own HUD additions, generation/snapshot strategy, win condition, and
> lifecycle.

## Goal

Make adding a new game mode a matter of "implement `IGameMode` and add a
case in `GameController.CreateMode`" — not "edit a dozen scattered
`if (_isCoopMode)` branches across `Update`, `WireHud`, `WireInput`,
`WireVictory`, `OnArrowCleared`, `OnLeaveConfirm`, etc."

Originally the prototype endless mode was bolted on by adding such
branches. That made every mode-touching change risk breaking the others
(e.g. moving the garbage meter into the shared HUD broke the leave modal
because of an XML comment collision; toggling timer-label visibility for
endless leaked into classic). Encapsulating each mode in its own class
prevents this whole class of bug.

## Architecture

```
GameController (thin orchestrator):
  - Shared SerializeFields: visualSettings, mainCamera, hudUIDocument,
    dragThresholdPixels, loadingFadeDuration, editor overrides.
  - Shared state: FocusNavigator, leave modal, loading overlay, board,
    boardView, camera controller, input handler.
  - Awake creates IGameMode based on GameSettings.
  - Update → mode.Tick().
  - WireHud → mode.OnHudWired() at end.
  - WireInput → uses mode.TapAttemptHandler.
  - End-of-setup → mode.WireRunFlow().
  - OnDestroy → mode.Dispose().

IGameMode (Assets/Scripts/View/Scene/Modes/IGameMode.cs):
  - string Name
  - IEnumerator Setup(GameContext)        // produces Board (+ view, etc).
  - void Tick()                            // per-frame mode logic.
  - void OnHudWired()                      // mode-specific HUD tweaks.
  - Func<Cell, Vector3, bool> TapAttemptHandler
  - void OnArrowCleared()                  // autosave / nothing.
  - void WireRunFlow()                     // victory / topout / nothing.
  - bool SupportsSaveOnLeave
  - bool WouldOverwriteDifferentSave
  - void SaveAndLeave()
  - void Dispose()

GameContext: shared dependencies passed from controller to mode (read-only
host services + writable Board/BoardView/etc that the mode populates during
Setup).

ClassicMode (MonoBehaviour, Assets/Scripts/View/Scene/Modes/ClassicMode.cs):
  - Owns (eventually): victoryUIDocument, GameTimer, GameTimerView,
    ReplayRecorder, VictoryController, autosave logic, save/leave.
  - SerializeFields (eventually): victoryUIDocument, inspectionDuration,
    inspectionWarningThreshold.

CoopMode (MonoBehaviour, Assets/Scripts/View/Scene/Modes/CoopMode.cs):
  - Owns (eventually): CoopClient, CoopSession, CoopPlayerTimer,
    CoopSidebar, CoopResultsScreen, reconnect backoff state.
  - SerializeFields (eventually): none beyond shared.
```

## Phase 1 (landed)

What's in: the dispatch layer.

- `IGameMode`, `GameContext` defined.
- `ClassicMode`, `CoopMode` MonoBehaviour skeletons created. They currently
  delegate back to `GameController` for all heavy lifting via
  `internal`-marked accessors (`HudRetryButton`, `OnCoopTapInternal`,
  `UpdateCoopRuntime`, `WireClassicVictory`, `HandleClassicArrowCleared`,
  `ClassicSaveAndLeave`, `ClassicWouldOverwriteDifferentSave`).
- `GameController.CreateMode()` instantiates the right MonoBehaviour
  (classic vs coop) at the top of `GenerateAndSetup` and binds it.
- All scattered `if (_isCoopMode)` branches collapse to a single mode
  dispatch:
  - `Update()`: was 3 inline coop blocks → `_mode?.Tick()`.
  - `WireHud()`: was inline retry-button branch → mode adjusts via
    `OnHudWired()` at end.
  - `WireInput()`: was inline `_isCoopMode ? OnCoopTap : null` →
    `_mode?.TapAttemptHandler`.
  - `GenerateAndSetup` end + `CoopSetup` end: was `WireVictory()` /
    "skip WireVictory" → `_mode?.WireRunFlow()`.

What's NOT in: relocation of mode-specific bodies. `GenerateAndSetup`'s
classic branch + `CoopSetup` still live on `GameController`. The classic
timer, recorder, victory wiring, save logic still live on `GameController`.
Mode classes are thin wrappers around these via the internal hooks.

Behavior is preserved verbatim — the refactor is a pure dispatch
restructuring at this stage.

## Phase 2A–C (landed)

What's in: ClassicMode now owns the autosave / retry-modal / save-and-leave cluster.

- **Fields moved from GameController to ClassicMode**:
  `_autosaveEnabled`, `_isContinuedGame`, `_initialArrowCount`,
  `_clearsSinceLastSave`, `_initialBoardSnapshot`, `_retryModal`,
  `AutosaveInterval` const.
- **Methods moved**:
  - `OnArrowCleared` body (autosave) → `ClassicMode.OnArrowCleared`
    (already in `IGameMode`).
  - `BuildReplayData` → `ClassicMode.BuildReplayData` (called by
    `WireVictoryDefault` and `SaveAndLeave`).
  - `SaveAndLeave` → `ClassicMode.SaveAndLeave` (already in `IGameMode`).
  - `HasAnyClearedArrows`, `WouldOverwriteDifferentSave` → ClassicMode.
  - `OnRetryClicked`, `OnRetryConfirm`, `OnRetryCancel`, `OnQuickSave` →
    ClassicMode (private; click handler dispatched from GameController via
    `OnRetryClickedDispatch`).
  - `FinalizeSession` body → `ClassicMode.FinalizeSession`. GameController
    keeps a transitional shim that forwards to the active mode.
  - Retry-modal construction moved into `ClassicMode.OnHudWired`.
- **IGameMode interface gained**: `HasInProgressChanges` (mode owns the
  "would saving make sense?" predicate); `OnQuickSaveHandler` (mode-driven
  Ctrl+S binding).
- **GameController gained accessors** for ClassicMode to read shared state
  (`CurrentBoard`, `Timer`, `Recorder`, `GameId`, `ActiveSeed`, `Width`,
  `Height`, `MaxArrowLength`, `InspectionDuration`, `ActiveInputHandler`,
  `HudDocument`) plus shared actions (`RequestQuickReset`,
  `RequestReturnToModeSelect`).
- **GameController callsites updated**:
  - `WireInput` reads `_mode?.OnQuickSaveHandler` for Ctrl+S binding and
    routes `onArrowCleared` through `_mode.OnArrowCleared()`.
  - `WireHud` retry-button click forwards to `OnRetryClickedDispatch` which
    casts to ClassicMode.
  - `OnLeaveConfirm` / `ShowLeave` decision tree now reads
    `_mode.WouldOverwriteDifferentSave`, `_mode.SupportsSaveOnLeave`,
    `_mode.HasInProgressChanges` instead of inline classic-only state.
  - `WireVictoryDefault` reads `BuildReplayData` + `AutosaveEnabled` from
    the active ClassicMode (cast).
- **Internal hooks removed**: `HandleClassicArrowCleared`,
  `ClassicSaveAndLeave`, `ClassicWouldOverwriteDifferentSave` —
  IGameMode now exposes these directly.

GameController shrunk by ~120 lines net. Behavior preserved verbatim
(transitional shims forward where needed).

## Phase 2D (landed)

What's in: ClassicMode now owns the entire classic setup pipeline +
victory wiring. `GameController` shrank from ~1873 → ~1425 lines (-448);
ClassicMode grew from 202 → ~700.

- **Fields moved to ClassicMode**: `_timer`, `_recorder`, `_gameId`,
  `_activeSeed`, `_w`, `_h`, `_maxLen`, `_inspectionDur`, plus a
  `FrameBudgetMs` const.
- **Methods moved to ClassicMode**: `ResolveParameters`, `LoadSaveAsync`,
  `ApplyResumeData`, `ResolveSeed`, `RestoreBoard`, `GenerateBoard`,
  `ReplayClears`, `SetupResumedRecorder`, `SetupNewRecorder`, `SetupTimer`,
  `WireVictoryDefault` (now `WireVictory`, called from `WireRunFlow`).
- **Classic branch of `GenerateAndSetup` → `ClassicMode.RunSetup`**:
  produces `Board`/`BoardView`/`CameraController` into the `GameContext`
  the controller hands it. GameController's branch is now ~10 lines —
  build context, await `_mode.Setup(ctx)`, copy results, run shared
  HUD/input wiring, `_mode.WireRunFlow()`.
- **Timer-view construction moved to `ClassicMode.OnHudWired`** (it owns
  the timer). `WireHud` no longer references timer state.
- **Coop timer-label hide moved to `CoopMode.OnHudWired`** (was in the
  `WireHud` else-branch). Symmetric counterpart of classic's timer view.
- **WireInput** reads `Timer`/`Recorder` via `(_mode as ClassicMode)?.…`
  cast — coop passes nulls.
- **Coop `SetupCamera()` call inlined**: the helper moved to ClassicMode;
  coop's snapshot path keeps the camera-setup block inline until phase 2E.

### SerializeFields stay on GameController

`victoryUIDocument`, `inspectionDuration`, `inspectionWarningThreshold`
and the editor board overrides (`boardWidth`, `boardHeight`,
`maxArrowLength`, `useRandomSeed`, `seed`) remain on GameController.
ClassicMode is added at runtime via `AddComponent<ClassicMode>()`, so it
can't carry inspector-bound references. They're exposed through new
`internal` accessors:

- `VictoryDocument`, `InspectionWarningThreshold`,
  `EditorInspectionDuration`, `EditorBoardWidth/Height/MaxArrowLength`,
  `EditorUseRandomSeed`, `EditorSeed`.
- `BoardViewRef`, `CameraControllerRef`, `BackButton` — used by victory
  wiring to hide HUD elements at the end of the run.
- `MarkVictoryStarted()` — sets the `_victoryStarted` flag on
  GameController so subsequent Escape presses don't reopen the leave modal.
- `ShowLoadingInternal`, `HideLoadingInternal`, `SetLoadProgress`,
  `CancelRequested` — let mode Setup drive the shared loading overlay.

Net: zero editor work needed — every move is C#-only.

### Removed from GameController

- All accessors that existed only for ClassicMode to read controller
  state during phase 2A–C: `Timer`, `Recorder`, `GameId`, `ActiveSeed`,
  `Width`, `Height`, `MaxArrowLength`, `InspectionDuration` (ClassicMode
  now owns the underlying fields).
- `WireClassicVictory` shim, `RunClassicSetupCoroutine` stub,
  `FinalizeSession` shim — no longer routed through controller.

## Phase 2E (landed)

What's in: CoopMode now owns the entire co-op pipeline. `GameController`
shrank from ~1425 → ~722 lines (-703); CoopMode grew from 93 → ~785.

- **Fields moved to CoopMode**: `_coopLobbyCode`, `_coopClient`,
  `_coopSnapshotData`, `_coopSession`, `_coopUserId`, `_heartbeatAccum`,
  `_coopShouldReconnect`, `_coopReconnectAttempt`, `_coopReconnectAt`,
  `_coopReconnectInFlight`, `_coopTapPool`, `_coopPlayerTimer`,
  `_coopSidebar`, `_coopResults`, `_previousRosterIds`,
  `_rosterDiffPrimed`, `_lastRateLimitToastAt`, `CoopReconnectDelays`,
  `HeartbeatIntervalSec`, `ReconnectToastGiveUpAttempt`. Also dropped the
  `_isCoopMode` flag entirely — `CreateMode` peeks
  `GameSettings.ActiveLobbyCode` to decide which mode to add.
- **Methods moved to CoopMode**: `CoopSetup` (now `RunSetup`),
  `CoopCompletedSetup`, `ParseHex`, `OnCoopRemoteCleared`,
  `OnCoopRemoteRejectedDep`, `OnCoopLobbyCompleted`,
  `ShowCoopResultsOverlay`, `HideGameplayHudForResults`,
  `LaunchCoopReplay`, `OnCoopRosterUpdated`, `GetReconnectDelay`,
  `AttemptCoopReconnectAsync`, `OnCoopTap`. The old `UpdateCoopRuntime`
  body is now inline in `CoopMode.Tick` (which also calls
  `_coopClient?.Update()` — the WS pump used to run unconditionally on
  GameController, now part of the mode tick because the mode is created
  before its Setup awaits any messages).
- **GenerateAndSetup unified**: classic and coop now share one path —
  build `GameContext`, `yield return _mode.Setup(ctx)`, copy
  Board/BoardView/CameraController out of ctx, run shared
  `WireHud`/`WireInput`/`mode.WireRunFlow`. The old `if (_isCoopMode)`
  branch fork is gone.
- **OnDestroy simplified**: the individual `_coop* ?.Dispose()` calls
  collapsed into the existing `_mode?.Dispose()`. `CoopMode.Dispose` was
  made idempotent (nulls fields after disposing) so it can also be
  called early from `ReturnToModeSelect` to halt the reconnect driver
  before scene pop.
- **Removed obsolete from GameController**: `UpdateCoopRuntime`,
  `OnCoopTapInternal`, `RunCoopSetupCoroutine` stub, and the
  `using System.Threading.Tasks` import.

### Final GameController shape

After phase 2E, GameController holds:

- Awake / OnDestroy / OnThemeChanged / OnSettingsOpenChanged.
- `Update()` — FocusNavigator, Escape, loading overlay fade,
  `_mode?.Tick()`.
- `CreateMode()` factory (peeks `GameSettings.ActiveLobbyCode`).
- `GenerateAndSetup` — ~25 lines of orchestration around
  `_mode.Setup(ctx)`.
- Shared HUD wiring (`WireHud`, `WireInput`, retry-dispatch, leave modal
  decision tree consulting `_mode.SupportsSaveOnLeave` /
  `WouldOverwriteDifferentSave` / `HasInProgressChanges`).
- Loading overlay (`ShowLoading`, `HideLoading`, `UpdateLoadingLabel`,
  `FadeElement`).
- Internal accessors that expose SerializeFields and shared scene state
  to mode classes.

`GameController.cs`: 722 lines (down from ~1873 at start of refactor).

## Phase 3 (next)

Re-introduce endless mode by adding `EndlessMode : MonoBehaviour, IGameMode`
that implements `Setup` (combo-queue garbage meter, generation, etc)
without touching `GameController` or the other modes. Cherry-pick the
endless-prototype branch's domain-layer changes (PendingArrow,
EndlessBoardSession, NativeGeneration improvements, multi-candidate
selection) and reuse the EndlessHud.uxml asset path it established.

The encapsulation is now in place: adding endless should only require the
new mode class + assets, plus a branch in `CreateMode` that returns it
when `GameSettings` indicates endless. No more scattered
`if (mode == endless)` checks.

## Reference

- Endless prototype branch: `feature/endless-mode-prototype`. Holds the
  full endless code + earlier IGameModeStrategy attempt (smaller-scope
  variant of this refactor).
- Original survey + decomposition plan:
  `docs/GameControllerDecompositionPlan.md` on
  `refactor/gamecontroller-decomposition-plan` aux branch.
