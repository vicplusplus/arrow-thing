using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Manages the visual representation of the entire board.
/// Owns ArrowView instances and the grid renderer.
///
/// For boards with Width*Height >= <see cref="CullingThreshold"/>, viewport
/// culling is active: arrows are batched into chunked combined meshes (one
/// MeshFilter+MeshRenderer per visible chunk). Individual <see cref="ArrowView"/>
/// instances are only spawned for interactions (clear/blocked animations).
/// </summary>
public sealed class BoardView : MonoBehaviour
{
    public const int CullingThreshold = 3600; // 60x60

    private Board _board = null!;
    private VisualSettings _settings = null!;
    private int _clearedCount;

    private bool _trailVisible;
    private ArrowView _tintedSource;
    private ArrowView _tintedBlocker;

    // -- Non-culling mode: all arrows have views --------------------------------
    private readonly Dictionary<Arrow, ArrowView> _arrowViews = new();

    // -- Culling mode -----------------------------------------------------------
    private bool _useCulling;
    private CameraController _cameraController;
    private int _bucketsX;
    private int _bucketsY;
    private readonly Dictionary<int, BoardChunkRenderer> _chunks = new();
    private readonly Dictionary<Arrow, List<int>> _arrowChunks = new();
    private readonly Dictionary<Arrow, ArrowView> _interactionViews = new();
    private readonly Dictionary<Arrow, Color> _arrowColors = new();
    private BoardTrailRenderer _trailRenderer;
    private bool _highlightsActive;
    private static readonly Color HighlightColor = new(0f, 0.875f, 1f);

    /// <summary>
    /// Fired after the last arrow is cleared and recorded, but before its pull-out animation finishes.
    /// </summary>
    public event System.Action LastArrowClearing;

    /// <summary>
    /// Fired after the last arrow's pull-out animation finishes (board fully cleared).
    /// </summary>
    public event System.Action BoardCleared;

    /// <summary>
    /// Fired when trails are auto-disabled (e.g. after a successful clear).
    /// </summary>
    public event System.Action TrailAutoOff;

    /// <summary>
    /// Hook for routing real-arrow removal through a wrapper. Defaults to a
    /// direct <see cref="Board.RemoveArrow"/>. Endless mode overrides this with
    /// <see cref="EndlessBoardSession.ClearRealArrow"/> so the session's
    /// long-lived <see cref="NativeGenerationState"/> stays in sync (the
    /// cleared arrow's bitset row is zeroed and its slot is freed for future
    /// ghost spawns). Set via <see cref="SetArrowRemover"/>.
    /// </summary>
    private System.Action<Arrow> _arrowRemover;

    /// <summary>
    /// Overrides the default arrow-removal behavior. Pass null to restore the
    /// default (direct <see cref="Board.RemoveArrow"/>).
    /// </summary>
    public void SetArrowRemover(System.Action<Arrow> remover)
    {
        _arrowRemover = remover;
    }

    /// <summary>
    /// When true, arrow trail overlay stays visible after an arrow is cleared
    /// instead of auto-hiding. Controlled via Settings > Gameplay.
    /// </summary>
    public bool KeepTrailAfterClear { get; set; }

    public BoardGridRenderer GridRenderer { get; private set; }

    private Transform _arrowParent;

    /// <summary>
    /// Sets up grid and arrow parent. If <paramref name="spawnArrows"/> is false,
    /// arrows must be added incrementally via <see cref="AddArrowView"/> followed
    /// by <see cref="ApplyColoring"/> when complete.
    /// </summary>
    public void Init(Board board, VisualSettings settings, bool spawnArrows = true)
    {
        _board = board;
        _settings = settings;
        _useCulling = board.Width * board.Height >= CullingThreshold;

        GridRenderer = gameObject.AddComponent<BoardGridRenderer>();
        GridRenderer.Init(board, settings);

        _arrowParent = new GameObject("Arrows").transform;
        _arrowParent.SetParent(transform, false);

        if (_useCulling)
        {
            _bucketsX =
                (board.Width + BoardSpatialIndex.BucketSize - 1) / BoardSpatialIndex.BucketSize;
            _bucketsY =
                (board.Height + BoardSpatialIndex.BucketSize - 1) / BoardSpatialIndex.BucketSize;

            var trailGo = new GameObject("Trails");
            trailGo.transform.SetParent(transform, false);
            _trailRenderer = trailGo.AddComponent<BoardTrailRenderer>();
            _trailRenderer.Init(board, settings);
        }

        if (spawnArrows)
        {
            foreach (Arrow arrow in board.Arrows)
                AddArrowView(arrow);

            ApplyColoring();
        }
    }

