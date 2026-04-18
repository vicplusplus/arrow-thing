using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Object pool for tap indicator rings shown during replay playback.
/// Each indicator expands and fades out over a short duration.
/// </summary>
public sealed class TapIndicatorPool
{
    private const int PoolSize = 10;
    private const int TextureSize = 64;
    private const float RingOuterRadius = 0.45f;
    private const float RingInnerRadius = 0.35f;

    private static readonly Color DefaultClearColor = new(1f, 1f, 1f, 0.8f);
    private static readonly Color DefaultRejectColor = new(1f, 0.3f, 0.3f, 0.8f);

    private Color ClearColor;
    private Color RejectColor;
    private readonly Queue<TapIndicator> _pool = new();
    private readonly Sprite _sprite;
    private readonly float _duration;
    private readonly float _maxScale;
    private readonly Transform _parent;

    public TapIndicatorPool(
        Sprite sprite,
        float duration,
        float maxScale,
        Transform parent,
        Color? clearColor = null,
        Color? rejectColor = null
    )
    {
        ClearColor = clearColor ?? DefaultClearColor;
        RejectColor = rejectColor ?? DefaultRejectColor;
        _sprite = sprite != null ? sprite : CreateRingSprite();
        _duration = duration;
        _maxScale = maxScale;
        _parent = parent;

        for (int i = 0; i < PoolSize; i++)
            _pool.Enqueue(CreateIndicator());
    }

    public void UpdateColors(Color clearColor, Color rejectColor)
    {
        ClearColor = clearColor;
        RejectColor = rejectColor;
    }

    public void Spawn(Vector3 worldPos, bool isReject)
    {
        Spawn(worldPos, isReject ? RejectColor : ClearColor);
    }

    /// <summary>
    /// Spawn a ring with an explicit color. Used by co-op to tint remote
    /// taps in the clearer's color.
    /// </summary>
    public void Spawn(Vector3 worldPos, Color color)
    {
        TapIndicator ind;
        if (_pool.Count > 0)
            ind = _pool.Dequeue();
        else
            ind = CreateIndicator();

        worldPos.z = -1f; // render in front of arrows
        // Keep the default 80 % alpha envelope even when a hex tint is
        // supplied — indicator rings should read as translucent accents,
        // not solid color blocks.
        if (color.a > 0.99f)
            color.a = 0.8f;
        ind.Play(worldPos, color, _duration, _maxScale, OnReturn);
    }

    private void OnReturn(TapIndicator indicator)
    {
        _pool.Enqueue(indicator);
    }

    private TapIndicator CreateIndicator()
    {
        var go = new GameObject("TapIndicator");
        go.transform.SetParent(_parent, false);
        go.SetActive(false);

        var sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = _sprite;
        sr.sortingOrder = 10;

        var ind = go.AddComponent<TapIndicator>();
        return ind;
    }

    /// <summary>
    /// Creates a procedural ring sprite at runtime so no asset file is needed.
    /// </summary>
    private static Sprite CreateRingSprite()
    {
        var tex = new Texture2D(TextureSize, TextureSize, TextureFormat.RGBA32, false);
        tex.filterMode = FilterMode.Bilinear;
        float center = TextureSize * 0.5f;

        for (int y = 0; y < TextureSize; y++)
        {
            for (int x = 0; x < TextureSize; x++)
            {
                float dx = (x + 0.5f - center) / TextureSize;
                float dy = (y + 0.5f - center) / TextureSize;
                float dist = Mathf.Sqrt(dx * dx + dy * dy);

                if (dist >= RingInnerRadius && dist <= RingOuterRadius)
                {
                    // Smooth edges with anti-aliasing
                    float outerEdge = 1f - Mathf.Clamp01((dist - RingOuterRadius + 0.02f) / 0.02f);
                    float innerEdge = Mathf.Clamp01((dist - RingInnerRadius) / 0.02f);
                    float alpha = outerEdge * innerEdge;
                    tex.SetPixel(x, y, new Color(1f, 1f, 1f, alpha));
                }
                else
                {
                    tex.SetPixel(x, y, Color.clear);
                }
            }
        }

        tex.Apply();
        return Sprite.Create(
            tex,
            new Rect(0, 0, TextureSize, TextureSize),
            new Vector2(0.5f, 0.5f),
            TextureSize
        );
    }
}
