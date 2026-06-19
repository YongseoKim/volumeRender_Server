using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Draws a polyline in RectTransform local space for UI canvas overlays.
/// </summary>
[RequireComponent(typeof(CanvasRenderer))]
public sealed class UILineGraphic : MaskableGraphic
{
    private readonly List<Vector2> _points = new List<Vector2>();

    [SerializeField, Min(0.5f)] private float thickness = 4f;

    public float Thickness
    {
        get => thickness;
        set
        {
            thickness = Mathf.Max(0.5f, value);
            SetVerticesDirty();
        }
    }

    public int PointCount => _points.Count;

    public void ClearPoints()
    {
        if (_points.Count == 0)
        {
            return;
        }

        _points.Clear();
        SetVerticesDirty();
    }

    public void SetPoints(IReadOnlyList<Vector2> points)
    {
        _points.Clear();
        if (points != null && points.Count > 0)
        {
            _points.AddRange(points);
        }

        SetVerticesDirty();
    }

    public void AddPoint(Vector2 point)
    {
        _points.Add(point);
        SetVerticesDirty();
    }

    protected override void OnPopulateMesh(VertexHelper vertexHelper)
    {
        vertexHelper.Clear();

        if (_points.Count < 2)
        {
            return;
        }

        var halfWidth = thickness * 0.5f;
        var color32 = color;

        for (var i = 1; i < _points.Count; ++i)
        {
            AddLineSegment(vertexHelper, _points[i - 1], _points[i], halfWidth, color32);
        }
    }

    private static void AddLineSegment(
        VertexHelper vertexHelper,
        Vector2 start,
        Vector2 end,
        float halfWidth,
        Color32 color32)
    {
        var direction = end - start;
        var length = direction.magnitude;
        if (length <= 1e-4f)
        {
            return;
        }

        direction /= length;
        var normal = new Vector2(-direction.y, direction.x) * halfWidth;

        var bottomLeft = start - normal;
        var topLeft = start + normal;
        var topRight = end + normal;
        var bottomRight = end - normal;

        var vertexIndex = vertexHelper.currentVertCount;
        vertexHelper.AddVert(bottomLeft, color32, Vector2.zero);
        vertexHelper.AddVert(topLeft, color32, Vector2.zero);
        vertexHelper.AddVert(topRight, color32, Vector2.zero);
        vertexHelper.AddVert(bottomRight, color32, Vector2.zero);
        vertexHelper.AddTriangle(vertexIndex, vertexIndex + 1, vertexIndex + 2);
        vertexHelper.AddTriangle(vertexIndex + 2, vertexIndex + 3, vertexIndex);
    }
}