    /// <summary>
    /// Connects the camera controller for viewport culling. Call after camera setup.
    /// </summary>
    public void SetCameraController(CameraController cam)
    {
        if (!_useCulling)
            return;

        _cameraController = cam;
        cam.CameraChanged += OnCameraChanged;
    }

    /// <summary>
    /// Registers an arrow with the view. In culling mode, adds the arrow to all
    /// chunks it touches and marks them dirty for rebuild.
    /// </summary>
    public void AddArrowView(Arrow arrow)
    {
        if (_useCulling)
        {
            AddArrowToChunks(arrow);
            return;
        }

        SpawnArrowView(arrow);
    }

    private void AddArrowToChunks(Arrow arrow)
    {
        Color color = _arrowColors.TryGetValue(arrow, out Color c) ? c : _settings.arrowBodyColor;
        if (_highlightsActive && _board.IsClearable(arrow))
            color = HighlightColor;

        var chunkKeys = ComputeChunkKeys(arrow);
        _arrowChunks[arrow] = chunkKeys;

        foreach (int key in chunkKeys)
        {
            BoardChunkRenderer chunk = GetOrCreateChunk(key);
            chunk.AddArrow(arrow, color);
        }

        _trailRenderer.AddArrow(arrow);
    }

    private List<int> ComputeChunkKeys(Arrow arrow)
    {
        var keys = new List<int>();
        var seen = new HashSet<int>();
        foreach (Cell cell in arrow.Cells)
        {
            int bx = cell.X / BoardSpatialIndex.BucketSize;
            int by = cell.Y / BoardSpatialIndex.BucketSize;
            int key = by * _bucketsX + bx;
            if (seen.Add(key))
                keys.Add(key);
        }
        return keys;
    }

    private BoardChunkRenderer GetOrCreateChunk(int key)
    {
        if (_chunks.TryGetValue(key, out BoardChunkRenderer chunk))
            return chunk;

        int by = key / _bucketsX;
        int bx = key % _bucketsX;
        var go = new GameObject($"Chunk_{bx}_{by}");
        go.transform.SetParent(_arrowParent, false);
        chunk = go.AddComponent<BoardChunkRenderer>();
        chunk.Init(_board.Width, _board.Height, _settings);
        go.SetActive(false); // activated by camera visibility
        _chunks[key] = chunk;
        return chunk;
    }

    /// <summary>
    /// Re-colors all arrows for a new theme. Safe to call at any time.
    /// </summary>
    public void ApplyTheme(VisualSettings settings)
    {
        _settings = settings;
        if (settings.arrowPalette.Count > 0)
        {
            ApplyColoring();
        }
        else
        {
            if (_useCulling)
            {
                _arrowColors.Clear();
                foreach (var chunk in _chunks.Values)
                    chunk.MarkDirty();
            }
            else
            {
                foreach (var view in _arrowViews.Values)
                    view.SetBaseColor(settings.arrowBodyColor, settings.arrowHeadColor);
            }
        }

        if (_useCulling && _trailRenderer != null)
            _trailRenderer.ApplyTheme(settings);
    }

    /// <summary>
    /// Picks a deterministic palette color for an arrow not yet in the map-
    /// coloring graph (e.g. an endless-mode pending arrow) and applies it to
    /// the arrow's view. The color is chosen by hashing the arrow's head cell
    /// so a given pending arrow always pulses the same color, even across
    /// frames. When the pending commits and <see cref="ApplyColoring"/> runs,
    /// the proper graph-coloring algorithm may reassign a different color —
    /// the visual change happens at the moment alpha goes opaque, which reads
    /// as "now solid" rather than as a glitch.
    /// </summary>
    public void AssignFallbackColor(Arrow arrow)
    {
        if (_settings.arrowPalette == null || _settings.arrowPalette.Count == 0)
            return;
        int idx =
            (Mathf.Abs(arrow.HeadCell.X * 73856093 ^ arrow.HeadCell.Y * 19349663))
            % _settings.arrowPalette.Count;
        SetArrowColor(arrow, _settings.arrowPalette[idx]);
    }

