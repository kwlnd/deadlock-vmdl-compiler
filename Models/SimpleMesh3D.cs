using System;
using System.Collections.Generic;
using System.Numerics;

namespace DeadlockVmdlCompiler.Models;

public class SimpleMesh3D
{
    public string MeshName { get; set; } = string.Empty;
    public List<Vector3> Vertices { get; set; } = new();
    public List<int> Indices { get; set; } = new();
    public List<Vector3> Normals { get; set; } = new();
    public List<uint> TriangleColors { get; set; } = new();
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