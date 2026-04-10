# SceneNav: Switch from additive to single-mode scene loading

## Problem

SceneNav currently loads scenes additively and disables/enables them on Push/Pop.
This keeps scenes in memory across transitions, causing stale async state bugs
(e.g., a failed score submission's `async void` continuation survives scene disable
and leaks into a new game). The SaveState/RestoreState complexity isn't worth it.

## Design

Keep `SceneNavStack` (pure string stack) unchanged — it tracks navigation history.
Change `SceneNav` to use `LoadSceneMode.Single` for all transitions. Every scene
load fully unloads the previous scene. DontDestroyOnLoad singletons (KeybindManager,
SettingsController, LeaderboardManager) are unaffected.

### SceneNav changes

- **Push**: `_stack.Push(current, target); SceneManager.LoadScene(target);`
- **Pop**: `var prev = _stack.Pop(); SceneManager.LoadScene(prev ?? "MainMenu");`
- **Replace**: `_stack.Replace(current, target); SceneManager.LoadScene(target);`
- **Reset**: `_stack.Reset(); SceneManager.LoadScene(target);`
- Remove `SetSceneActive` helper (no more disable/enable).
- Remove `sceneLoaded` callbacks (Single mode sets active scene automatically).

### NavigableScene changes

- Remove `SaveState()` / `RestoreState()` virtual hooks.
- Remove `_hasState` field. `OnEnable` always runs fresh (no re-enable path).

### Scene controller changes

- **SoloSizeSelectController**: Remove `RestoreState`. Already reads from
  `GameSettings` on first enable — that becomes the only path.
- **LeaderboardScreenController**: Remove `SaveState`/`RestoreState`. Loads fresh
  each time. `GameSettings.LeaderboardFocusGameId` already carries focus state.

### SceneNavStack — no changes

The pure stack logic is correct and well-tested. Only the scene-loading side changes.

## Implementation plan

- [ ] 1. Simplify `SceneNav` — replace all four methods with single-mode loads
- [ ] 2. Strip SaveState/RestoreState from `NavigableScene`
- [ ] 3. Clean up `SoloSizeSelectController.RestoreState`
- [ ] 4. Clean up `LeaderboardScreenController.SaveState`/`RestoreState`
- [ ] 5. Verify `SceneNavStackTests` still pass (stack logic unchanged)
- [ ] 6. Update `docs/TechnicalDesign.md` if SceneNav description changed

## Testing

- Manual: Main Menu → Size Select → Game → Victory → Play Again → complete again
  → verify no stale submission
- Manual: Main Menu → Size Select → Game → Victory → Menu → verify correct return
- Manual: Leaderboard → Replay → back → verify leaderboard loads fresh
- EditMode: `SceneNavStackTests` should pass unchanged
