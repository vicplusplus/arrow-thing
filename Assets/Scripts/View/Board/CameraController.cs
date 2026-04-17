using System.Collections;
using UnityEngine;

/// <summary>
/// Orthographic camera controller. Exposes Pan/Zoom methods — input polling is
/// handled by InputHandler, which calls these.
/// </summary>
public sealed class CameraController : MonoBehaviour
{
    [Header("Zoom")]
    [SerializeField]
    private float minOrthoSize = 2f;

    [SerializeField]
    private float zoomSpeed = 1f;

    [Header("Pan")]
    [SerializeField]
    private float panBuffer = 2f;

    private Camera _cam = null!;
    private Rect _panBounds;
    private float _initialOrthoSize;
    private float _maxOrthoSize;
    private Vector3 _initialPosition;

    /// <summary>
    /// Fired when the camera position or zoom changes (pan, zoom, pinch, ZoomToFit).
    /// </summary>
    public event System.Action CameraChanged;

    public Camera Cam => _cam;
    public float ZoomSpeed
    {
        get => zoomSpeed;
        set => zoomSpeed = value;
    }

    public void Init(Board board, float bufferFraction = 0.1f)
    {
        _cam = GetComponent<Camera>();
        _cam.orthographic = true;

        // Fit camera to board with buffer margin
        float boardW = board.Width;
        float boardH = board.Height;
        float buffer = Mathf.Max(boardW, boardH) * bufferFraction;
        float sizeForHeight = (boardH + buffer) * 0.5f;
        float sizeForWidth = (boardW + buffer) * 0.5f / _cam.aspect;
        _cam.orthographicSize = Mathf.Max(sizeForHeight, sizeForWidth);
        _initialOrthoSize = _cam.orthographicSize;
        _maxOrthoSize = _initialOrthoSize;

        // Board center in world space (cell coords start at 0)
        float cx = (boardW - 1) * 0.5f;
        float cy = (boardH - 1) * 0.5f;

        // Pan bounds: board extents + panBuffer, centered on board
        float halfW = boardW * 0.5f + panBuffer;
        float halfH = boardH * 0.5f + panBuffer;
        _panBounds = new Rect(cx - halfW, cy - halfH, halfW * 2f, halfH * 2f);

        _initialPosition = new Vector3(cx, cy, -10f);
        transform.position = _initialPosition;
    }

    /// <summary>
    /// Returns the camera's visible world-space rectangle.
    /// </summary>
    public Rect GetVisibleWorldRect()
    {
        float h = _cam.orthographicSize * 2f;
        float w = h * _cam.aspect;
        Vector3 p = transform.position;
        return new Rect(p.x - w * 0.5f, p.y - h * 0.5f, w, h);
    }

    /// <summary>
    /// Pans the camera by a world-space delta, clamped to board bounds.
    /// </summary>
    public void Pan(Vector3 worldDelta)
    {
        Vector3 newPos = transform.position + worldDelta;
        newPos.x = Mathf.Clamp(newPos.x, _panBounds.xMin, _panBounds.xMax);
        newPos.y = Mathf.Clamp(newPos.y, _panBounds.yMin, _panBounds.yMax);
        newPos.z = transform.position.z;
        transform.position = newPos;
        CameraChanged?.Invoke();
    }

    /// <summary>
    /// Zooms the camera by a scroll delta value (positive = zoom in).
    /// </summary>
    public void Zoom(float scrollDelta)
    {
        // Scale by current ortho size so each scroll step is a proportional zoom,
        // giving consistent feel across board sizes (6x6 through 100x100).
        float scaledSpeed = zoomSpeed * _cam.orthographicSize * 0.1f;
        _cam.orthographicSize = Mathf.Clamp(
            _cam.orthographicSize - scrollDelta * scaledSpeed,
            minOrthoSize,
            _maxOrthoSize
        );
        CameraChanged?.Invoke();
    }

    /// <summary>
    /// Zooms the camera by a pinch ratio (> 1 = zoom in, &lt; 1 = zoom out).
    /// </summary>
    public void PinchZoom(float ratio)
    {
        _cam.orthographicSize = Mathf.Clamp(
            _cam.orthographicSize / ratio,
            minOrthoSize,
            _maxOrthoSize
        );
        CameraChanged?.Invoke();
    }

    /// <summary>
    /// Smoothly zooms out to the initial fit-to-board view and re-centers.
    /// </summary>
    public void ZoomToFit(float duration, System.Action onComplete = null)
    {
        StartCoroutine(ZoomToFitCoroutine(duration, onComplete));
    }

    private IEnumerator ZoomToFitCoroutine(float duration, System.Action onComplete)
    {
        float startSize = _cam.orthographicSize;
        Vector3 startPos = transform.position;
        Vector3 targetPos = new(_initialPosition.x, _initialPosition.y, startPos.z);
        float elapsed = 0f;

        while (elapsed < duration)
        {
            elapsed += Time.deltaTime;
            float t = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(elapsed / duration));
            _cam.orthographicSize = Mathf.Lerp(startSize, _initialOrthoSize, t);
            transform.position = Vector3.Lerp(startPos, targetPos, t);
            CameraChanged?.Invoke();
            yield return null;
        }

        _cam.orthographicSize = _initialOrthoSize;
        transform.position = targetPos;
        CameraChanged?.Invoke();
        onComplete?.Invoke();
    }
}
