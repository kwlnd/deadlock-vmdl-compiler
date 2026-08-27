using System;
using System.Collections.Generic;
using System.Numerics;

namespace DeadlockVmdlCompiler.Models;

public class MeshTexture
{
    public string Name { get; set; } = string.Empty;
    public int Width { get; set; }
    public int Height { get; set; }
    public int[] Pixels { get; set; } = Array.Empty<int>();
    public int FallbackColor { get; set; } = unchecked((int)0xFF94A3B8);

    public int Sample(float u, float v)
    {
        if (Pixels.Length == 0 || Width <= 0 || Height <= 0) return FallbackColor;

        u = u - MathF.Floor(u);
        v = v - MathF.Floor(v);

        int x = (int)(u * Width);
        int y = (int)(v * Height);

        if (x < 0) x = 0; if (x >= Width) x = Width - 1;
        if (y < 0) y = 0; if (y >= Height) y = Height - 1;

        return Pixels[y * Width + x];
    }
}

public class SimpleMesh3D
{
    public string MeshName { get; set; } = string.Empty;
    public List<Vector3> Vertices { get; set; } = new();
    public List<Vector3> Normals { get; set; } = new();
    public List<Vector2> TexCoords { get; set; } = new();
    public List<int> Indices { get; set; } = new();
    public List<int> TriangleMaterialIds { get; set; } = new();
    public List<MeshTexture> Materials { get; set; } = new();

    public Vector3 BoundsMin { get; set; } = new Vector3(-1, -1, -1);
    public Vector3 BoundsMax { get; set; } = new Vector3(1, 1, 1);
    public Vector3 Center { get; set; } = Vector3.Zero;
    public float Radius { get; set; } = 1.0f;
    public int BoneCount { get; set; }
    public int MaterialCount { get; set; }

    public void RecalculateBounds()
    {
        if (Vertices.Count == 0)
        {
            BoundsMin = new Vector3(-1, -1, -1);
            BoundsMax = new Vector3(1, 1, 1);
            Center = Vector3.Zero;
            Radius = 1.0f;
            return;
        }

        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);

        foreach (var v in Vertices)
        {
            min = Vector3.Min(min, v);
            max = Vector3.Max(max, v);
        }

        BoundsMin = min;
        BoundsMax = max;
        Center = (min + max) * 0.5f;

        float maxDistSq = 0;
        foreach (var v in Vertices)
        {
            float distSq = Vector3.DistanceSquared(v, Center);
            if (distSq > maxDistSq)
                maxDistSq = distSq;
        }

        Radius = MathF.Max(0.1f, MathF.Sqrt(maxDistSq));
    }
}