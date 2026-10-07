using System;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using DeadlockVmdlCompiler.Models;

namespace DeadlockVmdlCompiler.Controls;

/// <summary>Orbit camera in model space: metres, Y up.</summary>
public struct OrbitCamera
{
    public const float FieldOfView = 40.0f * MathF.PI / 180.0f;

    public float Yaw;
    public float Pitch;
    public float Distance;
    public Vector3 Target;

    public readonly Vector3 Direction
    {
        get
        {
            var yaw = Yaw * MathF.PI / 180.0f;
            var pitch = Pitch * MathF.PI / 180.0f;
            return new Vector3(MathF.Cos(pitch) * MathF.Sin(yaw), MathF.Sin(pitch), MathF.Cos(pitch) * MathF.Cos(yaw));
        }
    }

    public readonly Vector3 Position => Target + Direction * Distance;

    /// <summary>Three-quarter front view that fits the whole model.</summary>
    public static OrbitCamera Frame(Vector3 center, float radius, float aspect)
    {
        var fit = MathF.Min(FieldOfView, 2 * MathF.Atan(MathF.Tan(FieldOfView / 2) * MathF.Max(0.2f, aspect)));
        return new OrbitCamera
        {
            // Source 2 heroes face +X.
            Yaw = 62.0f,
            Pitch = 8.0f,
            Target = center,
            Distance = MathF.Max(0.05f, radius) / MathF.Sin(fit / 2) * 1.05f
        };
    }
}

/// <summary>Flat arrays of a mesh, built once so a frame never touches list indexers.</summary>
public sealed class PreparedMesh
{
    public Vector3[] Positions { get; private init; } = [];
    public Vector3[] Normals { get; private init; } = [];
    public Vector2[] TexCoords { get; private init; } = [];
    public int[] Indices { get; private init; } = [];
    public int[] TriangleMaterials { get; private init; } = [];
    public MeshTexture[] Materials { get; private init; } = [];
    public Vector3 Center { get; private init; }
    public float Radius { get; private init; } = 1;
    public int TriangleCount => Indices.Length / 3;

    public static PreparedMesh? From(SimpleMesh3D? mesh)
    {
        if (mesh == null || mesh.Vertices.Count == 0) return null;
        var count = mesh.Vertices.Count;
        var normals = new Vector3[count];
        var uvs = new Vector2[count];
        CollectionsMarshal.AsSpan(mesh.Normals)[..Math.Min(count, mesh.Normals.Count)].CopyTo(normals);
        CollectionsMarshal.AsSpan(mesh.TexCoords)[..Math.Min(count, mesh.TexCoords.Count)].CopyTo(uvs);
        var triangles = mesh.Indices.Count / 3;
        var materials = new int[triangles];
        CollectionsMarshal.AsSpan(mesh.TriangleMaterialIds)[..Math.Min(triangles, mesh.TriangleMaterialIds.Count)]
            .CopyTo(materials);
        return new PreparedMesh
        {
            Positions = mesh.Vertices.ToArray(),
            Normals = normals,
            TexCoords = uvs,
            Indices = mesh.Indices.ToArray(),
            TriangleMaterials = materials,
            Materials = mesh.Materials.ToArray(),
            Center = mesh.Center,
            Radius = mesh.Radius
        };
    }
}

/// <summary>Software rasterizer for the model preview; has no UI dependencies.</summary>
public sealed class SoftwareMeshRenderer
{
    private const int UntexturedColor = unchecked((int)0xFF94A3B8);
    private const int GridColor = unchecked((int)0xFF263042);
    private const int AxisColor = unchecked((int)0xFF46597A);

    // Screen x, screen y, 1/w (0 when behind the camera), light.
    private Vector4[] _projected = [];

    public void Render(PreparedMesh? mesh, in OrbitCamera camera, int width, int height, int[] pixels, float[] depth)
    {
        ClearBackground(width, height, pixels, depth);

        var radius = mesh?.Radius ?? 1.0f;
        var near = MathF.Max(0.005f, camera.Distance * 0.02f);
        var far = camera.Distance + radius * 40 + 50;
        var position = camera.Position;
        var view = Matrix4x4.CreateLookAt(position, camera.Target, Vector3.UnitY);
        var projection = Matrix4x4.CreatePerspectiveFieldOfView(OrbitCamera.FieldOfView, (float)width / height, near, far);
        var viewProjection = view * projection;

        if (mesh != null)
            RenderMesh(mesh, camera, viewProjection, near, width, height, pixels, depth);

        DrawGrid(radius, viewProjection, near, width, height, pixels, depth);
    }

