using System;
using System.Collections;
using UnityEngine;

/// <summary>
/// Visual representation of a single arrow. Owns the procedural body mesh
/// and a separate arrowhead child object. Handles clear, bump, and reject
/// flash animations via arc-length window sliding.
/// </summary>
public sealed class ArrowView : MonoBehaviour
{
    private MeshFilter _meshFilter = null!;
    private MeshRenderer _meshRenderer = null!;
    private Material _materialInstance = null!;
    private Material _headMaterialInstance = null!;
    private VisualSettings _settings = null!;
    private GameObject _arrowHead = null!;

    // Cached path and geometry for animation
    private Vector3[] _path = null!;
    private float[] _arcLengths = null!;
    private float _totalArcLength;
    private float _extendedArcLength;
    private float _bodyWidth;
    private Vector3 _headDir;
    private Vector3 _initialHeadPos;

    private static readonly int FlashTId = Shader.PropertyToID("_FlashT");
    private static readonly int FlashColorId = Shader.PropertyToID("_FlashColor");
    private static readonly int ColorId = Shader.PropertyToID("_Color");

    private Color _baseBodyColor;
    private Color _baseHeadColor;
    private GameObject _trailLine;

    public Arrow Arrow { get; private set; } = null!;

    /// <summary>
    /// Initializes the arrow view with its domain arrow and visual settings.
    /// </summary>
    public void Init(Arrow arrow, int boardWidth, int boardHeight, VisualSettings settings)
    {
        Arrow = arrow;
        _settings = settings;
        _bodyWidth = settings.arrowBodyWidth;

        // Body mesh
        if (_meshFilter == null)
            _meshFilter = gameObject.AddComponent<MeshFilter>();
        if (_meshRenderer == null)
            _meshRenderer = gameObject.AddComponent<MeshRenderer>();

        _meshRenderer.material = settings.arrowBodyMaterial;
        _materialInstance = _meshRenderer.material;

        _baseBodyColor = settings.arrowBodyColor;
        _materialInstance.SetColor(ColorId, _baseBodyColor);
        _materialInstance.SetColor(FlashColorId, settings.rejectFlashColor);
        _materialInstance.SetFloat(FlashTId, 0f);

        // Build the original path and compute arc lengths
        Vector3[] originalPath = BoardCoords.ArrowPathToWorld(arrow, boardWidth, boardHeight);
        _headDir = (originalPath[0] - originalPath[1]).normalized;
        _initialHeadPos = originalPath[0];

        // Compute arc lengths of the original path
        _totalArcLength = 0f;
        for (int i = 1; i < originalPath.Length; i++)
            _totalArcLength += Vector3.Distance(originalPath[i - 1], originalPath[i]);

        // Extend path with a synthetic exit point along the head direction
        float extensionDist = Mathf.Max(boardWidth, boardHeight) * settings.pathExtensionMultiplier;
        Vector3 exitPoint = originalPath[0] + _headDir * extensionDist;

        _path = new Vector3[originalPath.Length + 1];
        // path[0] is the extension point (farthest from board), original head becomes path[1]
        _path[0] = exitPoint;
        for (int i = 0; i < originalPath.Length; i++)
            _path[i + 1] = originalPath[i];

        // Compute arc lengths for the extended path
        _arcLengths = new float[_path.Length];
        _arcLengths[0] = 0f;
        for (int i = 1; i < _path.Length; i++)
            _arcLengths[i] = _arcLengths[i - 1] + Vector3.Distance(_path[i - 1], _path[i]);
        _extendedArcLength = _arcLengths[_arcLengths.Length - 1];

        // Build initial body mesh using the original path (no extension visible)
        Mesh bodyMesh = ArrowMeshBuilder.Build(originalPath, _bodyWidth);
        _meshFilter.mesh = bodyMesh;
        _meshRenderer.sortingOrder = 1;

        // Arrowhead as separate child object
        _arrowHead = CreateArrowHead(originalPath, settings);
        _arrowHead.transform.SetParent(transform, true);

        _headMaterialInstance = _arrowHead.GetComponent<MeshRenderer>().material;
        _baseHeadColor = settings.arrowHeadColor;
        _headMaterialInstance.SetColor(ColorId, _baseHeadColor);
        _headMaterialInstance.SetColor(FlashColorId, settings.rejectFlashColor);
        _headMaterialInstance.SetFloat(FlashTId, 0f);

        // Trail line (hidden by default)
        _trailLine = CreateTrailLine(settings);
        _trailLine.transform.SetParent(transform, true);
        _trailLine.SetActive(false);
    }

