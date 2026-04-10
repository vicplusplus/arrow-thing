using System;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.EnhancedTouch;
using Touch = UnityEngine.InputSystem.EnhancedTouch.Touch;

/// <summary>
/// Unified input handler for both PC and mobile. Uses the Input System Gameplay
/// action map. Left-click / touch is disambiguated into tap (select arrow) vs
/// drag (pan camera) by a configurable screen-space distance threshold.
/// </summary>
public sealed class InputHandler : MonoBehaviour
{
    private float _dragThresholdPixels;

    private Board _board = null!;
    private BoardView _boardView = null!;
    private CameraController _camCtrl = null!;
    private GameTimer _timer;
    private ReplayRecorder _recorder;
    private Action _onArrowCleared;
    private Action _onQuickReset;
    private Action _onQuickSave;
    private Action _onToggleTrail;

    private InputAction _pointAction = null!;
    private InputAction _selectAction = null!;
    private InputAction _zoomAction = null!;

    private Vector2 _pressStartScreen;
    private Vector3 _pressStartWorld;
    private bool _isDragging;
    private bool _isPressed;
    private bool _inputEnabled = true;

    // Pinch state
    private float _lastPinchDistance;

    public void SetInputEnabled(bool enabled)
    {
        _inputEnabled = enabled;
    }

    public void Init(
        Board board,
        BoardView boardView,
        CameraController camCtrl,
        float dragThresholdPixels = 15f,
        GameTimer timer = null,
        ReplayRecorder recorder = null,
        Action onArrowCleared = null,
        Action onQuickReset = null,
        Action onQuickSave = null,
        Action onToggleTrail = null
    )
    {
        _board = board;
        _boardView = boardView;
        _camCtrl = camCtrl;
        _dragThresholdPixels = dragThresholdPixels;
        _timer = timer;
        _recorder = recorder;
        _onArrowCleared = onArrowCleared;
        _onQuickReset = onQuickReset;
        _onQuickSave = onQuickSave;
        _onToggleTrail = onToggleTrail;

        var km = KeybindManager.Instance;
        _pointAction = km.Point;
        _selectAction = km.Select;
        _zoomAction = km.Zoom;

        EnhancedTouchSupport.Enable();
    }

    private void OnDestroy()
    {
        if (EnhancedTouchSupport.enabled)
            EnhancedTouchSupport.Disable();
    }

    private void Update()
    {
        if (!_inputEnabled)
            return;

        HandleTouchPinch();
        HandleScrollZoom();
        HandleSelectAndPan();
        HandleKeybinds();
    }

    private void HandleKeybinds()
    {
        var km = KeybindManager.Instance;
        if (km == null)
            return;

        if (km.QuickReset.WasPerformedThisFrame())
            _onQuickReset?.Invoke();

        if (km.ToggleTrail.WasPerformedThisFrame())
            _onToggleTrail?.Invoke();

        if (km.ClickHovered.WasPerformedThisFrame())
        {
            Vector2 screenPos = _pointAction.ReadValue<Vector2>();
            HandleTap(screenPos);
        }

        if (km.QuickSave.WasPerformedThisFrame())
            _onQuickSave?.Invoke();
    }

    private void HandleSelectAndPan()
    {
        if (_selectAction.WasPressedThisFrame())
        {
            _pressStartScreen = _pointAction.ReadValue<Vector2>();
            _pressStartWorld = _camCtrl.Cam.ScreenToWorldPoint(_pressStartScreen);
            _isDragging = false;
            _isPressed = true;
        }

        if (_isPressed && _selectAction.IsPressed())
        {
            Vector2 currentScreen = _pointAction.ReadValue<Vector2>();
            float dist = Vector2.Distance(_pressStartScreen, currentScreen);

            if (!_isDragging && dist >= _dragThresholdPixels)
            {
                _isDragging = true;
                // Reset origin to current position so the camera doesn't snap
                // by the full threshold distance on the first drag frame.
                _pressStartWorld = _camCtrl.Cam.ScreenToWorldPoint(currentScreen);
            }

            if (_isDragging)
            {
                Vector3 currentWorld = _camCtrl.Cam.ScreenToWorldPoint(currentScreen);
                Vector3 delta = _pressStartWorld - currentWorld;
                _camCtrl.Pan(delta);
                // Recalculate start world pos after pan so dragging feels smooth
                _pressStartWorld = _camCtrl.Cam.ScreenToWorldPoint(currentScreen);
            }
        }

        if (_isPressed && _selectAction.WasReleasedThisFrame())
        {
            _isPressed = false;

            if (!_isDragging)
                HandleTap(_pressStartScreen);
        }
    }

    private void HandleTap(Vector2 screenPos)
    {
        Vector3 worldPos = _camCtrl.Cam.ScreenToWorldPoint(screenPos);
        Cell cell = BoardCoords.WorldToCell(worldPos, _board.Width, _board.Height);

        if (!_board.Contains(cell))
            return;

        Arrow arrow = _board.GetArrowAt(cell);
        if (arrow == null)
        {
            if (_recorder != null)
                _recorder.RecordMiss(worldPos.x, worldPos.y);
            return;
        }

        double wallTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;

        // Any arrow tap starts the solve timer (ends inspection)
        bool wasInspecting = _timer != null && !_timer.IsSolving;
        if (wasInspecting)
        {
            _timer.StartSolve(wallTime);
            Debug.Log("[InputHandler] Inspection ended — solve timer started");
            if (_recorder != null)
                _recorder.RecordStartSolve();
        }

        ClearResult result = _boardView.TryClearArrow(arrow);

        if (result != ClearResult.Blocked)
        {
            if (_recorder != null)
                _recorder.RecordClear(worldPos.x, worldPos.y);

            if (result == ClearResult.ClearedLast)
            {
                if (_timer != null)
                    _timer.Finish(wallTime);
                Debug.Log(
                    $"[InputHandler] Last arrow cleared — timer finished, solveElapsed={(_timer != null ? _timer.SolveElapsed.ToString("F3") : "N/A")}s"
                );
                _boardView.NotifyLastArrowClearing();
            }
            else
            {
                _onArrowCleared?.Invoke();
            }
        }
        else
        {
            if (_recorder != null)
                _recorder.RecordReject(worldPos.x, worldPos.y);
        }
    }

    private void HandleScrollZoom()
    {
        float scroll = _zoomAction.ReadValue<float>();
        if (!Mathf.Approximately(scroll, 0f))
            _camCtrl.Zoom(scroll);
    }

    private void HandleTouchPinch()
    {
        if (Touch.activeTouches.Count < 2)
        {
            _lastPinchDistance = 0f;
            return;
        }

        var touch0 = Touch.activeTouches[0];
        var touch1 = Touch.activeTouches[1];
        float currentDistance = Vector2.Distance(touch0.screenPosition, touch1.screenPosition);

        if (_lastPinchDistance > 0f && currentDistance > 0f)
        {
            float ratio = currentDistance / _lastPinchDistance;
            _camCtrl.PinchZoom(ratio);
        }

        _lastPinchDistance = currentDistance;

        // Suppress tap when pinching
        _isDragging = true;
    }
}