    /// <summary>
    /// Sets a single arrow's render color directly. Used by endless mode to
    /// stamp combo colors on freshly-spawned arrows without going through
    /// the dependency-graph palette assignment in <see cref="ApplyColoring"/>.
    /// </summary>
    public void SetArrowColor(Arrow arrow, Color c)
    {
        if (_useCulling)
        {
            _arrowColors[arrow] = c;
            if (_arrowChunks.TryGetValue(arrow, out var keys))
                foreach (int key in keys)
                    if (_chunks.TryGetValue(key, out var chunk))
                        chunk.SetArrowColor(arrow, c);
        }
        else if (_arrowViews.TryGetValue(arrow, out ArrowView view))
        {
            view.SetBaseColor(c, c);
        }
    }

    /// <summary>
    /// Applies map-coloring palette to all current arrow views.
    /// </summary>
    public void ApplyColoring()
    {
        if (_settings.arrowPalette.Count > 0)
        {
            int[] colors = ArrowColoring.AssignColors(_board, _settings.arrowPalette.Count);
            for (int i = 0; i < _board.Arrows.Count; i++)
            {
                Arrow arrow = _board.Arrows[i];
                Color c = _settings.arrowPalette[colors[i]];

                if (_useCulling)
                {
                    _arrowColors[arrow] = c;
                    if (_arrowChunks.TryGetValue(arrow, out var keys))
                    {
                        foreach (int key in keys)
                            if (_chunks.TryGetValue(key, out var chunk))
                                chunk.SetArrowColor(arrow, c);
                    }
                }
                else
                {
                    _arrowViews[arrow].SetBaseColor(c, c);
                }
            }
        }

        if (_useCulling && _cameraController != null)
            OnCameraChanged();
    }

    /// <summary>
    /// Attempts to clear an arrow. Returns a ClearResult indicating what happened.
    /// </summary>
    public ClearResult TryClearArrow(Arrow arrow)
    {
        if (_useCulling)
            return TryClearArrowCulled(arrow);

        if (!_arrowViews.TryGetValue(arrow, out ArrowView view))
        {
            Debug.LogWarning(
                $"[BoardView] TryClearArrow: no view for arrow@({arrow.HeadCell.X},{arrow.HeadCell.Y})"
            );
            return ClearResult.Blocked;
        }

        ClearPreviousTints();

        if (!_board.IsClearable(arrow))
        {
            PlayBlockedFeedback(arrow, view);
            return ClearResult.Blocked;
        }

        _arrowViews.Remove(arrow);
        if (_arrowRemover != null)
            _arrowRemover(arrow);
        else
            _board.RemoveArrow(arrow);
        _clearedCount++;
        bool wasFirst = _clearedCount == 1;
        bool wasLast = _board.Arrows.Count == 0;

        if (_trailVisible && !KeepTrailAfterClear)
        {
            SetAllTrailsVisible(false);
            TrailAutoOff?.Invoke();
        }

        ClearResult result =
            wasLast ? ClearResult.ClearedLast
            : wasFirst ? ClearResult.ClearedFirst
            : ClearResult.Cleared;

        view.PlayPullOut(onComplete: () =>
        {
            Destroy(view.gameObject);
            if (wasLast)
                BoardCleared?.Invoke();
        });

        return result;
    }