    private static GameObject CreateArrowHead(Vector3[] path, VisualSettings settings)
    {
        Vector3 headPos = path[0];
        Vector3 headDir = (path[0] - path[1]).normalized;
        Vector3 headPerp = new Vector3(-headDir.y, headDir.x, 0f);

        float headHalfBase = settings.arrowBodyWidth * settings.arrowHeadWidthMultiplier;

        // Build mesh centered at origin so transform.position controls placement
        Vector3 tip = headDir * settings.arrowHeadLength;
        Vector3 baseLeft = -headPerp * headHalfBase;
        Vector3 baseRight = headPerp * headHalfBase;

        var mesh = new Mesh { name = "ArrowHead" };
        mesh.vertices = new[] { baseLeft, baseRight, tip };
        mesh.triangles = new[] { 0, 1, 2 };
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();

        var go = new GameObject("ArrowHead");
        go.transform.position = headPos;

        var mf = go.AddComponent<MeshFilter>();
        mf.mesh = mesh;

        var mr = go.AddComponent<MeshRenderer>();
        mr.material =
            settings.arrowHeadMaterial != null
                ? settings.arrowHeadMaterial
                : settings.arrowBodyMaterial;
        mr.sortingOrder = 2;

        return go;
    }

    private GameObject CreateTrailLine(VisualSettings settings)
    {
        // Reuse the already-computed _path and _arcLengths.
        // _path[0] is the exit point (head + headDir * extensionDist), _path[1] is the original head.
        // The segment [0, extensionDist] is a straight line from the exit point back to the head,
        // i.e. the full trail ray extending to the edge of the visible area.
        float extensionDist = _arcLengths[1];
        Mesh trailMesh = ArrowMeshBuilder.Build(_path, _bodyWidth, 0f, extensionDist);

        var go = new GameObject("TrailLine");
        var mf = go.AddComponent<MeshFilter>();
        mf.mesh = trailMesh;

        var mr = go.AddComponent<MeshRenderer>();
        mr.material =
            settings.arrowTrailMaterial != null
                ? settings.arrowTrailMaterial
                : settings.arrowBodyMaterial;
        mr.material.SetColor(ColorId, settings.trailColor);
        mr.sortingOrder = 0; // below arrow body (1) and head (2)

        return go;
    }

    // ---- Public state methods ---------------------------------------------

    /// <summary>
    /// Overrides the base body and head colors (e.g. for map-coloring palette).
    /// </summary>
    public void SetBaseColor(Color bodyColor, Color headColor)
    {
        _baseBodyColor = bodyColor;
        _baseHeadColor = headColor;
        _materialInstance.SetColor(ColorId, _baseBodyColor);
        _headMaterialInstance.SetColor(ColorId, _baseHeadColor);
    }

    /// <summary>
    /// Applies a persistent tint toward the reject flash color to indicate a blocked attempt.
    /// The tint blends on top of _Color only — _FlashT animations play independently.
    /// </summary>
    public void SetBlockedTint(float intensity, Color tintColor)
    {
        Color body = Color.Lerp(_baseBodyColor, tintColor, intensity);
        Color head = Color.Lerp(_baseHeadColor, tintColor, intensity);
        _materialInstance.SetColor(ColorId, body);
        _headMaterialInstance.SetColor(ColorId, head);
    }

