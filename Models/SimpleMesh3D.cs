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
        // Source 2 textures are authored with a top-left image origin. Bilinear
        // filtering removes the pixelated look of the old nearest-neighbour path.
        float fx = u * Width - 0.5f;
        float fy = v * Height - 0.5f;
        int x0 = Math.Clamp((int)MathF.Floor(fx), 0, Width - 1);
        int y0 = Math.Clamp((int)MathF.Floor(fy), 0, Height - 1);
        int x1 = Math.Min(Width - 1, x0 + 1);
        int y1 = Math.Min(Height - 1, y0 + 1);
        float tx = Math.Clamp(fx - MathF.Floor(fx), 0, 1);
        float ty = Math.Clamp(fy - MathF.Floor(fy), 0, 1);
        int c00 = Pixels[y0 * Width + x0], c10 = Pixels[y0 * Width + x1];
        int c01 = Pixels[y1 * Width + x0], c11 = Pixels[y1 * Width + x1];
        static byte Ch(int c, int shift) => (byte)((c >> shift) & 0xFF);
        int r = (int)MathF.Round(Ch(c00, 16) * (1 - tx) * (1 - ty) + Ch(c10, 16) * tx * (1 - ty) + Ch(c01, 16) * (1 - tx) * ty + Ch(c11, 16) * tx * ty);
        int g = (int)MathF.Round(Ch(c00, 8) * (1 - tx) * (1 - ty) + Ch(c10, 8) * tx * (1 - ty) + Ch(c01, 8) * (1 - tx) * ty + Ch(c11, 8) * tx * ty);
        int b = (int)MathF.Round(Ch(c00, 0) * (1 - tx) * (1 - ty) + Ch(c10, 0) * tx * (1 - ty) + Ch(c01, 0) * (1 - tx) * ty + Ch(c11, 0) * tx * ty);
        return unchecked((int)(0xFF000000u | ((uint)r << 16) | ((uint)g << 8) | (uint)b));
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