    private ClearResult TryClearArrowCulled(Arrow arrow)
    {
        if (!_arrowChunks.ContainsKey(arrow))
            return ClearResult.Blocked;

        ClearPreviousTints();

        if (!_board.IsClearable(arrow))
        {
            // Spawn a temporary ArrowView for blocked feedback if visible.
            ArrowView view = PromoteToInteractionView(arrow);
            if (view != null)
                PlayBlockedFeedback(arrow, view);
            return ClearResult.Blocked;
        }

        // Promote to interaction view for animation, then remove from chunks.
        ArrowView animView = PromoteToInteractionView(arrow);
        RemoveArrowFromChunks(arrow);
        if (_arrowRemover != null)
            _arrowRemover(arrow);
        else
            _board.RemoveArrow(arrow);
        _clearedCount++;
        bool wasFirst = _clearedCount == 1;
        bool wasLast = _board.Arrows.Count == 0;

        if (_trailVisible && !KeepTrailAfterClear)
        {
            SetAllTrailsVisible(false);
            TrailAutoOff?.Invoke();
        }

        ClearResult result =
            wasLast ? ClearResult.ClearedLast
            : wasFirst ? ClearResult.ClearedFirst
            : ClearResult.Cleared;

        if (animView != null)
        {
            animView.PlayPullOut(onComplete: () =>
            {
                _interactionViews.Remove(arrow);
                Destroy(animView.gameObject);
                if (wasLast)
                    BoardCleared?.Invoke();
            });
        }
        else if (wasLast)
        {
            BoardCleared?.Invoke();
        }

        return result;
    }

    /// <summary>
    /// Spawns an individual ArrowView for an arrow that needs interaction
    /// (clear/blocked animation). Hides the arrow from chunk meshes so the
    /// individual view is the sole renderer.
    /// </summary>
    private ArrowView PromoteToInteractionView(Arrow arrow)
    {
        if (_interactionViews.TryGetValue(arrow, out ArrowView existing))
            return existing;

        // Hide arrow in all chunks containing it.
        if (_arrowChunks.TryGetValue(arrow, out var keys))
        {
            foreach (int key in keys)
                if (_chunks.TryGetValue(key, out var chunk))
                    chunk.HideArrow(arrow);
        }

        var go = new GameObject($"Interaction_{arrow.HeadCell.X}_{arrow.HeadCell.Y}");
        go.transform.SetParent(_arrowParent, false);
        var view = go.AddComponent<ArrowView>();
        view.Init(arrow, _board.Width, _board.Height, _settings);

        // Render above chunk meshes so animations are never occluded by stale chunks.
        // Also kill dome highlight to match the solid look of chunk meshes.
        foreach (var renderer in go.GetComponentsInChildren<MeshRenderer>())
        {
            renderer.sortingOrder += 10;
            if (renderer.material.HasProperty("_HighlightStrength"))
                renderer.material.SetFloat("_HighlightStrength", 0f);
        }

        if (_arrowColors.TryGetValue(arrow, out Color color))
            view.SetBaseColor(color, color);

        _interactionViews[arrow] = view;
        return view;
    }

    private void RemoveArrowFromChunks(Arrow arrow)
    {
        if (_arrowChunks.TryGetValue(arrow, out var keys))
        {
            foreach (int key in keys)
                if (_chunks.TryGetValue(key, out var chunk))
                    chunk.RemoveArrow(arrow);
            _arrowChunks.Remove(arrow);
        }
        _arrowColors.Remove(arrow);
        _trailRenderer.RemoveArrow(arrow);
    }

    /// <summary>
    /// Call after recording the final clear event to fire <see cref="LastArrowClearing"/>.
    /// </summary>
    public void NotifyLastArrowClearing() => LastArrowClearing?.Invoke();

    /// <summary>
    /// Removes an arrow without animation. Used during resume clear replay.
    /// </summary>
    public void RemoveArrowView(Arrow arrow)
    {
        if (_useCulling)
        {
            if (_interactionViews.TryGetValue(arrow, out ArrowView iv))
            {
                _interactionViews.Remove(arrow);
                Destroy(iv.gameObject);
            }
            RemoveArrowFromChunks(arrow);
            return;
        }

        if (_arrowViews.TryGetValue(arrow, out ArrowView av))
        {
            _arrowViews.Remove(arrow);
            Destroy(av.gameObject);
        }
    }

    /// <summary>
    /// Returns the ArrowView for a given arrow, or null if not found.
    /// In culling mode, only returns interaction views (chunk-rendered arrows have no individual view).
    /// </summary>
    public ArrowView GetArrowView(Arrow arrow)
    {
        if (_useCulling)
            return _interactionViews.TryGetValue(arrow, out ArrowView v) ? v : null;

        return _arrowViews.TryGetValue(arrow, out ArrowView av) ? av : null;
    }

