using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

/// <summary>
/// The leaderboard's right-click / kebab context menu. Owns the floating
/// menu element, its delete-confirmation modal, the in-place keyboard
/// navigation popup, and the open/dismiss state. Pulled out of
/// <c>LeaderboardScreenController</c> as a self-contained sub-component
/// (~260 LOC) so the controller no longer carries the menu's per-action
/// machinery.
///
/// <para>Callbacks fire when the user picks an action; the controller
/// owns the actual mutations (favorite toggle via <c>LeaderboardManager</c>,
/// replay launch, delete + post-rebuild refocus) because they touch
/// scene-wide state that the menu shouldn't know about.</para>
/// </summary>
public sealed class LeaderboardContextMenu
{
    /// <summary>Per-instance hooks the menu calls when the user picks an action.</summary>
    public sealed class Callbacks
    {
        /// <summary>True when the leaderboard is in its compact (mobile / narrow) layout. The menu shows favorite/play actions inline only in compact mode.</summary>
        public Func<bool> IsCompact;

        /// <summary>
        /// Re-read the favorited state of <paramref>gameId</paramref> from the
        /// store right before the delete decision. The user could have toggled
        /// favorite via the inline button (or a keyboard shortcut) while the
        /// menu was open, so the cached <see cref="_isFavorite"/> from Show()
        /// is potentially stale.
        /// </summary>
        public Func<string, bool> IsFavorite;

        /// <summary>Toggle favorite for gameId, given the current favorited state.</summary>
        public Action<string, bool> OnToggleFavorite;

        /// <summary>Launch replay for gameId.</summary>
        public Action<string> OnPlay;

        /// <summary>
        /// Delete the entry with this gameId. Fires only after the user
        /// has confirmed (for favorited entries) or immediately (for
        /// non-favorited). The controller is responsible for the actual
        /// store mutation + post-delete focus restoration.
        /// </summary>
        public Action<string> OnDeleteConfirmed;
    }

    private readonly VisualElement _root;
    private readonly VisualElement _menu;
    private readonly Button _favoriteBtn;
    private readonly Button _playBtn;
    private readonly Button _deleteBtn;
    private readonly ConfirmModal _deleteModal;
    private readonly PopupKeyboardNav _nav = new PopupKeyboardNav();
    private readonly Callbacks _cb;

    private string _gameId;
    private bool _isFavorite;
    private string _pendingDeleteGameId;

    /// <summary>True when the menu is currently visible.</summary>
    public bool IsOpen => _menu != null && !_menu.ClassListContains("lb--hidden");

    /// <summary>True when the menu's keyboard nav popup is currently consuming input.</summary>
    public bool IsKeyboardNavActive => _nav.IsActive;

    /// <summary>
    /// Looks up + wires the menu and its delete modal off the leaderboard
    /// scene root. Caller passes <paramref name="root"/> = the scene root
    /// so the menu can read panel height and position itself.
    /// </summary>
    public LeaderboardContextMenu(VisualElement root, Callbacks callbacks)
    {
        _root = root ?? throw new ArgumentNullException(nameof(root));
        _cb = callbacks ?? throw new ArgumentNullException(nameof(callbacks));

        _menu = root.Q("lb-context-menu");
        _favoriteBtn = root.Q<Button>("ctx-favorite-btn");
        _playBtn = root.Q<Button>("ctx-play-btn");
        _deleteBtn = root.Q<Button>("ctx-delete-btn");

        // Wire button click handlers up front; per-action behavior reads
        // the menu's current _gameId state.
        if (_favoriteBtn != null)
            _favoriteBtn.clicked += OnFavoriteClicked;
        if (_playBtn != null)
            _playBtn.clicked += OnPlayClicked;
        if (_deleteBtn != null)
            _deleteBtn.clicked += OnDeleteClicked;

        // Delete-confirmation modal lives here because it's only used by
        // the delete-favorited path. Non-favorited entries skip the modal
        // and delete immediately.
        _deleteModal = new ConfirmModal(
            root.Q("delete-modal"),
            "Delete this favorited entry?",
            "Delete",
            "Cancel",
            isDanger: true
        );
        _deleteModal.Confirmed += OnDeleteModalConfirm;
        _deleteModal.Cancelled += OnDeleteModalCancel;
    }

    /// <summary>Drive the menu's keyboard navigation. Caller invokes from PreUpdate when <see cref="IsKeyboardNavActive"/>.</summary>
    public void UpdateKeyboardNav() => _nav.Update();

