using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
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
    private float _distance = 3.2f;
    private Point _lastPointerPos;
    private bool _isDragging;

    private WriteableBitmap? _bitmap;
    private float[]? _depthBuffer;
    private int[]? _pixelBuffer;

    private static readonly Vector3 KeyLight = Vector3.Normalize(new Vector3(0.6f, 1.2f, 0.8f));
    private static readonly Vector3 FillLight = Vector3.Normalize(new Vector3(-0.8f, 0.4f, -0.6f));
    private static readonly IPen BorderPen = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromArgb(90, 42, 52, 70)), 1.0);

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
        _distance -= zoomDelta * 0.35f;
        if (_distance < 0.3f) _distance = 0.3f;
        if (_distance > 25.0f) _distance = 25.0f;

        InvalidateVisual();
        e.Handled = true;
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        var bounds = Bounds;
        int width = (int)bounds.Width;
        int height = (int)bounds.Height;

        if (width < 10 || height < 10)
            return;

        if (_bitmap == null || _bitmap.PixelSize.Width != width || _bitmap.PixelSize.Height != height)
        {
            _bitmap?.Dispose();
            _bitmap = new WriteableBitmap(new PixelSize(width, height), new Avalonia.Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
            _depthBuffer = new float[width * height];
            _pixelBuffer = new int[width * height];
        }

        if (_depthBuffer == null || _pixelBuffer == null || _bitmap == null) return;

        // Clear Depth & Color Buffer with dark studio background
        for (int y = 0; y < height; y++)
        {
            float t = (float)y / height;
            byte r = (byte)(14 + t * 6);
            byte g = (byte)(18 + t * 8);
            byte b = (byte)(25 + t * 11);
            int bg = unchecked((int)(0xFF000000 | ((uint)r << 16) | ((uint)g << 8) | b));

            int rowStart = y * width;
            for (int x = 0; x < width; x++)
            {
                _pixelBuffer[rowStart + x] = bg;
                _depthBuffer[rowStart + x] = float.MaxValue;
            }
        }

        // Camera calculations
        float aspect = (float)width / height;
        float radYaw = _yaw * MathF.PI / 180.0f;
        float radPitch = _pitch * MathF.PI / 180.0f;

        float camX = _distance * MathF.Cos(radPitch) * MathF.Sin(radYaw);
        float camY = _distance * MathF.Sin(radPitch);
        float camZ = _distance * MathF.Cos(radPitch) * MathF.Cos(radYaw);

        var camPos = new Vector3(camX, camY + 0.9f, camZ);
        var targetPos = new Vector3(0, 0.9f, 0);
        var up = Vector3.UnitY;

        var viewMatrix = Matrix4x4.CreateLookAt(camPos, targetPos, up);
        var projMatrix = Matrix4x4.CreatePerspectiveFieldOfView(45.0f * MathF.PI / 180.0f, aspect, 0.05f, 100.0f);
        var viewProj = viewMatrix * projMatrix;

        float halfW = width * 0.5f;
        float halfH = height * 0.5f;

        Vector3? Project(Vector3 v)
        {
            var p4 = Vector4.Transform(new Vector4(v, 1.0f), viewProj);
            if (p4.W <= 0.01f) return null;
            float invW = 1.0f / p4.W;
            float ndcX = p4.X * invW;
            float ndcY = p4.Y * invW;
            float ndcZ = p4.Z * invW;

            if (ndcZ < -1.0f || ndcZ > 1.0f) return null;

            float screenX = (ndcX + 1.0f) * halfW;
            float screenY = (1.0f - ndcY) * halfH;

            return new Vector3(screenX, screenY, p4.W);
        }

        // Draw 3D Grid into pixel buffer
        void DrawLine(Vector3 p1, Vector3 p2, int color)
        {
            var s1 = Project(p1);
            var s2 = Project(p2);
            if (!s1.HasValue || !s2.HasValue) return;

            int x0 = (int)s1.Value.X, y0 = (int)s1.Value.Y;
            int x1 = (int)s2.Value.X, y1 = (int)s2.Value.Y;
            float z0 = s1.Value.Z, z1 = s2.Value.Z;

            int dx = Math.Abs(x1 - x0), dy = Math.Abs(y1 - y0);
            int sx = x0 < x1 ? 1 : -1, sy = y0 < y1 ? 1 : -1;
            int err = dx - dy;

            int steps = Math.Max(dx, dy);
            float stepInv = steps > 0 ? (1.0f / steps) : 0;
            int curStep = 0;

            while (true)
            {
                if (x0 >= 0 && x0 < width && y0 >= 0 && y0 < height)
                {
                    float curZ = z0 + (z1 - z0) * (curStep * stepInv);
                    int idx = y0 * width + x0;
                    if (curZ < _depthBuffer[idx])
                    {
                        _pixelBuffer[idx] = color;
                    }
                }

                if (x0 == x1 && y0 == y1) break;
                int e2 = 2 * err;
                if (e2 > -dy) { err -= dy; x0 += sx; }
                if (e2 < dx) { err += dx; y0 += sy; }
                curStep++;
            }
        }

        float gridSize = 2.5f;
        float gridStep = 0.5f;
        const int gridColor = unchecked((int)0xFF2A3446);
        const int axisColor = unchecked((int)0xFF4B6082);

        for (float x = -gridSize; x <= gridSize + 0.001f; x += gridStep)
        {
            int col = MathF.Abs(x) < 0.001f ? axisColor : gridColor;
            DrawLine(new Vector3(x, 0, -gridSize), new Vector3(x, 0, gridSize), col);
        }
        for (float z = -gridSize; z <= gridSize + 0.001f; z += gridStep)
        {
            int col = MathF.Abs(z) < 0.001f ? axisColor : gridColor;
            DrawLine(new Vector3(-gridSize, 0, z), new Vector3(gridSize, 0, z), col);
        }

        // Render Mesh with Z-buffer & Studio Shading
        var mesh = CurrentMesh;
        if (mesh != null && mesh.Vertices.Count > 0)
        {
            float scale = mesh.Radius > 0.01f ? (1.5f / mesh.Radius) : 1.0f;
            var center = mesh.Center;

            var screenVerts = new Vector3?[mesh.Vertices.Count];
            for (int i = 0; i < mesh.Vertices.Count; i++)
            {
                var v = mesh.Vertices[i];
                var localV = (v - center) * scale;
                localV.Y += 0.9f;
                screenVerts[i] = Project(localV);
            }

            int triCount = mesh.Indices.Count / 3;
            for (int t = 0; t < triCount; t++)
            {
                int i0 = mesh.Indices[t * 3];
                int i1 = mesh.Indices[t * 3 + 1];
                int i2 = mesh.Indices[t * 3 + 2];

                if (i0 >= screenVerts.Length || i1 >= screenVerts.Length || i2 >= screenVerts.Length) continue;

                var sv0 = screenVerts[i0];
                var sv1 = screenVerts[i1];
                var sv2 = screenVerts[i2];

                if (!sv0.HasValue || !sv1.HasValue || !sv2.HasValue) continue;

                var v0 = sv0.Value;
                var v1 = sv1.Value;
                var v2 = sv2.Value;

                // Calculate 3D normal & Studio Lighting
                var w0 = mesh.Vertices[i0];
                var w1 = mesh.Vertices[i1];
                var w2 = mesh.Vertices[i2];
                var normal = Vector3.Normalize(Vector3.Cross(w1 - w0, w2 - w0));

                float ndotk = MathF.Max(0, Vector3.Dot(normal, KeyLight));
                float ndotf = MathF.Max(0, Vector3.Dot(normal, FillLight));
                float backLight = MathF.Max(0, Vector3.Dot(-normal, KeyLight)) * 0.15f;
                float lighting = Math.Clamp(0.28f + ndotk * 0.58f + ndotf * 0.18f + backLight, 0.2f, 1.0f);

                uint baseCol = (t < mesh.TriangleColors.Count) ? mesh.TriangleColors[t] : 0xFF94A3B8;
                byte br = (byte)((baseCol >> 16) & 0xFF);
                byte bg = (byte)((baseCol >> 8) & 0xFF);
                byte bb = (byte)(baseCol & 0xFF);

                byte r = (byte)Math.Clamp((int)(br * lighting), 0, 255);
                byte g = (byte)Math.Clamp((int)(bg * lighting), 0, 255);
                byte b = (byte)Math.Clamp((int)(bb * lighting), 0, 255);
                int finalColor = unchecked((int)(0xFF000000 | ((uint)r << 16) | ((uint)g << 8) | b));

                // Rasterize Triangle with Z-Buffer
                RasterizeTriangle(v0, v1, v2, finalColor, width, height, _depthBuffer, _pixelBuffer);
            }
        }

        // Copy buffer to bitmap
        using (var locked = _bitmap.Lock())
        {
            Marshal.Copy(_pixelBuffer, 0, locked.Address, _pixelBuffer.Length);
        }

        context.DrawImage(_bitmap, new Rect(0, 0, width, height));
        context.DrawRectangle(null, BorderPen, new Rect(0.5, 0.5, width - 1, height - 1));
    }

    private static void RasterizeTriangle(
        Vector3 v0, Vector3 v1, Vector3 v2,
        int color, int width, int height,
        float[] depthBuffer, int[] pixelBuffer)
    {
        int minX = Math.Max(0, (int)MathF.Floor(MathF.Min(v0.X, MathF.Min(v1.X, v2.X))));
        int maxX = Math.Min(width - 1, (int)MathF.Ceiling(MathF.Max(v0.X, MathF.Max(v1.X, v2.X))));
        int minY = Math.Max(0, (int)MathF.Floor(MathF.Min(v0.Y, MathF.Min(v1.Y, v2.Y))));
        int maxY = Math.Min(height - 1, (int)MathF.Ceiling(MathF.Max(v0.Y, MathF.Max(v1.Y, v2.Y))));

        if (minX > maxX || minY > maxY) return;

        float area = (v1.Y - v2.Y) * (v0.X - v2.X) + (v2.X - v1.X) * (v0.Y - v2.Y);
        if (MathF.Abs(area) < 0.0001f) return;
        float invArea = 1.0f / area;

        for (int y = minY; y <= maxY; y++)
        {
            float py = y + 0.5f;
            int rowStart = y * width;

            for (int x = minX; x <= maxX; x++)
            {
                float px = x + 0.5f;

                float w0 = ((v1.Y - v2.Y) * (px - v2.X) + (v2.X - v1.X) * (py - v2.Y)) * invArea;
                float w1 = ((v2.Y - v0.Y) * (px - v2.X) + (v0.X - v2.X) * (py - v2.Y)) * invArea;
                float w2 = 1.0f - w0 - w1;

                if (w0 >= 0 && w1 >= 0 && w2 >= 0)
                {
                    float z = w0 * v0.Z + w1 * v1.Z + w2 * v2.Z;
                    int idx = rowStart + x;

                    if (z < depthBuffer[idx])
                    {
                        depthBuffer[idx] = z;
                        pixelBuffer[idx] = color;
                    }
                }
            }
        }
    }
}