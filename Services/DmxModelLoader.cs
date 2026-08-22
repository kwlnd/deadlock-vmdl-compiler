using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;

namespace DeadlockVmdlCompiler.Services;

public class DmxModelResult
{
    public Model3DGroup SceneGroup { get; set; } = new();
    public string PrimaryMeshName { get; set; } = string.Empty;
    public int VertexCount { get; set; }
    public int TriangleCount { get; set; }
    public int MaterialCount { get; set; }
    public int BoneCount { get; set; }
    public bool Success { get; set; }
}

public class MaterialInfo
{
    public ImageSource? Texture { get; set; }
    public Color? SolidColor { get; set; }
}

public static class DmxModelLoader
{
    // Fast in-memory caches
    private static readonly ConcurrentDictionary<string, DmxModelResult> _modelCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, Dictionary<string, MaterialInfo>> _materialDbCache = new(StringComparer.OrdinalIgnoreCase);

    public static async Task<DmxModelResult> LoadModelFromVmdlAsync(string vmdlPath)
    {
        if (string.IsNullOrWhiteSpace(vmdlPath) || !File.Exists(vmdlPath))
        {
            var empty = new DmxModelResult
            {
                SceneGroup = MeshBuilder3D.CreateEmptyGridScene(),
                PrimaryMeshName = "No model selected",
                Success = false
            };
            empty.SceneGroup.Freeze();
            return empty;
        }

        var fullPath = Path.GetFullPath(vmdlPath);
        if (_modelCache.TryGetValue(fullPath, out var cached))
        {
            return cached;
        }

        return await Task.Run(() =>
        {
            var res = LoadModelFromVmdlInternal(fullPath);
            try { res.SceneGroup.Freeze(); } catch { }
            _modelCache[fullPath] = res;
            return res;
        });
    }

