using System;
using System.Collections.Generic;
using System.Numerics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using DeadlockVmdlCompiler.Models;

namespace DeadlockVmdlCompiler.Controls;

public class Model3DViewportControl : Control
{
    public static readonly StyledProperty<SimpleMesh3D?> CurrentMeshProperty =
        AvaloniaProperty.Register<Model3DViewportControl, SimpleMesh3D?>(nameof(CurrentMesh));

    public SimpleMesh3D? CurrentMesh
    {
        get => GetValue(CurrentMeshProperty);
        set => SetValue(CurrentMeshProperty, value);
    }

    private float _yaw = 225.0f;
    private float _pitch = 15.0f;
    private float _distance = 3.5f;
    private Point _lastPointerPos;
    private bool _isDragging;

    // Neutral Studio Floor Grid Pens (No garish red/blue colors)
    private static readonly IPen GridPen = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromArgb(100, 38, 46, 60)), 1.0);
    private static readonly IPen CenterAxisPen = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromArgb(180, 58, 72, 95)), 1.2);
    private static readonly IPen WirePen = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromArgb(140, 75, 110, 165)), 0.7);
    private static readonly IBrush BackgroundBrush = new ImmutableSolidColorBrush(Color.FromRgb(15, 18, 24));

    public Model3DViewportControl()
    {
        ClipToBounds = true;
    }

    static Model3DViewportControl()
    {
        AffectsRender<Model3DViewportControl>(CurrentMeshProperty);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var point = e.GetCurrentPoint(this);
        if (point.Properties.IsLeftButtonPressed || point.Properties.IsRightButtonPressed)
        {
            _isDragging = true;
            _lastPointerPos = point.Position;
            e.Pointer.Capture(this);
            e.Handled = true;
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_isDragging)
        {
            var curPos = e.GetPosition(this);
            var delta = curPos - _lastPointerPos;
            _lastPointerPos = curPos;

            _yaw += (float)delta.X * 0.6f;
            _pitch -= (float)delta.Y * 0.6f;

            if (_pitch > 85.0f) _pitch = 85.0f;
            if (_pitch < -85.0f) _pitch = -85.0f;

            InvalidateVisual();
            e.Handled = true;
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_isDragging)
        {
            _isDragging = false;
            e.Pointer.Capture(null);
            e.Handled = true;
        }
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        float zoomDelta = (float)e.Delta.Y;
        _distance -= zoomDelta * 0.4f;
        if (_distance < 0.5f) _distance = 0.5f;
        if (_distance > 25.0f) _distance = 25.0f;

        InvalidateVisual();
        e.Handled = true;
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        var bounds = Bounds;
        if (bounds.Width <= 1 || bounds.Height <= 1)
            return;

        context.FillRectangle(BackgroundBrush, new Rect(0, 0, bounds.Width, bounds.Height));

        float aspect = (float)(bounds.Width / bounds.Height);
        float radYaw = _yaw * MathF.PI / 180.0f;
        float radPitch = _pitch * MathF.PI / 180.0f;

        float camX = _distance * MathF.Cos(radPitch) * MathF.Sin(radYaw);
        float camY = _distance * MathF.Sin(radPitch);
        float camZ = _distance * MathF.Cos(radPitch) * MathF.Cos(radYaw);

        var camPos = new Vector3(camX, camY + 0.9f, camZ);
        var targetPos = new Vector3(0, 0.9f, 0);
        var up = Vector3.UnitY;

        var viewMatrix = Matrix4x4.CreateLookAt(camPos, targetPos, up);
        var projMatrix = Matrix4x4.CreatePerspectiveFieldOfView(45.0f * MathF.PI / 180.0f, aspect, 0.1f, 100.0f);
        var viewProj = viewMatrix * projMatrix;

        float halfW = (float)bounds.Width * 0.5f;
        float halfH = (float)bounds.Height * 0.5f;

        Point? Project(Vector3 v)
        {
            var p4 = Vector4.Transform(new Vector4(v, 1.0f), viewProj);
            if (p4.W <= 0.01f) return null;
            float ndcX = p4.X / p4.W;
            float ndcY = p4.Y / p4.W;
            float ndcZ = p4.Z / p4.W;

            if (ndcZ < -1.0f || ndcZ > 1.0f) return null;

            float screenX = (ndcX + 1.0f) * halfW;
            float screenY = (1.0f - ndcY) * halfH;

            return new Point(screenX, screenY);
        }

        // Draw Ground Grid (Clean studio floor)
        float gridSize = 3.5f;
        float gridStep = 0.5f;

        for (float x = -gridSize; x <= gridSize + 0.001f; x += gridStep)
        {
            var p1 = Project(new Vector3(x, 0, -gridSize));
            var p2 = Project(new Vector3(x, 0, gridSize));
            if (p1.HasValue && p2.HasValue)
            {
                var pen = MathF.Abs(x) < 0.001f ? CenterAxisPen : GridPen;
                context.DrawLine(pen, p1.Value, p2.Value);
            }
        }

        for (float z = -gridSize; z <= gridSize + 0.001f; z += gridStep)
        {
            var p1 = Project(new Vector3(-gridSize, 0, z));
            var p2 = Project(new Vector3(gridSize, 0, z));
            if (p1.HasValue && p2.HasValue)
            {
                var pen = MathF.Abs(z) < 0.001f ? CenterAxisPen : GridPen;
                context.DrawLine(pen, p1.Value, p2.Value);
            }
        }

        // Render Mesh
        var mesh = CurrentMesh;
        if (mesh != null && mesh.Vertices.Count > 0)
        {
            float scale = mesh.Radius > 0.01f ? (1.5f / mesh.Radius) : 1.0f;
            var center = mesh.Center;

            var projectedVerts = new Point?[mesh.Vertices.Count];
            var depths = new float[mesh.Vertices.Count];

            for (int i = 0; i < mesh.Vertices.Count; i++)
            {
                var v = mesh.Vertices[i];
                var localV = (v - center) * scale;
                localV.Y += 0.9f;

                projectedVerts[i] = Project(localV);
                var p4 = Vector4.Transform(new Vector4(localV, 1.0f), viewProj);
                depths[i] = p4.W;
            }

            var lightDir = Vector3.Normalize(new Vector3(0.5f, 1.0f, 0.8f));

            if (mesh.Indices.Count >= 3)
            {
                int triCount = mesh.Indices.Count / 3;
                var triOrder = new List<(int Index, float Depth)>(triCount);

                for (int t = 0; t < triCount; t++)
                {
                    int i0 = mesh.Indices[t * 3];
                    int i1 = mesh.Indices[t * 3 + 1];
                    int i2 = mesh.Indices[t * 3 + 2];

                    if (i0 < depths.Length && i1 < depths.Length && i2 < depths.Length)
                    {
                        float avgDepth = (depths[i0] + depths[i1] + depths[i2]) / 3.0f;
                        triOrder.Add((t, avgDepth));
                    }
                }

                // Back to front depth sorting
                triOrder.Sort((a, b) => b.Depth.CompareTo(a.Depth));

                int step = triCount > 6000 ? 2 : 1;

                for (int idx = 0; idx < triOrder.Count; idx += step)
                {
                    int t = triOrder[idx].Index;
                    int i0 = mesh.Indices[t * 3];
                    int i1 = mesh.Indices[t * 3 + 1];
                    int i2 = mesh.Indices[t * 3 + 2];

                    var p0 = projectedVerts[i0];
                    var p1 = projectedVerts[i1];
                    var p2 = projectedVerts[i2];

                    if (p0.HasValue && p1.HasValue && p2.HasValue)
                    {
                        float cross = (float)((p1.Value.X - p0.Value.X) * (p2.Value.Y - p0.Value.Y) -
                                              (p1.Value.Y - p0.Value.Y) * (p2.Value.X - p0.Value.X));
                        if (cross > 0)
                        {
                            var v0 = mesh.Vertices[i0];
                            var v1 = mesh.Vertices[i1];
                            var v2 = mesh.Vertices[i2];
                            var normal = Vector3.Normalize(Vector3.Cross(v1 - v0, v2 - v0));
                            float ndotl = MathF.Max(0.25f, Vector3.Dot(normal, lightDir));

                            byte r = (byte)(35 + ndotl * 120);
                            byte g = (byte)(45 + ndotl * 135);
                            byte b = (byte)(60 + ndotl * 165);
                            var fillBrush = new ImmutableSolidColorBrush(Color.FromRgb(r, g, b));

                            var geometry = new StreamGeometry();
                            using (var gc = geometry.Open())
                            {
                                gc.BeginFigure(p0.Value, isFilled: true);
                                gc.LineTo(p1.Value);
                                gc.LineTo(p2.Value);
                                gc.EndFigure(isClosed: true);
                            }

                            context.DrawGeometry(fillBrush, WirePen, geometry);
                        }
                    }
                }
            }
        }

        // Clean border
        var borderPen = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromArgb(100, 36, 44, 58)), 1.0);
        context.DrawRectangle(null, borderPen, new Rect(0.5, 0.5, bounds.Width - 1, bounds.Height - 1));
    }
}
