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

## Phase 2D (next)

Goal: move the rest of classic-only state into ClassicMode, then
restructure `GenerateAndSetup` so the classic branch lives in
`ClassicMode.Setup`.

### ClassicMode absorbs (remaining)

- `_timer`, `_recorder`, `_gameId`, `_activeSeed`, `_w`, `_h`, `_maxLen`,
  `_inspectionDur` fields (these are still on GameController for now, with
  internal accessors so ClassicMode can read them).
- `victoryUIDocument`, `inspectionDuration`, `inspectionWarningThreshold`
  SerializeFields → move from `GameController` to `ClassicMode`. Requires
  user to reattach in Unity editor.
- `SetupTimer`, `SetupNewRecorder`, `SetupResumedRecorder` methods (set
  the timer + recorder + game-id state).
- `RestoreBoard`, `ReplayClears`, `ResolveParameters`, `ApplyResumeData`,
  `ResolveSeed`, `LoadSaveAsync` (resume / replay logic).
- `WireVictoryDefault` body — moves into `ClassicMode.WireRunFlow`.
- The classic branch of `GenerateAndSetup` (lines ~301–375) becomes
  `ClassicMode.Setup`'s body. Once moved, `GenerateAndSetup` shrinks to
  ~10 lines: resolve mode flag, create mode, `yield return mode.Setup`,
  `WireHud`, `WireInput`, `mode.WireRunFlow`.

**Editor task during this phase**: detach `Victory UIDocument` and the
two timer SerializeFields from the GameController component in `Game.unity`
and reattach them to the new ClassicMode component on the same GameObject.
Per CLAUDE.md, this is editor work — done manually.

### CoopMode absorbs

- All `_coopXxx` fields.
- `CoopSetup`, `CoopCompletedSetup`, `OnCoopRemoteCleared`,
  `OnCoopRemoteRejectedDep`, `OnCoopLobbyCompleted`, `OnCoopRosterUpdated`,
  `OnCoopTap`, `ShowCoopResultsOverlay`, `HideGameplayHudForResults`,
  `LaunchCoopReplay`, `AttemptCoopReconnectAsync`, `GetReconnectDelay`.
- `UpdateCoopRuntime` body (currently extracted, called by stub on
  controller) → moves wholesale into `CoopMode.Tick`.
- The coop branch of `GenerateAndSetup` (one-liner) and the entirety of
  `CoopSetup` become `CoopMode.Setup`'s body.
- `OnDestroy` coop disposes (`_coopResults`, `_coopSidebar`, etc) → into
  `CoopMode.Dispose`.

### Trim `GameController`

After phase 2, the controller should hold only:

- Awake / OnDestroy / OnThemeChanged / OnSettingsOpenChanged.
- `Update()` for FocusNavigator, Escape, loading overlay fade — plus
  `_mode?.Tick()`.
- Shared HUD wiring (back button, leave modal, loading overlay,
  cancel-generation modal, FocusNavigator).
- Shared input wiring (calls `mode.TapAttemptHandler`).
- Loading overlay coroutine helpers (`ShowLoading`, `HideLoading`,
  `UpdateLoadingLabel`, `FadeElement`).
- Shared board/view/camera setup helpers — or move to a static helper.
- `CreateMode()` factory.
- Leave modal decision tree — but checks the active mode's
  `SupportsSaveOnLeave` / `WouldOverwriteDifferentSave` properties.

Estimated final size: ~400–500 lines (down from ~1800).

### Order of operations

1. Move ClassicMode-only fields + methods. Keep stubs on GameController
   that forward to `_mode` when needed for backward compatibility, then
   remove the stubs once nothing references them.
2. Move CoopMode-only fields + methods. Same pattern.
3. Trim GameController.
4. Verify both modes still work (manually + any tests we add along the way).

## Phase 3 (after both modes work)

Re-introduce endless mode by adding `EndlessMode : MonoBehaviour, IGameMode`
that implements `Setup` (combo-queue garbage meter, generation, etc)
without touching `GameController` or the other modes. Cherry-pick the
endless-prototype branch's domain-layer changes (PendingArrow,
EndlessBoardSession, NativeGeneration improvements, multi-candidate
selection) and reuse the EndlessHud.uxml asset path it established.

This phase validates the framework: if adding endless requires only the
new mode class + asset, the encapsulation is correct.

## Reference

- Endless prototype branch: `feature/endless-mode-prototype`. Holds the
  full endless code + earlier IGameModeStrategy attempt (smaller-scope
  variant of this refactor).
- Original survey + decomposition plan:
  `docs/GameControllerDecompositionPlan.md` on
  `refactor/gamecontroller-decomposition-plan` aux branch.