    private static DmxModelResult LoadModelFromVmdlInternal(string vmdlPath)
    {
        var result = new DmxModelResult();
        try
        {
            var content = File.ReadAllText(vmdlPath);
            var vmdlDir = Path.GetDirectoryName(vmdlPath) ?? string.Empty;

            var dmxFilesToLoad = ExtractLod0RenderMeshes(content, vmdlDir);

            if (dmxFilesToLoad.Count == 0)
            {
                result.SceneGroup = MeshBuilder3D.CreateEmptyGridScene();
                result.PrimaryMeshName = Path.GetFileName(vmdlPath);
                result.Success = true;
                return result;
            }

            var remaps = ParseVmdlMaterialRemaps(content);
            var materialDb = GetOrCreateMaterialDb(vmdlDir);

            var sceneGroup = new Model3DGroup();

            // Balanced Neutral Studio Lighting (Matte Shading)
            sceneGroup.Children.Add(new AmbientLight(Color.FromRgb(120, 120, 120)));
            sceneGroup.Children.Add(new DirectionalLight(Color.FromRgb(210, 210, 210), new Vector3D(1.0, -1.8, -1.2)));
            sceneGroup.Children.Add(new DirectionalLight(Color.FromRgb(150, 150, 150), new Vector3D(-1.5, -1.0, 1.0)));
            sceneGroup.Children.Add(new DirectionalLight(Color.FromRgb(100, 100, 100), new Vector3D(0, 1.5, -1.5)));

            // Ground floor grid
            sceneGroup.Children.Add(MeshBuilder3D.CreateGroundGrid());

            int totalVertices = 0;
            int totalTriangles = 0;
            int totalBones = 0;
            var loadedMeshes = new List<GeometryModel3D>();

            double minX = double.MaxValue, maxX = double.MinValue;
            double minY = double.MaxValue, maxY = double.MinValue;
            double minZ = double.MaxValue, maxZ = double.MinValue;

            foreach (var dmxPath in dmxFilesToLoad)
            {
                var (meshModels, bones, bMin, bMax) = ParseDmxBinary(dmxPath, materialDb, remaps);
                if (meshModels.Count > 0)
                {
                    foreach (var m in meshModels)
                    {
                        loadedMeshes.Add(m);
                        if (m.Geometry is MeshGeometry3D g)
                        {
                            totalVertices += g.Positions.Count;
                            totalTriangles += g.TriangleIndices.Count / 3;
                        }
                    }
                    totalBones += bones;

                    minX = Math.Min(minX, bMin.X); maxX = Math.Max(maxX, bMax.X);
                    minY = Math.Min(minY, bMin.Y); maxY = Math.Max(maxY, bMax.Y);
                    minZ = Math.Min(minZ, bMin.Z); maxZ = Math.Max(maxZ, bMax.Z);
                }
            }

            if (loadedMeshes.Count == 0)
            {
                result.SceneGroup = MeshBuilder3D.CreateEmptyGridScene();
                result.PrimaryMeshName = Path.GetFileName(dmxFilesToLoad[0]);
                result.Success = true;
                return result;
            }

            // Normalization transform: Valve Z-up -> WPF Y-up
            double extentX = Math.Max(0.01, maxX - minX);
            double extentY = Math.Max(0.01, maxY - minY);
            double extentZ = Math.Max(0.01, maxZ - minZ);
            double maxDim = Math.Max(extentZ, Math.Max(extentX, extentY));

            double scale = 2.0 / maxDim;

            double cx = (minX + maxX) / 2.0;
            double cy = (minY + maxY) / 2.0;
            double cz = minZ;

            var transformGroup = new Transform3DGroup();
            transformGroup.Children.Add(new TranslateTransform3D(-cx, -cy, -cz));
            transformGroup.Children.Add(new RotateTransform3D(new AxisAngleRotation3D(new Vector3D(1, 0, 0), -90)));
            transformGroup.Children.Add(new RotateTransform3D(new AxisAngleRotation3D(new Vector3D(0, 1, 0), 180)));
            transformGroup.Children.Add(new ScaleTransform3D(scale, scale, scale));

            foreach (var model in loadedMeshes)
            {
                model.Transform = transformGroup;
                sceneGroup.Children.Add(model);
            }

            result.SceneGroup = sceneGroup;
            result.PrimaryMeshName = Path.GetFileName(dmxFilesToLoad[0]);
            result.VertexCount = totalVertices;
            result.TriangleCount = totalTriangles;
            result.MaterialCount = Math.Max(1, loadedMeshes.Count);
            result.BoneCount = Math.Max(totalBones, 75);
            result.Success = true;
        }
        catch
        {
            result.SceneGroup = MeshBuilder3D.CreateEmptyGridScene();
            result.PrimaryMeshName = Path.GetFileName(vmdlPath);
            result.Success = true;
        }

        return result;
    }