    /// <summary>
    /// Show the menu anchored to <paramref name="anchorRow"/>'s world bounds.
    /// In compact (narrow) mode the menu also offers favorite + play; on
    /// wide layouts those are inline on the row and the menu is delete-only.
    /// </summary>
    public void Show(string gameId, bool isFavorite, VisualElement anchorRow)
    {
        if (_menu == null)
            return;

        _gameId = gameId;
        _isFavorite = isFavorite;
        bool compact = _cb.IsCompact();

        if (_favoriteBtn != null)
        {
            ShowElement(_favoriteBtn, compact);
            _favoriteBtn.text = isFavorite ? "Unfavorite" : "Favorite";
        }
        ShowElement(_playBtn, compact);

        // Position near the anchor row, flipping above if it would
        // overflow the bottom of the scene root.
        var rowBounds = anchorRow.worldBound;
        float panelHeight = _root.resolvedStyle.height;
        float menuHeight = _menu.resolvedStyle.height;
        if (menuHeight <= 0)
            menuHeight = 60; // fallback estimate when the menu hasn't been laid out yet

        bool fitsBelow = rowBounds.yMax + menuHeight <= panelHeight;

        _menu.style.right = 16;
        _menu.style.left = StyleKeyword.Auto;

        if (fitsBelow)
        {
            _menu.style.top = rowBounds.yMax;
            _menu.style.bottom = StyleKeyword.Auto;
        }
        else
        {
            _menu.style.bottom = panelHeight - rowBounds.yMin;
            _menu.style.top = StyleKeyword.Auto;
        }

        ShowElement(_menu, true);

        // Wire keyboard navigation for the visible buttons.
        var navItems = new List<VisualElement>();
        var navCallbacks = new List<Action>();
        if (compact && _favoriteBtn != null)
        {
            navItems.Add(_favoriteBtn);
            navCallbacks.Add(OnFavoriteClicked);
        }
        if (compact && _playBtn != null)
        {
            navItems.Add(_playBtn);
            navCallbacks.Add(OnPlayClicked);
        }
        if (_deleteBtn != null)
        {
            navItems.Add(_deleteBtn);
            navCallbacks.Add(OnDeleteClicked);
        }
        _nav.Open(navItems, navCallbacks, Dismiss);
    }

    /// <summary>Close the menu and clear its current entry context.</summary>
    public void Dismiss()
    {
        ShowElement(_menu, false);
        _nav.Close();
        _gameId = null;
    }

    /// <summary>
    /// True if a click at <paramref name="worldPos"/> would land inside
    /// the open menu. Used by the controller's root pointer-down handler
    /// to suppress the click-outside-to-dismiss path on in-menu clicks.
    /// </summary>
    public bool ContainsWorldPoint(Vector2 worldPos) =>
        _menu != null && _menu.worldBound.Contains(worldPos);

    /// <summary>
    /// Re-check the favorited state of the currently-open entry from the
    /// authoritative source (e.g. <c>LeaderboardManager</c>) before a
    /// delete decision. Called by the controller in <c>OnDeleteClicked</c>
    /// path because the user could have toggled favorite while the menu
    /// was already open via the inline button.
    /// </summary>
    public void RefreshFavoriteState(bool currentlyFavorite)
    {
        _isFavorite = currentlyFavorite;
    }

    /// <summary>The gameId the menu is currently anchored to, or null if dismissed.</summary>
    public string CurrentGameId => _gameId;

    // ── Action handlers (route to callbacks) ──────────────────────────

    private void OnFavoriteClicked()
    {
        if (_gameId == null)
            return;
        _cb.OnToggleFavorite(_gameId, _isFavorite);
        Dismiss();
    }

    private void OnPlayClicked()
    {
        if (_gameId == null)
            return;
        // Dismiss before launch so the playback scene starts clean.
        var id = _gameId;
        Dismiss();
        _cb.OnPlay(id);
    }

    private void OnDeleteClicked()
    {
        if (_gameId == null)
            return;

        // Re-read favorited state from the store — could have been toggled
        // since Show() via the inline button or a keyboard shortcut.
        bool currentlyFavorite = _cb.IsFavorite != null ? _cb.IsFavorite(_gameId) : _isFavorite;

        if (currentlyFavorite)
        {
            _pendingDeleteGameId = _gameId;
            Dismiss();
            _deleteModal.Show();
        }
        else
        {
            var id = _gameId;
            Dismiss();
            _cb.OnDeleteConfirmed(id);
        }
    }

    private void OnDeleteModalConfirm()
    {
        _deleteModal.Hide();
        if (_pendingDeleteGameId != null)
        {
            var id = _pendingDeleteGameId;
            _pendingDeleteGameId = null;
            _cb.OnDeleteConfirmed(id);
        }
    }

    private void OnDeleteModalCancel()
    {
        _deleteModal.Hide();
        _pendingDeleteGameId = null;
    }

    private static void ShowElement(VisualElement el, bool show)
    {
        if (el == null)
            return;
        if (show)
            el.RemoveFromClassList("lb--hidden");
        else
            el.AddToClassList("lb--hidden");
    }
}
