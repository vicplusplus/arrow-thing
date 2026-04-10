# Global Toast & Fire-and-Forget Score Submission

## Manual Test Cases

### Prefab Setup

**TC-0: GlobalToast prefab creation**
- Preconditions: Branch `feat/global-toast` checked out; no `Resources/GlobalToast` prefab exists yet.
- Steps:
  1. Create empty GameObject, name it `GlobalToast`.
  2. Add `UIDocument` component, assign `Assets/UI/Shared/GlobalToast.uxml`.
  3. Create or assign a `PanelSettings` with Sort Order higher than all other panels (e.g., 100).
  4. Add `UIThemeApplier` component.
  5. Add `GlobalToast` component.
  6. Save as prefab at `Assets/Resources/GlobalToast.prefab`.
- Postconditions: Prefab exists at `Resources/GlobalToast`. Play mode starts without `[GlobalToast] Prefab not found` error in console.

---

### Bootstrap & Lifecycle

**TC-1: Singleton bootstraps on first scene load**
- Preconditions: GlobalToast prefab exists at `Resources/GlobalToast`.
- Steps:
  1. Enter Play mode from any scene (Main Menu, Game, Leaderboard).
- Postconditions: `GlobalToast` GameObject exists in hierarchy, marked `DontDestroyOnLoad`. No error in console. Toast is not visible (hidden by default).

**TC-2: Singleton survives scene transitions**
- Preconditions: In Play mode, Main Menu scene loaded. GlobalToast exists in hierarchy.
- Steps:
  1. Navigate Main Menu → Solo Size Select → Game → victory → Menu (back to Main Menu).
- Postconditions: Exactly one `GlobalToast` GameObject exists in hierarchy throughout all transitions. No duplicates.

**TC-3: Missing prefab logs error gracefully**
- Preconditions: Temporarily rename or delete `Resources/GlobalToast.prefab`.
- Steps:
  1. Enter Play mode.
- Postconditions: Console shows `[GlobalToast] Prefab not found at Resources/GlobalToast...` error. No exception/crash. Game functions normally (score submission silently skips toast display).

---

### Error Toast (Persistent)

**TC-4: Error toast appears on submission failure (server down)**
- Preconditions: Logged in. Server is unreachable (stop server or disconnect network).
- Steps:
  1. Start and complete a board (clear all arrows).
  2. Observe victory sequence.
- Postconditions: Victory popup appears immediately (no delay/stall). Error toast appears at top-right showing "No internet connection." Toast has a visible Dismiss button. Toast remains visible indefinitely.

**TC-5: Error toast persists across scene transition**
- Preconditions: Error toast visible from TC-4.
- Steps:
  1. Click "Menu" on the victory popup to return to Main Menu.
- Postconditions: Error toast remains visible on Main Menu screen. Dismiss button still functional.

**TC-6: Error toast dismissed manually**
- Preconditions: Error toast visible.
- Steps:
  1. Click the "Dismiss" button on the toast.
- Postconditions: Toast disappears. No toast visible.

**TC-7: Error toast persists through multiple scene transitions**
- Preconditions: Error toast visible from TC-4.
- Steps:
  1. Navigate: Menu → Settings (open/close) → Solo Size Select → back to Menu.
- Postconditions: Toast remains visible through all transitions.

**TC-8: New error replaces existing error toast**
- Preconditions: Error toast visible. Server still down.
- Steps:
  1. Start and complete another board.
- Postconditions: Toast updates with the new error message (or stays the same if message is identical). Only one toast visible, not stacked.

---

### Info Toast (Auto-Hide)

**TC-9: Info toast auto-hides after ~4 seconds**
- Preconditions: No toast visible. (Requires calling `GlobalToast.Instance.ShowInfo("Test")` from a debug script or console, since no current code path triggers info toasts.)
- Steps:
  1. Trigger `GlobalToast.Instance.ShowInfo("Test message")`.
- Postconditions: Toast appears without Dismiss button. Toast auto-hides after approximately 4 seconds. No manual dismissal needed.

---

### Score Submission Flow (Fire-and-Forget)

**TC-10: Successful submission — no toast shown**
- Preconditions: Logged in. Server running with Redis and worker.
- Steps:
  1. Complete a board (clear all arrows).
- Postconditions: Victory popup appears immediately (no stall). No toast appears. Console logs show `[ScoreSubmitter] Score accepted for verification` then `[ScoreSubmitter] Verified: rank=..., pb=...`. Score appears on leaderboard.

**TC-11: Victory popup is non-blocking**
- Preconditions: Logged in. Server running but slow (or normal).
- Steps:
  1. Complete a board.
  2. Immediately interact with the victory popup (click Play Again or Menu) before submission could have completed.
- Postconditions: Scene transition happens instantly. No hang, no error. Submission continues in background.

**TC-12: Not logged in — no submission attempted**
- Preconditions: Not logged in (no JWT stored).
- Steps:
  1. Complete a board.
- Postconditions: Victory popup appears. No network requests in console logs. No toast. Local leaderboard entry still recorded.

**TC-13: 401 Unauthorized — session expired toast**
- Preconditions: Logged in with an expired/invalid JWT. Server running.
- Steps:
  1. Complete a board.
- Postconditions: Error toast appears: "Session expired. Please log in again." Toast persists until dismissed.

**TC-14: Verification rejected — failure toast**
- Preconditions: Logged in. Server running. Replay data is tampered (requires modifying replay before submission, e.g., setting impossible finalTime).
- Steps:
  1. Complete a board with tampered replay data.
- Postconditions: Error toast appears with "Verification failed: ..." message. Toast persists until dismissed.

**TC-15: Verification still pending after polling — no toast**
- Preconditions: Logged in. Worker is stopped (server + Redis running, but worker not consuming queue).
- Steps:
  1. Complete a board.
  2. Wait ~6 seconds (3 polls × 2s interval).
- Postconditions: Console logs show `[ScoreSubmitter] Verification still pending after polling`. No toast shown (pending is not an error). Score may appear on leaderboard once worker processes it later.

---

### Theme & Rendering

**TC-16: Toast renders above all other UI**
- Preconditions: Error toast visible.
- Steps:
  1. Open Settings overlay.
- Postconditions: Toast renders on top of the Settings panel (due to higher PanelSettings sort order).

**TC-17: Toast respects active theme**
- Preconditions: GlobalToast prefab has UIThemeApplier.
- Steps:
  1. Trigger an error toast.
  2. Open Settings → change theme.
- Postconditions: Toast styling updates to match the new theme (background color, text color follow `.toast` base styles from Shared.uss).