    /// <summary>
    /// Restores the arrow to its base color, clearing any blocked tint.
    /// </summary>
    public void ClearBlockedTint()
    {
        _materialInstance.SetColor(ColorId, _baseBodyColor);
        _headMaterialInstance.SetColor(ColorId, _baseHeadColor);
    }

    /// <summary>
    /// Applies or removes a bright green highlight tint to indicate clearability.
    /// </summary>
    public void SetHighlight(bool highlighted)
    {
        if (highlighted)
        {
            Color green = new(0f, 0.875f, 1f); // #00DFFF — electric cyan
            _materialInstance.SetColor(ColorId, green);
            _headMaterialInstance.SetColor(ColorId, green);
        }
        else
        {
            _materialInstance.SetColor(ColorId, _baseBodyColor);
            _headMaterialInstance.SetColor(ColorId, _baseHeadColor);
        }
    }

    /// <summary>
    /// Shows or hides the trail line extending from the arrow head.
    /// </summary>
    public void SetTrailVisible(bool visible)
    {
        if (_trailLine != null)
            _trailLine.SetActive(visible);
    }

    // ---- Pending-mode rendering (endless mode) ----------------------------

    // Pending mode modulates material alpha as a sine wave so the arrow reads
    // as "about to commit" without being unreadable. The existing ArrowBody
    // shader (Assets/Art/Shaders/ArrowBody.shader) already supports alpha
    // (Transparent queue + SrcAlpha blend), so no material reconfiguration is
    // needed — just write _Color.a per frame. See docs/TODO.md
    // (Endless Mode → Pending arrow visuals) for spec.
    private bool _isPending;
    private const float PendingAlphaBase = 0.12f;
    private const float PendingAlphaAmplitude = 0.08f;
    private const float PendingWavelengthSeconds = 1f;

    /// <summary>
    /// Enables pending rendering: body and head material alpha modulated as
    /// <c>0.12 ± 0.08 * sin(2π * t / 1s)</c>. Phase is shared across pending
    /// arrows (driven by <see cref="Time.time"/>) so simultaneous ones pulse
    /// in unison.
    /// </summary>
    public void EnterPendingMode()
    {
        _isPending = true;
        // Hide trail line on pending arrows — they're not yet blocking
        // anything, a trail would be visual noise.
        SetTrailVisible(false);
    }

    /// <summary>
    /// Disables pending rendering and restores full opacity. Called on commit
    /// (pending becomes real) and before returning to the view pool.
    /// </summary>
    public void ExitPendingMode()
    {
        _isPending = false;
        Color body = _materialInstance.GetColor(ColorId);
        _materialInstance.SetColor(ColorId, new Color(body.r, body.g, body.b, 1f));
        Color head = _headMaterialInstance.GetColor(ColorId);
        _headMaterialInstance.SetColor(ColorId, new Color(head.r, head.g, head.b, 1f));
    }

    private void Update()
    {
        if (!_isPending)
            return;

        float t = Time.time * 2f * Mathf.PI / PendingWavelengthSeconds;
        float alpha = PendingAlphaBase + PendingAlphaAmplitude * Mathf.Sin(t);

        Color body = _materialInstance.GetColor(ColorId);
        _materialInstance.SetColor(ColorId, new Color(body.r, body.g, body.b, alpha));
        Color head = _headMaterialInstance.GetColor(ColorId);
        _headMaterialInstance.SetColor(ColorId, new Color(head.r, head.g, head.b, alpha));
    }

    // ---- Pooling support --------------------------------------------------

    /// <summary>
    /// Reinitializes this view to represent a different arrow, reusing the
    /// existing GameObject and components. Destroys old child objects and
    /// rebuilds geometry. Used by the viewport-culling ArrowView pool.
    /// </summary>
    public void Reinit(Arrow arrow, int boardWidth, int boardHeight, VisualSettings settings)
    {
        StopAllCoroutines();
        gameObject.SetActive(true);
        _isPending = false;

        // Destroy old child objects (arrowhead, trail)
        if (_arrowHead != null)
            Destroy(_arrowHead);
        if (_trailLine != null)
            Destroy(_trailLine);

        Init(arrow, boardWidth, boardHeight, settings);
    }