    /// <summary>
    /// Removes an arrow from the view dictionary and plays the pull-out animation.
    /// Used by the replay viewer and by co-op for remote clears. Optional
    /// <paramref name="flashColor"/> briefly tints the arrow at the start of
    /// the slide — co-op uses it to attribute clears to player colors.
    /// </summary>
    public void ClearArrowAnimated(Arrow arrow, Color? flashColor = null)
    {
        if (_useCulling)
        {
            if (!_arrowChunks.ContainsKey(arrow))
                return;

            ArrowView view = PromoteToInteractionView(arrow);
            RemoveArrowFromChunks(arrow);
            view.PlayPullOut(
                onComplete: () =>
                {
                    _interactionViews.Remove(arrow);
                    Destroy(view.gameObject);
                },
                flashColor: flashColor
            );
            return;
        }

        if (!_arrowViews.TryGetValue(arrow, out ArrowView av))
            return;

        _arrowViews.Remove(arrow);
        av.PlayPullOut(onComplete: () => Destroy(av.gameObject), flashColor: flashColor);
    }

    /// <summary>
    /// Highlights all currently clearable arrows.
    /// </summary>
    public void UpdateClearableHighlights(Board board, bool showTrails = false)
    {
        _highlightsActive = true;

        if (_useCulling)
        {
            foreach (var kvp in _arrowChunks)
            {
                Arrow arrow = kvp.Key;
                bool clearable = board.IsClearable(arrow);
                Color color = clearable
                    ? HighlightColor
                    : (_arrowColors.TryGetValue(arrow, out Color c) ? c : _settings.arrowBodyColor);
                foreach (int key in kvp.Value)
                    if (_chunks.TryGetValue(key, out var chunk))
                        chunk.SetArrowColor(arrow, color);
            }
            return;
        }

        foreach (var kvp in _arrowViews)
        {
            bool clearable = board.IsClearable(kvp.Key);
            kvp.Value.SetHighlight(clearable);
            if (showTrails)
                kvp.Value.SetTrailVisible(clearable);
        }
    }

    /// <summary>
    /// Removes highlight tint from all arrow views, restoring base colors.
    /// </summary>
    public void ClearAllHighlights()
    {
        _highlightsActive = false;

        if (_useCulling)
        {
            foreach (var kvp in _arrowChunks)
            {
                Arrow arrow = kvp.Key;
                Color color = _arrowColors.TryGetValue(arrow, out Color c)
                    ? c
                    : _settings.arrowBodyColor;
                foreach (int key in kvp.Value)
                    if (_chunks.TryGetValue(key, out var chunk))
                        chunk.SetArrowColor(arrow, color);
            }
            return;
        }

        foreach (var view in _arrowViews.Values)
            view.SetHighlight(false);
    }

    /// <summary>
    /// Shows or hides trail lines on all remaining arrows. In culling mode,
    /// trails are rendered as a single combined mesh via <see cref="BoardTrailRenderer"/>.
    /// </summary>
    public void SetAllTrailsVisible(bool visible)
    {
        _trailVisible = visible;

        if (_useCulling)
        {
            if (_trailRenderer != null)
                _trailRenderer.gameObject.SetActive(visible);
            return;
        }

        foreach (ArrowView view in _arrowViews.Values)
            view.SetTrailVisible(visible);
    }

    // -- Culling internals ------------------------------------------------------

    private void OnCameraChanged()
    {
        if (_cameraController == null)
            return;

        Rect worldRect = _cameraController.GetVisibleWorldRect();

        // Compute visible chunk range with 1-chunk padding.
        int minX = Mathf.FloorToInt(worldRect.xMin) - 1;
        int minY = Mathf.FloorToInt(worldRect.yMin) - 1;
        int maxX = Mathf.CeilToInt(worldRect.xMax) + 1;
        int maxY = Mathf.CeilToInt(worldRect.yMax) + 1;

        int bMinX = Mathf.Max(minX / BoardSpatialIndex.BucketSize, 0);
        int bMinY = Mathf.Max(minY / BoardSpatialIndex.BucketSize, 0);
        int bMaxX = Mathf.Min(maxX / BoardSpatialIndex.BucketSize, _bucketsX - 1);
        int bMaxY = Mathf.Min(maxY / BoardSpatialIndex.BucketSize, _bucketsY - 1);

        var visibleKeys = new HashSet<int>();
        for (int by = bMinY; by <= bMaxY; by++)
        {
            for (int bx = bMinX; bx <= bMaxX; bx++)
            {
                visibleKeys.Add(by * _bucketsX + bx);
            }
        }

        // Activate visible chunks, deactivate non-visible.
        foreach (var kvp in _chunks)
        {
            bool shouldBeActive = visibleKeys.Contains(kvp.Key);
            if (kvp.Value.gameObject.activeSelf != shouldBeActive)
                kvp.Value.gameObject.SetActive(shouldBeActive);
            if (shouldBeActive)
                kvp.Value.RebuildIfDirty();
        }
    }