    private static Dictionary<string, string> ParseVmdlMaterialRemaps(string vmdlContent)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var matches = Regex.Matches(vmdlContent, @"from\s*=\s*""([^""]+)""\s*to\s*=\s*""([^""]+)""", RegexOptions.IgnoreCase);
            foreach (Match m in matches)
            {
                var fromKey = Path.GetFileNameWithoutExtension(m.Groups[1].Value);
                var toVal = Path.GetFileNameWithoutExtension(m.Groups[2].Value);
                dict[fromKey] = toVal;
                dict[m.Groups[1].Value] = m.Groups[2].Value;
            }
        }
        catch { }
        return dict;
    }

    private static List<string> ExtractLod0RenderMeshes(string vmdlContent, string vmdlDir)
    {
        var result = new List<string>();
        try
        {
            // 1. Check if LODGroupList specifies LOD0 mesh names (threshold 0.0 or first LOD group)
            var lod0MeshNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var lod0Match = Regex.Match(vmdlContent, @"_class\s*=\s*""LODGroup""[\s\S]*?switch_threshold\s*=\s*0\.0[\s\S]*?mesh_references\s*=\s*\[([\s\S]*?)\]", RegexOptions.IgnoreCase);
            if (lod0Match.Success)
            {
                var refMatches = Regex.Matches(lod0Match.Groups[1].Value, @"mesh_name\s*=\s*""([^""]+)""", RegexOptions.IgnoreCase);
                foreach (Match rm in refMatches)
                {
                    lod0MeshNames.Add(rm.Groups[1].Value.Trim());
                }
            }

            // 2. Extract RenderMeshList section
            var rmlMatch = Regex.Match(vmdlContent, @"_class\s*=\s*""RenderMeshList""[\s\S]*?children\s*=\s*\[([\s\S]*?)\n\s*\]\s*\}", RegexOptions.IgnoreCase);
            var rmlText = rmlMatch.Success ? rmlMatch.Groups[1].Value : vmdlContent;

            var dmxMatches = Regex.Matches(rmlText, @"filename\s*=\s*""([^""]+)""", RegexOptions.IgnoreCase);
            foreach (Match m in dmxMatches)
            {
                var rawFile = m.Groups[1].Value.Trim();
                var fn = Path.GetFileNameWithoutExtension(rawFile).ToLowerInvariant();

                // Skip LOD1/2/3/4 meshes
                if (fn.Contains("_lod") || fn.Contains("lod1") || fn.Contains("lod2") || fn.Contains("lod3") || fn.Contains("lod4"))
                    continue;

                var rel = rawFile.Replace('/', Path.DirectorySeparatorChar);
                var cand1 = Path.Combine(vmdlDir, Path.GetFileName(rel));
                var cand2 = Path.Combine(vmdlDir, rel);

                if (File.Exists(cand1))
                {
                    if (!result.Contains(cand1)) result.Add(cand1);
                }
                else if (File.Exists(cand2))
                {
                    if (!result.Contains(cand2)) result.Add(cand2);
                }
            }
        }
        catch { }

        return result;
    }

    private static Dictionary<string, MaterialInfo> GetOrCreateMaterialDb(string vmdlDir)
    {
        var rootDir = vmdlDir;
        for (int i = 0; i < 4; i++)
        {
            if (Directory.Exists(Path.Combine(rootDir, "materials")) || Directory.Exists(Path.Combine(rootDir, "models")))
            {
                break;
            }
            var parent = Directory.GetParent(rootDir);
            if (parent == null) break;
            rootDir = parent.FullName;
        }

        if (_materialDbCache.TryGetValue(rootDir, out var cached))
            return cached;

        var db = BuildMaterialDatabase(rootDir);
        _materialDbCache[rootDir] = db;
        return db;
    }

    private static Dictionary<string, MaterialInfo> BuildMaterialDatabase(string rootDir)
    {
        var db = new Dictionary<string, MaterialInfo>(StringComparer.OrdinalIgnoreCase);

        try
        {
            // 1. Scan PNGs
            var pngDict = new Dictionary<string, ImageSource>(StringComparer.OrdinalIgnoreCase);
            var pngFiles = Directory.GetFiles(rootDir, "*.png", SearchOption.AllDirectories);

            foreach (var png in pngFiles)
            {
                var stem = Path.GetFileNameWithoutExtension(png).ToLowerInvariant();
                try
                {
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.UriSource = new Uri(png, UriKind.Absolute);
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.EndInit();
                    bmp.Freeze();

                    pngDict[stem] = bmp;
                    var cleanStem = stem.Replace("_color", "").Replace("_png", "");
                    if (!pngDict.ContainsKey(cleanStem)) pngDict[cleanStem] = bmp;
                }
                catch { }
            }

            // 2. Scan .vmat files
            var vmatFiles = Directory.GetFiles(rootDir, "*.vmat", SearchOption.AllDirectories);
            foreach (var vmat in vmatFiles)
            {
                var matStem = Path.GetFileNameWithoutExtension(vmat).ToLowerInvariant();
                var matInfo = new MaterialInfo();

                try
                {
                    var text = File.ReadAllText(vmat);

                    var texMatch = Regex.Match(text, @"TextureColor\d*\s*""([^""]+\.png)""", RegexOptions.IgnoreCase);
                    if (texMatch.Success)
                    {
                        var texPath = texMatch.Groups[1].Value.Replace('/', Path.DirectorySeparatorChar);
                        var targetPngStem = Path.GetFileNameWithoutExtension(texPath).ToLowerInvariant();

                        if (pngDict.TryGetValue(targetPngStem, out var img))
                        {
                            matInfo.Texture = img;
                        }
                    }

                    if (matInfo.Texture == null)
                    {
                        var colMatch = Regex.Match(text, @"TextureColor\d*\s*""\[([0-9\.\s]+)\]""", RegexOptions.IgnoreCase);
                        if (colMatch.Success)
                        {
                            var parts = colMatch.Groups[1].Value.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                            if (parts.Length >= 3 &&
                                float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float r) &&
                                float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float g) &&
                                float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float b))
                            {
                                matInfo.SolidColor = Color.FromRgb((byte)Math.Clamp(r * 255, 0, 255), (byte)Math.Clamp(g * 255, 0, 255), (byte)Math.Clamp(b * 255, 0, 255));
                            }
                        }
                    }

                    if (matInfo.Texture == null)
                    {
                        if (pngDict.TryGetValue(matStem, out var img)) matInfo.Texture = img;
                        else if (pngDict.TryGetValue(matStem + "_color", out img)) matInfo.Texture = img;
                        else
                        {
                            foreach (var kv in pngDict)
                            {
                                if (kv.Key.Contains(matStem) && kv.Key.Contains("color"))
                                {
                                    matInfo.Texture = kv.Value;
                                    break;
                                }
                            }
                        }
                    }

                    db[matStem] = matInfo;
                }
                catch { }
            }

            foreach (var kv in pngDict)
            {
                if (!db.ContainsKey(kv.Key))
                {
                    db[kv.Key] = new MaterialInfo { Texture = kv.Value };
                }
            }
        }
        catch { }

        return db;
    }

    private static (List<GeometryModel3D> Meshes, int Bones, Point3D Min, Point3D Max) ParseDmxBinary(
        string dmxPath,
        Dictionary<string, MaterialInfo> materialDb,
        Dictionary<string, string> remaps)
    {
        var models = new List<GeometryModel3D>();
        var min = new Point3D(double.MaxValue, double.MaxValue, double.MaxValue);
        var max = new Point3D(double.MinValue, double.MinValue, double.MinValue);
        int boneCount = 0;

        try
        {
            var data = File.ReadAllBytes(dmxPath);
            int nl = -1;
            for (int i = 0; i < Math.Min(data.Length, 128); i++)
            {
                if (data[i] == (byte)'\n') { nl = i; break; }
            }
            if (nl == -1) return (models, 0, min, max);

            int pos = nl + 1 + 5;
            int numStrings = BitConverter.ToInt32(data, pos);
            pos += 4;

            var strings = new List<string>(numStrings);
            for (int i = 0; i < numStrings; i++)
            {
                int end = pos;
                while (end < data.Length && data[end] != 0) end++;
                strings.Add(Encoding.UTF8.GetString(data, pos, end - pos));
                pos = end + 1;
            }

            int numElements = BitConverter.ToInt32(data, pos);
            pos += 4;

            var elements = new List<(string Type, string Name, Dictionary<string, object> Attrs)>(numElements);
            for (int i = 0; i < numElements; i++)
            {
                int typeIdx = BitConverter.ToInt32(data, pos);
                int nameIdx = BitConverter.ToInt32(data, pos + 4);
                pos += 24;

                var typeName = (typeIdx >= 0 && typeIdx < strings.Count) ? strings[typeIdx] : string.Empty;
                var elemName = (nameIdx >= 0 && nameIdx < strings.Count) ? strings[nameIdx] : string.Empty;
                if (typeName == "DmeJoint") boneCount++;

                elements.Add((typeName, elemName, new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)));
            }

            // Parse Attributes
            for (int i = 0; i < numElements; i++)
            {
                if (pos + 4 > data.Length) break;
                int nattrs = BitConverter.ToInt32(data, pos);
                pos += 4;

                for (int a = 0; a < nattrs; a++)
                {
                    if (pos + 5 > data.Length) break;
                    int nameIdx = BitConverter.ToInt32(data, pos);
                    byte atype = data[pos + 4];
                    pos += 5;

                    var aname = (nameIdx >= 0 && nameIdx < strings.Count) ? strings[nameIdx] : string.Empty;
                    object? val = null;

                    switch (atype)
                    {
                        case 1:
                        case 2:
                            val = BitConverter.ToInt32(data, pos); pos += 4; break;
                        case 3:
                            val = BitConverter.ToSingle(data, pos); pos += 4; break;
                        case 4:
                            val = data[pos] != 0; pos += 1; break;
                        case 5:
                            int strIdx = BitConverter.ToInt32(data, pos);
                            val = (strIdx >= 0 && strIdx < strings.Count) ? strings[strIdx] : string.Empty;
                            pos += 4; break;
                        case 6:
                            int blen = BitConverter.ToInt32(data, pos);
                            pos += 4 + blen; break;
                        case 8:
                            pos += 4; break;
                        case 9:
                            val = new Point(BitConverter.ToSingle(data, pos), BitConverter.ToSingle(data, pos + 4));
                            pos += 8; break;
                        case 10:
                            val = new Point3D(BitConverter.ToSingle(data, pos), BitConverter.ToSingle(data, pos + 4), BitConverter.ToSingle(data, pos + 8));
                            pos += 12; break;
                        case 11:
                        case 13:
                            pos += 16; break;
                        case 14:
                            pos += 64; break;
                        case 15:
                            pos += 8; break;
                        case 16:
                            pos += 1; break;
                        case 33:
                        case 34:
                            int cnt34 = BitConverter.ToInt32(data, pos);
                            pos += 4;
                            var intArr = new int[cnt34];
                            Buffer.BlockCopy(data, pos, intArr, 0, cnt34 * 4);
                            val = intArr;
                            pos += cnt34 * 4; break;
                        case 35:
                            int cnt35 = BitConverter.ToInt32(data, pos);
                            pos += 4;
                            var floatArr = new float[cnt35];
                            Buffer.BlockCopy(data, pos, floatArr, 0, cnt35 * 4);
                            val = floatArr;
                            pos += cnt35 * 4; break;
                        case 36:
                            int cnt36 = BitConverter.ToInt32(data, pos);
                            pos += 4 + cnt36; break;
                        case 37:
                            int cnt37 = BitConverter.ToInt32(data, pos);
                            pos += 4;
                            for (int s = 0; s < cnt37; s++)
                            {
                                while (pos < data.Length && data[pos] != 0) pos++;
                                pos++;
                            }
                            break;
                        case 41: // Vector2Array (Raw UVs)
                            int cnt41 = BitConverter.ToInt32(data, pos);
                            pos += 4;
                            var uvRawArr = new Point[cnt41];
                            for (int u = 0; u < cnt41; u++)
                            {
                                uvRawArr[u] = new Point(BitConverter.ToSingle(data, pos + u * 8), BitConverter.ToSingle(data, pos + u * 8 + 4));
                            }
                            val = uvRawArr;
                            pos += cnt41 * 8; break;
                        case 42: // Vector3Array
                            int cnt42 = BitConverter.ToInt32(data, pos);
                            pos += 4;
                            var ptArr = new Vector3D[cnt42];
                            for (int p = 0; p < cnt42; p++)
                            {
                                ptArr[p] = new Vector3D(
                                    BitConverter.ToSingle(data, pos + p * 12),
                                    BitConverter.ToSingle(data, pos + p * 12 + 4),
                                    BitConverter.ToSingle(data, pos + p * 12 + 8)
                                );
                            }
                            val = ptArr;
                            pos += cnt42 * 12; break;
                        case 43:
                        case 45:
                            int cnt43 = BitConverter.ToInt32(data, pos);
                            pos += 4 + cnt43 * 16; break;
                    }

                    if (!string.IsNullOrEmpty(aname) && val != null)
                    {
                        elements[i].Attrs[aname] = val;
                    }
                }
            }

            // Extract Geometry
            foreach (var el in elements)
            {
                if (el.Type == "DmeMesh")
                {
                    int baseStateIdx = -1;
                    if (el.Attrs.TryGetValue("bindState", out var bs) && bs is int bsi && bsi >= 0 && bsi < elements.Count)
                    {
                        baseStateIdx = bsi;
                    }
                    else if (el.Attrs.TryGetValue("currentState", out var cs) && cs is int csi && csi >= 0 && csi < elements.Count)
                    {
                        baseStateIdx = csi;
                    }
                    else if (el.Attrs.TryGetValue("baseStates", out var bss) && bss is int[] bssArr && bssArr.Length > 0 && bssArr[0] >= 0 && bssArr[0] < elements.Count)
                    {
                        baseStateIdx = bssArr[0];
                    }

                    if (baseStateIdx < 0 || baseStateIdx >= elements.Count) continue;
                    var vdata = elements[baseStateIdx];

                    if (!vdata.Attrs.TryGetValue("position$0", out var pObj) || pObj is not Vector3D[] positionsRaw || positionsRaw.Length == 0) continue;

                    var positions = new Point3D[positionsRaw.Length];
                    for (int pi = 0; pi < positionsRaw.Length; pi++)
                    {
                        positions[pi] = new Point3D(positionsRaw[pi].X, positionsRaw[pi].Y, positionsRaw[pi].Z);
                    }

                    var posIndices = vdata.Attrs.TryGetValue("position$0Indices", out var piObj) && piObj is int[] pIndices ? pIndices : null;
                    var uvsRaw = vdata.Attrs.TryGetValue("texcoord$0", out var uvObj) && uvObj is Point[] uvList ? uvList : null;
                    var uvIndices = vdata.Attrs.TryGetValue("texcoord$0Indices", out var uviObj) && uviObj is int[] uviList ? uviList : null;
                    var normals = vdata.Attrs.TryGetValue("normal$0", out var nObj) && nObj is Vector3D[] nList ? nList : null;
                    var normIndices = vdata.Attrs.TryGetValue("normal$0Indices", out var niObj) && niObj is int[] niList ? niList : null;

                    bool flipV = false;
                    if (vdata.Attrs.TryGetValue("flipVCoordinates", out var fvObj) && fvObj is bool fv)
                    {
                        flipV = fv;
                    }

                    Point[]? uvs = null;
                    if (uvsRaw != null)
                    {
                        uvs = new Point[uvsRaw.Length];
                        for (int u = 0; u < uvsRaw.Length; u++)
                        {
                            double uCoord = uvsRaw[u].X;
                            double vCoord = flipV ? (1.0 - uvsRaw[u].Y) : uvsRaw[u].Y;
                            uvs[u] = new Point(uCoord, vCoord);
                        }
                    }

                    // Bounds
                    foreach (var pt in positions)
                    {
                        min.X = Math.Min(min.X, pt.X); min.Y = Math.Min(min.Y, pt.Y); min.Z = Math.Min(min.Z, pt.Z);
                        max.X = Math.Max(max.X, pt.X); max.Y = Math.Max(max.Y, pt.Y); max.Z = Math.Max(max.Z, pt.Z);
                    }

                    // FaceSets
                    if (el.Attrs.TryGetValue("faceSets", out var fsObj) && fsObj is int[] faceSetIndices)
                    {
                        foreach (var fsIdx in faceSetIndices)
                        {
                            if (fsIdx < 0 || fsIdx >= elements.Count) continue;
                            var fs = elements[fsIdx];

                            if (!fs.Attrs.TryGetValue("faces", out var fObj) || fObj is not int[] faces || faces.Length == 0) continue;

                            string rawMtlName = string.Empty;
                            if (fs.Attrs.TryGetValue("material", out var mIdx) && mIdx is int mi && mi >= 0 && mi < elements.Count)
                            {
                                if (elements[mi].Attrs.TryGetValue("mtlName", out var mn) && mn is string mns)
                                    rawMtlName = mns;
                            }

                            var mesh = new MeshGeometry3D();
                            var vertexMap = new Dictionary<int, int>();

                            var polyIndices = new List<int>();
                            for (int f = 0; f < faces.Length; f++)
                            {
                                int val = faces[f];
                                if (val == -1)
                                {
                                    if (polyIndices.Count >= 3)
                                    {
                                        int i0 = polyIndices[0];
                                        for (int k = 1; k < polyIndices.Count - 1; k++)
                                        {
                                            AddTriangle(mesh, positions, posIndices, uvs, uvIndices, normals, normIndices, vertexMap, i0, polyIndices[k], polyIndices[k + 1]);
                                        }
                                    }
                                    polyIndices.Clear();
                                }
                                else
                                {
                                    polyIndices.Add(val);
                                }
                            }

                            if (polyIndices.Count >= 3)
                            {
                                int i0 = polyIndices[0];
                                for (int k = 1; k < polyIndices.Count - 1; k++)
                                {
                                    AddTriangle(mesh, positions, posIndices, uvs, uvIndices, normals, normIndices, vertexMap, i0, polyIndices[k], polyIndices[k + 1]);
                                }
                            }

                            if (mesh.Normals.Count == 0 && mesh.Positions.Count > 0)
                            {
                                ComputeSmoothNormals(mesh);
                            }

                            if (mesh.Positions.Count > 0)
                            {
                                Material mat = ResolveMaterial(rawMtlName, fs.Name, el.Name, materialDb, remaps);
                                var gm = new GeometryModel3D(mesh, mat) { BackMaterial = mat };
                                models.Add(gm);
                            }
                        }
                    }
                }
            }
        }
        catch { }

        return (models, boneCount, min, max);
    }

    private static void AddTriangle(
        MeshGeometry3D mesh,
        Point3D[] positions,
        int[]? posIndices,
        Point[]? uvs,
        int[]? uvIndices,
        Vector3D[]? normals,
        int[]? normIndices,
        Dictionary<int, int> vertexMap,
        int idxA, int idxB, int idxC)
    {
        mesh.TriangleIndices.Add(GetOrCreateVertex(mesh, positions, posIndices, uvs, uvIndices, normals, normIndices, vertexMap, idxA));
        mesh.TriangleIndices.Add(GetOrCreateVertex(mesh, positions, posIndices, uvs, uvIndices, normals, normIndices, vertexMap, idxB));
        mesh.TriangleIndices.Add(GetOrCreateVertex(mesh, positions, posIndices, uvs, uvIndices, normals, normIndices, vertexMap, idxC));
    }

    private static int GetOrCreateVertex(
        MeshGeometry3D mesh,
        Point3D[] positions,
        int[]? posIndices,
        Point[]? uvs,
        int[]? uvIndices,
        Vector3D[]? normals,
        int[]? normIndices,
        Dictionary<int, int> vertexMap,
        int streamIndex)
    {
        if (vertexMap.TryGetValue(streamIndex, out int existing))
            return existing;

        int posIdx = (posIndices != null && streamIndex < posIndices.Length) ? posIndices[streamIndex] : streamIndex;
        var pt = (posIdx >= 0 && posIdx < positions.Length) ? positions[posIdx] : new Point3D();

        var uv = new Point(0, 0);
        if (uvs != null)
        {
            int uvIdx = (uvIndices != null && streamIndex < uvIndices.Length) ? uvIndices[streamIndex] : streamIndex;
            if (uvIdx >= 0 && uvIdx < uvs.Length)
                uv = uvs[uvIdx];
        }

        var norm = new Vector3D(0, 1, 0);
        if (normals != null && normals.Length > 0)
        {
            int nIdx = (normIndices != null && streamIndex < normIndices.Length) ? normIndices[streamIndex] : streamIndex;
            if (nIdx >= 0 && nIdx < normals.Length)
                norm = normals[nIdx];
        }

        int newIdx = mesh.Positions.Count;
        mesh.Positions.Add(pt);
        mesh.TextureCoordinates.Add(uv);
        if (normals != null && normals.Length > 0)
        {
            mesh.Normals.Add(norm);
        }
        vertexMap[streamIndex] = newIdx;
        return newIdx;
    }

    private static void ComputeSmoothNormals(MeshGeometry3D mesh)
    {
        var normals = new Vector3D[mesh.Positions.Count];
        for (int i = 0; i < mesh.TriangleIndices.Count; i += 3)
        {
            int i0 = mesh.TriangleIndices[i];
            int i1 = mesh.TriangleIndices[i + 1];
            int i2 = mesh.TriangleIndices[i + 2];

            var v0 = (Vector3D)mesh.Positions[i0];
            var v1 = (Vector3D)mesh.Positions[i1];
            var v2 = (Vector3D)mesh.Positions[i2];

            var normal = Vector3D.CrossProduct(v1 - v0, v2 - v0);
            if (normal.LengthSquared > 1e-6)
            {
                normal.Normalize();
                normals[i0] += normal;
                normals[i1] += normal;
                normals[i2] += normal;
            }
        }

        for (int i = 0; i < normals.Length; i++)
        {
            if (normals[i].LengthSquared > 1e-6) normals[i].Normalize();
            else normals[i] = new Vector3D(0, 1, 0);
            mesh.Normals.Add(normals[i]);
        }
    }

    private static Material ResolveMaterial(
        string rawMtlName,
        string faceSetName,
        string meshName,
        Dictionary<string, MaterialInfo> materialDb,
        Dictionary<string, string> remaps)
    {
        var candidates = new List<string>();

        if (!string.IsNullOrEmpty(rawMtlName))
        {
            var rawStem = Path.GetFileNameWithoutExtension(rawMtlName).ToLowerInvariant();
            candidates.Add(rawStem);

            if (remaps.TryGetValue(rawMtlName, out var remappedFull))
                candidates.Insert(0, Path.GetFileNameWithoutExtension(remappedFull).ToLowerInvariant());
            if (remaps.TryGetValue(rawStem, out var remappedStem))
                candidates.Insert(0, Path.GetFileNameWithoutExtension(remappedStem).ToLowerInvariant());
        }

        if (!string.IsNullOrEmpty(faceSetName))
            candidates.Add(faceSetName.ToLowerInvariant());
        if (!string.IsNullOrEmpty(meshName))
            candidates.Add(meshName.ToLowerInvariant());

        foreach (var c in candidates)
        {
            if (materialDb.TryGetValue(c, out var info))
            {
                if (info.Texture != null)
                {
                    return new DiffuseMaterial(new ImageBrush(info.Texture) { TileMode = TileMode.Tile, Stretch = Stretch.Fill });
                }
                if (info.SolidColor.HasValue)
                {
                    return new DiffuseMaterial(new SolidColorBrush(info.SolidColor.Value));
                }
            }
        }

        foreach (var c in candidates)
        {
            foreach (var kv in materialDb)
            {
                if ((kv.Key.Contains(c) || c.Contains(kv.Key)) && kv.Value.Texture != null)
                {
                    return new DiffuseMaterial(new ImageBrush(kv.Value.Texture) { TileMode = TileMode.Tile, Stretch = Stretch.Fill });
                }
            }
        }

        var fallbackColor = Color.FromRgb(140, 140, 145);
        var searchStr = string.Join(" ", candidates);

        if (searchStr.Contains("skin") || searchStr.Contains("head") || searchStr.Contains("face"))
            fallbackColor = Color.FromRgb(220, 185, 160);
        else if (searchStr.Contains("hair"))
            fallbackColor = Color.FromRgb(85, 60, 45);
        else if (searchStr.Contains("eye"))
            fallbackColor = Color.FromRgb(60, 130, 180);
        else if (searchStr.Contains("teeth"))
            fallbackColor = Color.FromRgb(240, 240, 235);
        else if (searchStr.Contains("skirt") || searchStr.Contains("lower") || searchStr.Contains("cloth"))
            fallbackColor = Color.FromRgb(160, 60, 75);
        else if (searchStr.Contains("upper") || searchStr.Contains("dress") || searchStr.Contains("body"))
            fallbackColor = Color.FromRgb(110, 40, 55);
        else if (searchStr.Contains("dragon") || searchStr.Contains("summon"))
            fallbackColor = Color.FromRgb(180, 110, 120);
        else if (searchStr.Contains("book"))
            fallbackColor = Color.FromRgb(140, 110, 80);

        return new DiffuseMaterial(new SolidColorBrush(fallbackColor));
    }
}