    /// <summary>
    /// Deactivates this view for return to the pool. Stops animations and
    /// hides the GameObject.
    /// </summary>
    public void Deactivate()
    {
        StopAllCoroutines();
        gameObject.SetActive(false);
    }

    // ---- Animation helpers ------------------------------------------------

    /// <summary>
    /// Samples a world-space position along the extended path at a given arc length.
    /// </summary>
    private Vector3 SamplePathAtArcLength(float arcLength)
    {
        if (arcLength <= 0f)
            return _path[0];
        if (arcLength >= _extendedArcLength)
            return _path[_path.Length - 1];

        for (int i = 1; i < _path.Length; i++)
        {
            if (_arcLengths[i] >= arcLength)
            {
                float segStart = _arcLengths[i - 1];
                float segEnd = _arcLengths[i];
                float t = (arcLength - segStart) / (segEnd - segStart);
                return Vector3.Lerp(_path[i - 1], _path[i], t);
            }
        }

        return _path[_path.Length - 1];
    }

    /// <summary>
    /// Applies a slideOffset to the body mesh and arrowhead position.
    /// The extended path is laid out as [exitPoint, originalHead, ...originalTail].
    /// slideOffset=0 means the arrow is at rest; positive values slide head-first toward the exit.
    /// </summary>
    private void ApplySlideOffset(float slideOffset)
    {
        // The original arrow occupies the tail end of the extended path.
        // At rest: windowStart = extensionDist, windowEnd = extendedArcLength
        // extensionDist = _arcLengths[1] (distance from exit point to original head)
        float extensionDist = _arcLengths[1];
        float windowStart = extensionDist - slideOffset;
        float windowEnd = windowStart + _totalArcLength;

        // Clamp to extended path bounds
        windowStart = Mathf.Max(0f, windowStart);
        windowEnd = Mathf.Min(_extendedArcLength, windowEnd);

        _meshFilter.mesh = ArrowMeshBuilder.Build(_path, _bodyWidth, windowStart, windowEnd);

        // Position arrowhead at the leading edge (windowStart is the head end in the extended path)
        _arrowHead.transform.position = SamplePathAtArcLength(windowStart);
    }

    // ---- Animations -------------------------------------------------------

    /// <summary>
    /// Plays the pull-out animation for a clearable arrow. The arrow slides
    /// head-first off the board and is destroyed on completion. Optional
    /// <paramref name="flashColor"/> briefly tints the arrow at the start of
    /// the slide — used by co-op remote clears to attribute the clear to
    /// another player's color.
    /// </summary>
    public void PlayPullOut(Action onComplete = null, Color? flashColor = null)
    {
        StopAllCoroutines();
        StartCoroutine(PullOutCoroutine(onComplete, flashColor));
    }

