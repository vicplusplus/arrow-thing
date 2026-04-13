using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Renders a chunk of arrows as a single combined mesh. One MeshFilter +
/// MeshRenderer per chunk. Used by viewport culling for large boards to
/// reduce draw calls from one-per-arrow to one-per-visible-chunk.
///
/// Arrows are added via <see cref="AddArrow"/>; geometry is rebuilt lazily
/// on the next <see cref="RebuildIfDirty"/> call.
/// </summary>
public sealed class BoardChunkRenderer : MonoBehaviour
{
    private MeshFilter _meshFilter;
    private MeshRenderer _meshRenderer;
    private Mesh _mesh;
    private VisualSettings _settings;
    private int _boardWidth;
    private int _boardHeight;

    private readonly Dictionary<Arrow, Color> _arrows = new();
    private readonly HashSet<Arrow> _hidden = new();
    private bool _dirty = true;

    private static Material _sharedMaterial;

    public void Init(int boardWidth, int boardHeight, VisualSettings settings)
    {
        _boardWidth = boardWidth;
        _boardHeight = boardHeight;
        _settings = settings;

        _meshFilter = gameObject.AddComponent<MeshFilter>();
        _meshRenderer = gameObject.AddComponent<MeshRenderer>();
        _meshRenderer.sortingOrder = 1;
        _meshRenderer.material = GetSharedMaterial();

        _mesh = new Mesh { name = "ChunkMesh" };
        _mesh.MarkDynamic();
        _meshFilter.mesh = _mesh;
    }

    private static Material GetSharedMaterial()
    {
        if (_sharedMaterial == null)
        {
            var shader = Shader.Find("ArrowThing/ArrowBodyBatched");
            _sharedMaterial = new Material(shader);
        }
        return _sharedMaterial;
    }

    public int ArrowCount => _arrows.Count;

    public void AddArrow(Arrow arrow, Color color)
    {
        _arrows[arrow] = color;
        _dirty = true;
    }

    public void RemoveArrow(Arrow arrow)
    {
        if (_arrows.Remove(arrow))
            _dirty = true;
        if (_hidden.Remove(arrow))
            _dirty = true;
    }

    public void SetArrowColor(Arrow arrow, Color color)
    {
        if (_arrows.ContainsKey(arrow))
        {
            _arrows[arrow] = color;
            _dirty = true;
        }
    }

    /// <summary>
    /// Hides an arrow from the chunk mesh without removing it from the registry.
    /// Used when an interaction (clear animation) temporarily owns the arrow.
    /// </summary>
    public void HideArrow(Arrow arrow)
    {
        if (_arrows.ContainsKey(arrow) && _hidden.Add(arrow))
            _dirty = true;
    }

    public void UnhideArrow(Arrow arrow)
    {
        if (_hidden.Remove(arrow))
            _dirty = true;
    }

    public void MarkDirty() => _dirty = true;

    public void RebuildIfDirty()
    {
        if (!_dirty)
            return;

        Build();
        _dirty = false;
    }

    private void Build()
    {
        _mesh.Clear();

        if (_arrows.Count == 0)
            return;

        var verts = new List<Vector3>();
        var uvs = new List<Vector2>();
        var colors = new List<Color>();
        var tris = new List<int>();

        float bodyWidth = _settings.arrowBodyWidth;
        float halfWidth = bodyWidth * 0.5f;
        float headHalfBase = bodyWidth * _settings.arrowHeadWidthMultiplier;
        float headLength = _settings.arrowHeadLength;

        foreach (var kvp in _arrows)
        {
            Arrow arrow = kvp.Key;
            if (_hidden.Contains(arrow))
                continue;

            // Vertex colors aren't auto-converted from sRGB to linear like
            // material _Color uniforms are. Convert manually so batched arrows
            // match individual ArrowView output.
            Color color =
                QualitySettings.activeColorSpace == ColorSpace.Linear
                    ? kvp.Value.linear
                    : kvp.Value;
            Vector3[] path = BoardCoords.ArrowPathToWorld(arrow, _boardWidth, _boardHeight);

            BuildBody(path, halfWidth, color, verts, uvs, colors, tris);
            BuildHead(path, headHalfBase, headLength, color, verts, uvs, colors, tris);
        }

        if (verts.Count > 65535)
            _mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
        else
            _mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt16;

        _mesh.SetVertices(verts);
        _mesh.SetUVs(0, uvs);
        _mesh.SetColors(colors);
        _mesh.SetTriangles(tris, 0);
        _mesh.RecalculateBounds();
    }

    private static void BuildBody(
        Vector3[] path,
        float half,
        Color color,
        List<Vector3> verts,
        List<Vector2> uvs,
        List<Color> colors,
        List<int> tris
    )
    {
        for (int i = 0; i < path.Length - 1; i++)
        {
            Vector3 a = path[i];
            Vector3 b = path[i + 1];
            Vector3 dir = (b - a).normalized;
            Vector3 perp = new Vector3(-dir.y, dir.x, 0f) * half;

            AddQuad(verts, uvs, colors, tris, color, a - perp, a + perp, b - perp, b + perp);

            // Corner fill between this segment and the next.
            if (i < path.Length - 2)
            {
                Vector3 c = path[i + 2];
                Vector3 dir2 = (c - b).normalized;
                Vector3 perp2 = new Vector3(-dir2.y, dir2.x, 0f) * half;
                AddQuad(verts, uvs, colors, tris, color, b - perp, b + perp, b - perp2, b + perp2);
            }
        }
    }

    private static void BuildHead(
        Vector3[] path,
        float headHalfBase,
        float headLength,
        Color color,
        List<Vector3> verts,
        List<Vector2> uvs,
        List<Color> colors,
        List<int> tris
    )
    {
        Vector3 headPos = path[0];
        Vector3 headDir = (path[0] - path[1]).normalized;
        Vector3 headPerp = new Vector3(-headDir.y, headDir.x, 0f);

        Vector3 baseLeft = headPos - headPerp * headHalfBase;
        Vector3 baseRight = headPos + headPerp * headHalfBase;
        Vector3 tip = headPos + headDir * headLength;

        int baseIdx = verts.Count;
        verts.Add(baseLeft);
        uvs.Add(Vector2.zero);
        colors.Add(color);
        verts.Add(baseRight);
        uvs.Add(Vector2.zero);
        colors.Add(color);
        verts.Add(tip);
        uvs.Add(Vector2.zero);
        colors.Add(color);

        tris.Add(baseIdx);
        tris.Add(baseIdx + 1);
        tris.Add(baseIdx + 2);
    }

    private static void AddQuad(
        List<Vector3> verts,
        List<Vector2> uvs,
        List<Color> colors,
        List<int> tris,
        Color color,
        Vector3 v0,
        Vector3 v1,
        Vector3 v2,
        Vector3 v3
    )
    {
        int b = verts.Count;
        verts.Add(v0);
        uvs.Add(new Vector2(0f, 0f));
        colors.Add(color);
        verts.Add(v1);
        uvs.Add(new Vector2(0f, 1f));
        colors.Add(color);
        verts.Add(v2);
        uvs.Add(new Vector2(0f, 0f));
        colors.Add(color);
        verts.Add(v3);
        uvs.Add(new Vector2(0f, 1f));
        colors.Add(color);

        tris.Add(b);
        tris.Add(b + 1);
        tris.Add(b + 2);
        tris.Add(b + 2);
        tris.Add(b + 1);
        tris.Add(b + 3);
    }

    private void OnDestroy()
    {
        if (_mesh != null)
            Destroy(_mesh);
    }
}
