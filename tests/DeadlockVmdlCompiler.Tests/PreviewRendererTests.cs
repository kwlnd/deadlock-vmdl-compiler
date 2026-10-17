using DeadlockVmdlCompiler.Controls;
using DeadlockVmdlCompiler.Models;
using static DeadlockVmdlCompiler.Tests.TestSupport;

namespace DeadlockVmdlCompiler.Tests;

public class PreviewRendererTests
{
    [Fact]
    public void DrawsAFramedTexturedTriangleAndClearsWhenEmpty()
    {
        var mesh = new SimpleMesh3D();
        mesh.Vertices.AddRange([new(0, 0.5f, -0.5f), new(0, 0.5f, 0.5f), new(0, 1.5f, 0)]);
        mesh.Normals.AddRange([System.Numerics.Vector3.UnitX, System.Numerics.Vector3.UnitX, System.Numerics.Vector3.UnitX]);
        mesh.Indices.AddRange([0, 1, 2]);
        mesh.TriangleMaterialIds.Add(0);
        mesh.Materials.Add(new MeshTexture { Width = 1, Height = 1, Pixels = [unchecked((int)0xFFFF0000)] });
        mesh.RecalculateBounds();
        var renderer = new SoftwareMeshRenderer();
        var pixels = new int[160 * 120];
        var depth = new float[160 * 120];
        var data = PreparedMesh.From(mesh)!;
        var camera = OrbitCamera.Frame(data.Center, data.Radius, 160f / 120f);
        renderer.Render(data, camera, 160, 120, pixels, depth);
        var centre = pixels[60 * 160 + 80];
        Check(((centre >> 16) & 0xFF) > 100 && (centre & 0xFF) < 40 && depth[60 * 160 + 80] > 0,
            "The preview did not draw a framed, textured triangle at the centre of the view.");
        camera.Distance = 0.01f;
        renderer.Render(data, camera, 160, 120, pixels, depth);
        renderer.Render(null, camera, 160, 120, pixels, depth);
        Check(depth.All(value => value == 0), "An empty preview still contains geometry.");
    }
}
