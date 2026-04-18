using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Renders all arrow trails as a single combined mesh for the viewport-culling
/// path. Each arrow owns a 4-vertex slot in the mesh; <see cref="AddArrow"/>
/// appends, <see cref="RemoveArrow"/> uses swap-and-pop so per-clear updates
/// stay O(1). Geometry is uploaded lazily via <see cref="RebuildIfDirty"/>
/// before draw.
///
/// The GameObject is inactive when trails are hidden; the owning
/// <see cref="BoardView"/> toggles it in <c>SetAllTrailsVisible</c>.
/// </summary>
public sealed class BoardTrailRenderer : MonoBehaviour
{
    private static readonly int ColorId = Shader.PropertyToID("_Color");

    private MeshFilter _meshFilter;
    private MeshRenderer _meshRenderer;
    private Mesh _mesh;
    private Board _board;
    private VisualSettings _settings;
    private float _halfWidth;
    private float _extensionDist;

    private readonly Dictionary<Arrow, int> _slotByArrow = new();
    private readonly List<Arrow> _arrowBySlot = new();
    private readonly List<Vector3> _verts = new();
    private readonly List<Vector2> _uvs = new();
    private readonly List<int> _tris = new();
    private bool _dirty;

    public void Init(Board board, VisualSettings settings)
    {
        _board = board;
        _settings = settings;
        _halfWidth = settings.arrowBodyWidth * 0.5f;
        _extensionDist = Mathf.Max(board.Width, board.Height) * settings.pathExtensionMultiplier;

        _meshFilter = gameObject.AddComponent<MeshFilter>();
        _meshRenderer = gameObject.AddComponent<MeshRenderer>();
        _meshRenderer.sortingOrder = 0; // below chunks (1) and interaction views

        Material src =
            settings.arrowTrailMaterial != null
                ? settings.arrowTrailMaterial
                : settings.arrowBodyMaterial;
        _meshRenderer.material = src; // auto-instantiates
        _meshRenderer.material.SetColor(ColorId, settings.trailColor);

        _mesh = new Mesh { name = "TrailMesh" };
        _mesh.MarkDynamic();
        _meshFilter.mesh = _mesh;

        gameObject.SetActive(false);
    }

    public void AddArrow(Arrow arrow)
    {
        if (_slotByArrow.ContainsKey(arrow))
            return;
        if (arrow.Cells.Count < 2)
            return;

        int slot = _arrowBySlot.Count;
        _slotByArrow[arrow] = slot;
        _arrowBySlot.Add(arrow);

        _verts.Add(Vector3.zero);
        _verts.Add(Vector3.zero);
        _verts.Add(Vector3.zero);
        _verts.Add(Vector3.zero);
        _uvs.Add(new Vector2(0f, 0f));
        _uvs.Add(new Vector2(0f, 1f));
        _uvs.Add(new Vector2(0f, 0f));
        _uvs.Add(new Vector2(0f, 1f));
        WriteQuadVerts(arrow, slot * 4);

        int baseIdx = slot * 4;
        _tris.Add(baseIdx);
        _tris.Add(baseIdx + 1);
        _tris.Add(baseIdx + 2);
        _tris.Add(baseIdx + 2);
        _tris.Add(baseIdx + 1);
        _tris.Add(baseIdx + 3);

        _dirty = true;
    }

    public void RemoveArrow(Arrow arrow)
    {
        if (!_slotByArrow.TryGetValue(arrow, out int slot))
            return;

        int lastSlot = _arrowBySlot.Count - 1;
        if (slot != lastSlot)
        {
            int src = lastSlot * 4;
            int dst = slot * 4;
            for (int i = 0; i < 4; i++)
            {
                _verts[dst + i] = _verts[src + i];
                _uvs[dst + i] = _uvs[src + i];
            }
            Arrow moved = _arrowBySlot[lastSlot];
            _arrowBySlot[slot] = moved;
            _slotByArrow[moved] = slot;
        }

        _arrowBySlot.RemoveAt(lastSlot);
        _slotByArrow.Remove(arrow);
        _verts.RemoveRange(_verts.Count - 4, 4);
        _uvs.RemoveRange(_uvs.Count - 4, 4);
        _tris.RemoveRange(_tris.Count - 6, 6);

        _dirty = true;
    }

    /// <summary>
    /// Reapplies settings after a theme change. Updates trail color and
    /// rewrites all quad vertices in case <c>arrowBodyWidth</c> or
    /// <c>pathExtensionMultiplier</c> changed.
    /// </summary>
    public void ApplyTheme(VisualSettings settings)
    {
        _settings = settings;
        _halfWidth = settings.arrowBodyWidth * 0.5f;
        _extensionDist = Mathf.Max(_board.Width, _board.Height) * settings.pathExtensionMultiplier;

        Material src =
            settings.arrowTrailMaterial != null
                ? settings.arrowTrailMaterial
                : settings.arrowBodyMaterial;
        if (_meshRenderer.sharedMaterial != src)
            _meshRenderer.material = src;
        _meshRenderer.material.SetColor(ColorId, settings.trailColor);

        for (int slot = 0; slot < _arrowBySlot.Count; slot++)
            WriteQuadVerts(_arrowBySlot[slot], slot * 4);

        _dirty = true;
    }

    public void RebuildIfDirty()
    {
        if (!_dirty)
            return;

        _mesh.Clear();
        if (_verts.Count > 0)
        {
            _mesh.indexFormat =
                _verts.Count > 65535
                    ? UnityEngine.Rendering.IndexFormat.UInt32
                    : UnityEngine.Rendering.IndexFormat.UInt16;
            _mesh.SetVertices(_verts);
            _mesh.SetUVs(0, _uvs);
            _mesh.SetTriangles(_tris, 0);
            _mesh.RecalculateBounds();
        }
        _dirty = false;
    }

    private void WriteQuadVerts(Arrow arrow, int start)
    {
        Cell head = arrow.Cells[0];
        Cell next = arrow.Cells[1];
        Vector3 headPos = new Vector3(head.X, head.Y, 0f);
        Vector3 nextPos = new Vector3(next.X, next.Y, 0f);
        Vector3 headDir = (headPos - nextPos).normalized;
        Vector3 perp = new Vector3(-headDir.y, headDir.x, 0f) * _halfWidth;
        Vector3 exitPoint = headPos + headDir * _extensionDist;

        _verts[start] = exitPoint - perp;
        _verts[start + 1] = exitPoint + perp;
        _verts[start + 2] = headPos - perp;
        _verts[start + 3] = headPos + perp;
    }

    private void LateUpdate()
    {
        RebuildIfDirty();
    }

    private void OnDestroy()
    {
        if (_mesh != null)
            Destroy(_mesh);
    }
}