    private void SpawnArrowView(Arrow arrow)
    {
        var go = new GameObject($"Arrow_{arrow.HeadCell.X}_{arrow.HeadCell.Y}");
        go.transform.SetParent(_arrowParent, false);

        var view = go.AddComponent<ArrowView>();
        view.Init(arrow, _board.Width, _board.Height, _settings);
        _arrowViews[arrow] = view;
    }

    private void PlayBlockedFeedback(Arrow arrow, ArrowView view)
    {
        Arrow blocker = _board.GetFirstInRay(arrow);
        if (blocker != null)
        {
            (int dx, int dy) = Arrow.GetDirectionStep(arrow.HeadDirection);
            Cell cursor = new(arrow.HeadCell.X + dx, arrow.HeadCell.Y + dy);
            int cellDistance = 1;
            while (_board.Contains(cursor))
            {
                if (_board.GetArrowAt(cursor) == blocker)
                    break;
                cursor = new(cursor.X + dx, cursor.Y + dy);
                cellDistance++;
            }
            float contactArcLength = cellDistance - 0.5f;

            view.SetBlockedTint(_settings.blockedTintIntensity, _settings.rejectFlashColor);
            _tintedSource = view;

            // In culling mode, blocker may need promotion to interaction view too.
            ArrowView blockerView = GetArrowView(blocker);
            if (blockerView == null && _useCulling && _arrowChunks.ContainsKey(blocker))
                blockerView = PromoteToInteractionView(blocker);
            if (blockerView != null)
            {
                blockerView.SetBlockedTint(
                    _settings.blockedTintIntensity,
                    _settings.rejectFlashColor
                );
                _tintedBlocker = blockerView;
            }

            view.PlayBump(contactArcLength);
        }
        else
        {
            view.PlayRejectFlash();
        }
    }

    private void ClearPreviousTints()
    {
        if (_tintedSource != null)
        {
            _tintedSource.ClearBlockedTint();
            DemoteInteractionViewIfTemporary(_tintedSource);
            _tintedSource = null;
        }
        if (_tintedBlocker != null)
        {
            _tintedBlocker.ClearBlockedTint();
            DemoteInteractionViewIfTemporary(_tintedBlocker);
            _tintedBlocker = null;
        }
    }

    /// <summary>
    /// In culling mode, blocked-feedback creates temporary interaction views.
    /// When the tint is cleared, the views must be returned to the chunk meshes
    /// (unhide arrow + destroy temp view).
    /// </summary>
    private void DemoteInteractionViewIfTemporary(ArrowView view)
    {
        if (!_useCulling || view == null)
            return;

        Arrow arrow = view.Arrow;
        if (arrow == null)
            return;

        // Only demote if this view is currently in _interactionViews and the
        // arrow is still registered in chunks (not cleared).
        if (!_interactionViews.TryGetValue(arrow, out ArrowView tracked) || tracked != view)
            return;
        if (!_arrowChunks.ContainsKey(arrow))
            return;

        // Unhide in chunks.
        if (_arrowChunks.TryGetValue(arrow, out var keys))
        {
            foreach (int key in keys)
                if (_chunks.TryGetValue(key, out var chunk))
                    chunk.UnhideArrow(arrow);
        }

        _interactionViews.Remove(arrow);
        Destroy(view.gameObject);
    }

    private void Update()
    {
        if (!_useCulling || _cameraController == null)
            return;

        // Per-frame: re-evaluate visible chunks (handles new chunks from
        // mid-frame AddArrow during generation) and rebuild dirty ones.
        OnCameraChanged();
    }

    private void OnDestroy()
    {
        if (_cameraController != null)
            _cameraController.CameraChanged -= OnCameraChanged;
    }
}