    private IEnumerator PullOutCoroutine(Action onComplete, Color? flashColor)
    {
        float duration = _settings.clearSlideDuration;
        AnimationCurve curve = _settings.clearSlideCurve;
        float extensionDist = _arcLengths[1];
        float maxOffset = extensionDist + _totalArcLength;
        float elapsed = 0f;

        // Flash envelope: ramp from 1 → 0 over the first ~30 % of the slide.
        // Uses the existing _FlashColor / _FlashT shader pair, temporarily
        // overriding the color to the attribution tint.
        bool hasFlash = flashColor.HasValue;
        float flashDuration = duration * 0.3f;
        if (hasFlash)
        {
            _materialInstance.SetColor(FlashColorId, flashColor.Value);
            _headMaterialInstance.SetColor(FlashColorId, flashColor.Value);
        }

        while (elapsed < duration)
        {
            float t = curve.Evaluate(elapsed / duration);
            float slideOffset = Mathf.Lerp(0f, maxOffset, t);
            ApplySlideOffset(slideOffset);

            if (hasFlash)
            {
                float flashT =
                    elapsed < flashDuration ? 1f - Mathf.Clamp01(elapsed / flashDuration) : 0f;
                _materialInstance.SetFloat(FlashTId, flashT);
                _headMaterialInstance.SetFloat(FlashTId, flashT);
            }

            elapsed += Time.deltaTime;
            yield return null;
        }

        if (hasFlash)
        {
            _materialInstance.SetFloat(FlashTId, 0f);
            _headMaterialInstance.SetFloat(FlashTId, 0f);
            _materialInstance.SetColor(FlashColorId, _settings.rejectFlashColor);
            _headMaterialInstance.SetColor(FlashColorId, _settings.rejectFlashColor);
        }

        onComplete?.Invoke();
    }

    /// <summary>
    /// Plays the bump animation for a blocked arrow. The arrow slides toward
    /// the blocker, bumps, and returns. Reject flash fires on contact.
    /// </summary>
    public void PlayBump(float contactArcLength)
    {
        StopAllCoroutines();
        StartCoroutine(BumpCoroutine(contactArcLength));
    }

    private IEnumerator BumpCoroutine(float contactArcLength)
    {
        // Phase 1: Slide to contact
        float elapsed = 0f;
        float slideDuration = _settings.bumpSlideDuration;
        AnimationCurve slideCurve = _settings.bumpSlideCurve;

        while (elapsed < slideDuration)
        {
            float t = slideCurve.Evaluate(elapsed / slideDuration);
            ApplySlideOffset(Mathf.Lerp(0f, contactArcLength, t));
            elapsed += Time.deltaTime;
            yield return null;
        }

        // Phase 2: Bump overshoot — fire reject flash
        StartCoroutine(RejectFlashCoroutine());

        elapsed = 0f;
        float bumpDuration = _settings.bumpDuration;
        AnimationCurve bumpCurve = _settings.bumpCurve;
        float magnitude = _settings.bumpMagnitude;

        while (elapsed < bumpDuration)
        {
            float t = bumpCurve.Evaluate(elapsed / bumpDuration);
            // Curve goes 0→1→0: overshoot past contact then back to contact
            ApplySlideOffset(contactArcLength + magnitude * t);
            elapsed += Time.deltaTime;
            yield return null;
        }

        // Phase 3: Return to original position
        elapsed = 0f;
        float returnDuration = _settings.bumpReturnDuration;
        AnimationCurve returnCurve = _settings.bumpReturnCurve;

        while (elapsed < returnDuration)
        {
            float t = returnCurve.Evaluate(elapsed / returnDuration);
            ApplySlideOffset(Mathf.Lerp(contactArcLength, 0f, t));
            elapsed += Time.deltaTime;
            yield return null;
        }

        // Ensure final state is exactly the original position
        ApplySlideOffset(0f);
    }

    /// <summary>
    /// Plays the reject flash animation by driving _FlashT on both material instances.
    /// </summary>
    public void PlayRejectFlash()
    {
        StopAllCoroutines();
        StartCoroutine(RejectFlashCoroutine());
    }

    private IEnumerator RejectFlashCoroutine()
    {
        float elapsed = 0f;
        float duration = _settings.rejectFlashDuration;
        AnimationCurve curve = _settings.rejectFlashCurve;

        while (elapsed < duration)
        {
            float t = curve.Evaluate(elapsed / duration);
            _materialInstance.SetFloat(FlashTId, t);
            _headMaterialInstance.SetFloat(FlashTId, t);
            elapsed += Time.deltaTime;
            yield return null;
        }

        _materialInstance.SetFloat(FlashTId, 0f);
        _headMaterialInstance.SetFloat(FlashTId, 0f);
    }
}