    private static void ClearBackground(int width, int height, int[] pixels, float[] depth)
    {
        Array.Clear(depth, 0, width * height);
        for (var y = 0; y < height; y++)
        {
            var t = (float)y / height;
            var color = unchecked((int)(0xFF000000 | ((uint)(14 + t * 6) << 16) | ((uint)(18 + t * 8) << 8) | (uint)(25 + t * 11)));
            Array.Fill(pixels, color, y * width, width);
        }
    }

    private void RenderMesh(PreparedMesh mesh, in OrbitCamera camera, Matrix4x4 viewProjection, float near,
        int width, int height, int[] pixels, float[] depth)
    {
        if (_projected.Length < mesh.Positions.Length)
            _projected = new Vector4[mesh.Positions.Length];
        var projected = _projected;

        // The lights travel with the camera, so the model is never viewed from its dark side.
        var forward = Vector3.Normalize(camera.Target - camera.Position);
        var right = Vector3.Normalize(Vector3.Cross(forward, Vector3.UnitY));
        var up = Vector3.Cross(right, forward);
        var keyLight = Vector3.Normalize(-forward + up * 0.55f - right * 0.5f);
        var fillLight = Vector3.Normalize(-forward * 0.3f + right + up * 0.1f);
        var halfWidth = width * 0.5f;
        var halfHeight = height * 0.5f;

        Parallel.For(0, (mesh.Positions.Length + 4095) / 4096, chunk =>
        {
            var end = Math.Min(mesh.Positions.Length, (chunk + 1) * 4096);
            for (var i = chunk * 4096; i < end; i++)
            {
                var clip = Vector4.Transform(new Vector4(mesh.Positions[i], 1.0f), viewProjection);
                if (clip.W <= near)
                {
                    projected[i] = default;
                    continue;
                }
                var inverseW = 1.0f / clip.W;
                var normal = mesh.Normals[i];
                var key = Vector3.Dot(normal, keyLight) * 0.5f + 0.5f;
                var light = 0.24f + 0.82f * key * key + 0.12f * MathF.Max(0, Vector3.Dot(normal, fillLight));
                projected[i] = new Vector4((clip.X * inverseW + 1.0f) * halfWidth,
                    (1.0f - clip.Y * inverseW) * halfHeight, inverseW, light);
            }
        });

        // Each band owns its rows, so bands can rasterize every triangle without locks.
        var bands = Math.Clamp(Math.Min(Environment.ProcessorCount, height / 16), 1, 32);
        Parallel.For(0, bands, band =>
        {
            var firstRow = height * band / bands;
            var lastRow = height * (band + 1) / bands - 1;
            var indices = mesh.Indices;
            for (int triangle = 0, count = mesh.TriangleCount; triangle < count; triangle++)
            {
                int i0 = indices[triangle * 3], i1 = indices[triangle * 3 + 1], i2 = indices[triangle * 3 + 2];
                if ((uint)i0 >= (uint)mesh.Positions.Length || (uint)i1 >= (uint)mesh.Positions.Length ||
                    (uint)i2 >= (uint)mesh.Positions.Length) continue;
                var a = projected[i0];
                var b = projected[i1];
                var c = projected[i2];
                if (a.Z == 0 || b.Z == 0 || c.Z == 0) continue;

                var minY = Math.Max(firstRow, (int)MathF.Min(a.Y, MathF.Min(b.Y, c.Y)));
                var maxY = Math.Min(lastRow, (int)MathF.Max(a.Y, MathF.Max(b.Y, c.Y)));
                if (minY > maxY) continue;
                var minX = Math.Max(0, (int)MathF.Min(a.X, MathF.Min(b.X, c.X)));
                var maxX = Math.Min(width - 1, (int)MathF.Max(a.X, MathF.Max(b.X, c.X)));
                if (minX > maxX) continue;

                var area = (b.Y - c.Y) * (a.X - c.X) + (c.X - b.X) * (a.Y - c.Y);
                if (MathF.Abs(area) < 0.0001f) continue;
                var inverseArea = 1.0f / area;

                var materialId = mesh.TriangleMaterials[triangle];
                var material = (uint)materialId < (uint)mesh.Materials.Length ? mesh.Materials[materialId] : null;
                if (material is { IsAdditive: true }) continue;
                RasterizeTriangle(a, b, c, mesh.TexCoords[i0], mesh.TexCoords[i1], mesh.TexCoords[i2], material,
                    inverseArea, minX, maxX, minY, maxY, width, pixels, depth);
            }
        });
    }

