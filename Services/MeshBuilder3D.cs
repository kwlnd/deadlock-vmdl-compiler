using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace DeadlockVmdlCompiler.Services;

public static class MeshBuilder3D
{
    public static Model3DGroup CreateEmptyGridScene()
    {
        var group = new Model3DGroup();

        // Neutral White / Gray Studio Lighting
        group.Children.Add(new AmbientLight(Color.FromRgb(90, 90, 90)));
        group.Children.Add(new DirectionalLight(Color.FromRgb(250, 250, 250), new Vector3D(1.2, -1.8, -1.2)));
        group.Children.Add(new DirectionalLight(Color.FromRgb(160, 160, 160), new Vector3D(-1.5, -1.0, 1.0)));
        group.Children.Add(new DirectionalLight(Color.FromRgb(110, 110, 110), new Vector3D(0, 1.5, -1.5)));

        // Pure Neutral Gray Ground Floor Grid
        group.Children.Add(CreateGroundGrid());

        return group;
    }

    public static GeometryModel3D CreateGroundGrid()
    {
        var mesh = new MeshGeometry3D();
        double size = 3.5;
        double step = 0.5;
        double thickness = 0.010;

        for (double x = -size; x <= size + 0.001; x += step)
        {
            AddBox(mesh, new Point3D(x, 0, 0), thickness, 0.002, size * 2);
        }

        for (double z = -size; z <= size + 0.001; z += step)
        {
            AddBox(mesh, new Point3D(0, 0, z), size * 2, 0.002, thickness);
        }

        var gridMat = new DiffuseMaterial(new SolidColorBrush(Color.FromArgb(140, 75, 75, 75)));
        return new GeometryModel3D(mesh, gridMat);
    }

    private static void AddBox(MeshGeometry3D mesh, Point3D center, double sx, double sy, double sz)
    {
        double x1 = center.X - sx / 2, x2 = center.X + sx / 2;
        double y1 = center.Y - sy / 2, y2 = center.Y + sy / 2;
        double z1 = center.Z - sz / 2, z2 = center.Z + sz / 2;

        AddQuad(mesh, new Point3D(x1, y1, z2), new Point3D(x2, y1, z2), new Point3D(x2, y2, z2), new Point3D(x1, y2, z2));
        AddQuad(mesh, new Point3D(x2, y1, z1), new Point3D(x1, y1, z1), new Point3D(x1, y2, z1), new Point3D(x2, y2, z1));
        AddQuad(mesh, new Point3D(x1, y1, z1), new Point3D(x1, y1, z2), new Point3D(x1, y2, z2), new Point3D(x1, y2, z1));
        AddQuad(mesh, new Point3D(x2, y1, z2), new Point3D(x2, y1, z1), new Point3D(x2, y2, z1), new Point3D(x2, y2, z2));
        AddQuad(mesh, new Point3D(x1, y2, z2), new Point3D(x2, y2, z2), new Point3D(x2, y2, z1), new Point3D(x1, y2, z1));
        AddQuad(mesh, new Point3D(x1, y1, z1), new Point3D(x2, y1, z1), new Point3D(x2, y1, z2), new Point3D(x1, y1, z2));
    }

    private static void AddQuad(MeshGeometry3D mesh, Point3D p0, Point3D p1, Point3D p2, Point3D p3)
    {
        int i0 = mesh.Positions.Count;
        mesh.Positions.Add(p0); mesh.Positions.Add(p1); mesh.Positions.Add(p2); mesh.Positions.Add(p3);
        mesh.TriangleIndices.Add(i0); mesh.TriangleIndices.Add(i0 + 1); mesh.TriangleIndices.Add(i0 + 2);
        mesh.TriangleIndices.Add(i0); mesh.TriangleIndices.Add(i0 + 2); mesh.TriangleIndices.Add(i0 + 3);
    }
}