    private static void RasterizeTriangle(Vector4 a, Vector4 b, Vector4 c, Vector2 uvA, Vector2 uvB, Vector2 uvC,
        MeshTexture? material, float inverseArea, int minX, int maxX, int minY, int maxY, int width,
        int[] pixels, float[] depth)
    {
        var stepA = (b.Y - c.Y) * inverseArea;
        var rowA = (c.X - b.X) * inverseArea;
        var stepB = (c.Y - a.Y) * inverseArea;
        var rowB = (a.X - c.X) * inverseArea;

        // Attributes are divided by w up front, which keeps textures from swimming on large triangles.
        uvA *= a.Z;
        uvB *= b.Z;
        uvC *= c.Z;
        float lightA = a.W * a.Z, lightB = b.W * b.Z, lightC = c.W * c.Z;
        var textured = material is { Pixels.Length: > 0 };
        var flat = material?.FallbackColor ?? UntexturedColor;

        for (var y = minY; y <= maxY; y++)
        {
            var dy = y + 0.5f - c.Y;
            var dx = minX + 0.5f - c.X;
            var weightA = stepA * dx + rowA * dy;
            var weightB = stepB * dx + rowB * dy;
            var row = y * width;

            for (var x = minX; x <= maxX; x++, weightA += stepA, weightB += stepB)
            {
                var weightC = 1.0f - weightA - weightB;
                if (weightA < 0 || weightB < 0 || weightC < 0) continue;

                var inverseW = weightA * a.Z + weightB * b.Z + weightC * c.Z;
                var index = row + x;
                if (inverseW <= depth[index]) continue;
                depth[index] = inverseW;

                var w = 1.0f / inverseW;
                var light = (weightA * lightA + weightB * lightB + weightC * lightC) * w;
                var color = textured
                    ? material!.Sample((weightA * uvA.X + weightB * uvB.X + weightC * uvC.X) * w,
                        (weightA * uvA.Y + weightB * uvB.Y + weightC * uvC.Y) * w)
                    : flat;

                var red = Math.Min(255, (int)(((color >> 16) & 0xFF) * light));
                var green = Math.Min(255, (int)(((color >> 8) & 0xFF) * light));
                var blue = Math.Min(255, (int)((color & 0xFF) * light));
                pixels[index] = unchecked((int)0xFF000000) | (red << 16) | (green << 8) | blue;
            }
        }
    }

    private static void DrawGrid(float radius, Matrix4x4 viewProjection, float near, int width, int height,
        int[] pixels, float[] depth)
    {
        // Half-metre cells on the model's ground plane; coarser for very large models.
        var step = radius > 6 ? 2.0f : radius > 2.5f ? 1.0f : 0.5f;
        var lines = Math.Clamp((int)MathF.Ceiling(radius * 1.6f / step), 4, 12);
        var extent = lines * step;
        for (var i = -lines; i <= lines; i++)
        {
            var color = i == 0 ? AxisColor : GridColor;
            var offset = i * step;
            DrawLine(new Vector3(offset, 0, -extent), new Vector3(offset, 0, extent), color,
                viewProjection, near, width, height, pixels, depth);
            DrawLine(new Vector3(-extent, 0, offset), new Vector3(extent, 0, offset), color,
                viewProjection, near, width, height, pixels, depth);
        }
    }

    private static void DrawLine(Vector3 from, Vector3 to, int color, Matrix4x4 viewProjection, float near,
        int width, int height, int[] pixels, float[] depth)
    {
        var start = Vector4.Transform(new Vector4(from, 1.0f), viewProjection);
        var end = Vector4.Transform(new Vector4(to, 1.0f), viewProjection);
        if (start.W <= near && end.W <= near) return;
        // Clip against the near plane so a line passing the camera keeps its visible part.
        if (start.W <= near) start = Vector4.Lerp(start, end, (near - start.W) / (end.W - start.W) + 0.0001f);
        if (end.W <= near) end = Vector4.Lerp(end, start, (near - end.W) / (start.W - end.W) + 0.0001f);

        float startInverse = 1.0f / start.W, endInverse = 1.0f / end.W;
        float x0 = (start.X * startInverse + 1.0f) * width * 0.5f, y0 = (1.0f - start.Y * startInverse) * height * 0.5f;
        float x1 = (end.X * endInverse + 1.0f) * width * 0.5f, y1 = (1.0f - end.Y * endInverse) * height * 0.5f;

        var steps = (int)MathF.Ceiling(MathF.Max(MathF.Abs(x1 - x0), MathF.Abs(y1 - y0)));
        if (steps <= 0 || steps > 20000) return;
        for (var i = 0; i <= steps; i++)
        {
            var t = (float)i / steps;
            int x = (int)(x0 + (x1 - x0) * t), y = (int)(y0 + (y1 - y0) * t);
            if ((uint)x >= (uint)width || (uint)y >= (uint)height) continue;
            var index = y * width + x;
            // 1/w is linear in screen space, so the model correctly hides the grid behind it.
            if (startInverse + (endInverse - startInverse) * t > depth[index])
                pixels[index] = color;
        }
    }
}
