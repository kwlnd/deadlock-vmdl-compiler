using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using DeadlockVmdlCompiler.Models;

namespace DeadlockVmdlCompiler.Services;

public static class DmxModelLoader
{
    private static readonly ConcurrentDictionary<string, SimpleMesh3D> _modelCache = new(StringComparer.OrdinalIgnoreCase);

    public static async Task<SimpleMesh3D?> LoadModelFromVmdlAsync(string vmdlPath, string? citadelDir = null)
    {
        if (string.IsNullOrWhiteSpace(vmdlPath) || !File.Exists(vmdlPath))
            return null;

        var fullPath = Path.GetFullPath(vmdlPath);
        if (_modelCache.TryGetValue(fullPath, out var cached))
            return cached;

        return await Task.Run(() =>
        {
            var res = LoadModelFromVmdlInternal(fullPath, citadelDir);
            if (res != null && res.Vertices.Count > 0)
            {
                _modelCache[fullPath] = res;
            }
            return res;
        });
    }

    private static SimpleMesh3D? LoadModelFromVmdlInternal(string vmdlPath, string? citadelDir)
    {
        try
        {
            var vmdlDir = Path.GetDirectoryName(vmdlPath) ?? string.Empty;
            var vmdlContent = File.ReadAllText(vmdlPath);

            var dmxFiles = ExtractLod0RenderMeshes(vmdlContent, vmdlDir, citadelDir);

            var compositeMesh = new SimpleMesh3D
            {
                MeshName = Path.GetFileName(vmdlPath)
            };

            foreach (var dmx in dmxFiles)
            {
                var partial = LoadMeshFile(dmx);
                if (partial != null && partial.Vertices.Count > 0)
                {
                    int baseIndex = compositeMesh.Vertices.Count;
                    compositeMesh.Vertices.AddRange(partial.Vertices);
                    compositeMesh.Normals.AddRange(partial.Normals);

                    foreach (var idx in partial.Indices)
                    {
                        compositeMesh.Indices.Add(baseIndex + idx);
                    }
                    compositeMesh.BoneCount = Math.Max(compositeMesh.BoneCount, partial.BoneCount);
                }
            }

            if (compositeMesh.Vertices.Count > 0)
            {
                compositeMesh.RecalculateBounds();
                return compositeMesh;
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    private static List<string> ExtractLod0RenderMeshes(string vmdlContent, string vmdlDir, string? citadelDir)
    {
        var result = new List<string>();
        try
        {
            // Find addon root (parent of /models/)
            string? addonRoot = null;
            var cleanDir = vmdlDir.Replace('\\', '/');
            var mIdx = cleanDir.IndexOf("/models/", StringComparison.OrdinalIgnoreCase);
            if (mIdx >= 0)
            {
                addonRoot = cleanDir[..mIdx].Replace('/', Path.DirectorySeparatorChar);
            }

            var dmxMatches = Regex.Matches(vmdlContent, "\"([^\\r\\n\"]+\\.(dmx|smd|fbx|obj))\"", RegexOptions.IgnoreCase);
            foreach (Match m in dmxMatches)
            {
                var rawFile = m.Groups[1].Value.Trim();
                var fn = Path.GetFileName(rawFile).ToLowerInvariant();

                if (fn.Contains("_lod") || fn.Contains("lod1") || fn.Contains("lod2") || fn.Contains("lod3") || fn.Contains("lod4"))
                    continue;

                var rel = rawFile.Replace('/', Path.DirectorySeparatorChar);
                var fnOnly = Path.GetFileName(rel);

                var candidates = new List<string>
                {
                    Path.Combine(vmdlDir, fnOnly),
                    Path.Combine(vmdlDir, "mesh", fnOnly),
                    Path.Combine(vmdlDir, rel)
                };

                if (!string.IsNullOrEmpty(addonRoot))
                {
                    candidates.Add(Path.Combine(addonRoot, rel));
                    candidates.Add(Path.Combine(addonRoot, "models", rel));
                    candidates.Add(Path.Combine(addonRoot, fnOnly));
                }

                if (!string.IsNullOrEmpty(citadelDir))
                {
                    candidates.Add(Path.Combine(citadelDir, rel));
                    candidates.Add(Path.Combine(citadelDir, "models", rel));
                }

                var cur = vmdlDir;
                for (int i = 0; i < 5; i++)
                {
                    var parent = Directory.GetParent(cur)?.FullName;
                    if (string.IsNullOrEmpty(parent)) break;
                    candidates.Add(Path.Combine(parent, rel));
                    candidates.Add(Path.Combine(parent, fnOnly));
                    candidates.Add(Path.Combine(parent, "mesh", fnOnly));
                    cur = parent;
                }

                foreach (var cand in candidates)
                {
                    if (File.Exists(cand) && !result.Contains(cand))
                    {
                        result.Add(cand);
                        break;
                    }
                }
            }

            // If still no mesh found from regex, search entire directory tree of vmdlDir and addonRoot
            if (result.Count == 0)
            {
                var searchDirs = new List<string>();
                if (Directory.Exists(vmdlDir)) searchDirs.Add(vmdlDir);
                if (!string.IsNullOrEmpty(addonRoot) && Directory.Exists(addonRoot)) searchDirs.Add(addonRoot);

                foreach (var dir in searchDirs)
                {
                    var foundFiles = Directory.GetFiles(dir, "*.*", SearchOption.AllDirectories)
                        .Where(f => {
                            var ext = Path.GetExtension(f).ToLowerInvariant();
                            var name = Path.GetFileName(f).ToLowerInvariant();
                            return (ext == ".dmx" || ext == ".smd" || ext == ".obj") &&
                                   !name.Contains("_lod") && !name.Contains("lod1") && !name.Contains("lod2");
                        })
                        .ToList();

                    foreach (var f in foundFiles)
                    {
                        if (!result.Contains(f)) result.Add(f);
                    }

                    if (result.Count > 0) break;
                }
            }
        }
        catch { }

        return result;
    }

    private static SimpleMesh3D? LoadMeshFile(string filePath)
    {
        if (!File.Exists(filePath)) return null;

        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        if (ext == ".dmx")
        {
            var bin = ParseDmxBinary(filePath);
            if (bin != null && bin.Vertices.Count > 0) return bin;

            var txt = ParseDmxText(filePath);
            if (txt != null && txt.Vertices.Count > 0) return txt;
        }
        else if (ext == ".obj")
        {
            return ParseObj(filePath);
        }
        else if (ext == ".smd")
        {
            return ParseSmd(filePath);
        }

        return null;
    }

    private static SimpleMesh3D? ParseDmxBinary(string dmxPath)
    {
        try
        {
            var data = File.ReadAllBytes(dmxPath);
            if (data.Length < 100) return null;

            var header = Encoding.ASCII.GetString(data, 0, Math.Min(data.Length, 120));
            if (!header.StartsWith("<!-- DMXFormat", StringComparison.OrdinalIgnoreCase)) return null;

            int nullIdx = -1;
            for (int i = 0; i < Math.Min(data.Length, 300); i++)
            {
                if (data[i] == 0) { nullIdx = i; break; }
            }
            if (nullIdx < 0) return null;

            int pos = nullIdx + 1;
            int prefixLen = BitConverter.ToInt32(data, pos); pos += 4;
            pos += prefixLen;

            int stringCount = BitConverter.ToInt32(data, pos); pos += 4;
            var strings = new List<string>(stringCount);
            for (int s = 0; s < stringCount; s++)
            {
                int start = pos;
                while (pos < data.Length && data[pos] != 0) pos++;
                strings.Add(Encoding.UTF8.GetString(data, start, pos - start));
                pos++;
            }

            int elemCount = BitConverter.ToInt32(data, pos); pos += 4;
            var elements = new List<(string Type, string Name, Dictionary<string, object> Attrs)>(elemCount);

            for (int i = 0; i < elemCount; i++)
            {
                int typeIdx = BitConverter.ToInt32(data, pos); pos += 4;
                int nameIdx = BitConverter.ToInt32(data, pos); pos += 4;
                pos += 16; // Guid

                var type = (typeIdx >= 0 && typeIdx < strings.Count) ? strings[typeIdx] : string.Empty;
                var name = (nameIdx >= 0 && nameIdx < strings.Count) ? strings[nameIdx] : string.Empty;
                elements.Add((type, name, new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)));
            }

            for (int i = 0; i < elemCount; i++)
            {
                int attrCount = BitConverter.ToInt32(data, pos); pos += 4;
                for (int a = 0; a < attrCount; a++)
                {
                    int anameIdx = BitConverter.ToInt32(data, pos); pos += 4;
                    byte atype = data[pos]; pos += 1;
                    var aname = (anameIdx >= 0 && anameIdx < strings.Count) ? strings[anameIdx] : string.Empty;

                    object? val = null;
                    switch (atype)
                    {
                        case 1: val = elements[BitConverter.ToInt32(data, pos)]; pos += 4; break;
                        case 2: val = BitConverter.ToInt32(data, pos); pos += 4; break;
                        case 3: val = BitConverter.ToSingle(data, pos); pos += 4; break;
                        case 4: val = data[pos] != 0; pos += 1; break;
                        case 5:
                            int strIdx = BitConverter.ToInt32(data, pos);
                            val = (strIdx >= 0 && strIdx < strings.Count) ? strings[strIdx] : string.Empty;
                            pos += 4; break;
                        case 6: pos += 4 + BitConverter.ToInt32(data, pos); break;
                        case 8: pos += 4; break;
                        case 9: pos += 8; break;
                        case 10:
                            val = new Vector3(BitConverter.ToSingle(data, pos), BitConverter.ToSingle(data, pos + 4), BitConverter.ToSingle(data, pos + 8));
                            pos += 12; break;
                        case 11:
                        case 13: pos += 16; break;
                        case 14: pos += 64; break;
                        case 15: pos += 8; break;
                        case 16: pos += 1; break;
                        case 33:
                        case 34:
                            int cnt34 = BitConverter.ToInt32(data, pos); pos += 4;
                            var intArr = new int[cnt34];
                            Buffer.BlockCopy(data, pos, intArr, 0, cnt34 * 4);
                            val = intArr;
                            pos += cnt34 * 4; break;
                        case 35:
                            int cnt35 = BitConverter.ToInt32(data, pos); pos += 4;
                            var floatArr = new float[cnt35];
                            Buffer.BlockCopy(data, pos, floatArr, 0, cnt35 * 4);
                            val = floatArr;
                            pos += cnt35 * 4; break;
                        case 36: pos += 4 + BitConverter.ToInt32(data, pos); break;
                        case 37:
                            int cnt37 = BitConverter.ToInt32(data, pos); pos += 4;
                            for (int s = 0; s < cnt37; s++)
                            {
                                while (pos < data.Length && data[pos] != 0) pos++;
                                pos++;
                            }
                            break;
                        case 41: pos += 4 + BitConverter.ToInt32(data, pos) * 8; break;
                        case 42:
                            int cnt42 = BitConverter.ToInt32(data, pos); pos += 4;
                            var ptArr = new Vector3[cnt42];
                            for (int p = 0; p < cnt42; p++)
                            {
                                ptArr[p] = new Vector3(
                                    BitConverter.ToSingle(data, pos + p * 12),
                                    BitConverter.ToSingle(data, pos + p * 12 + 4),
                                    BitConverter.ToSingle(data, pos + p * 12 + 8)
                                );
                            }
                            val = ptArr;
                            pos += cnt42 * 12; break;
                        case 43:
                        case 45: pos += 4 + BitConverter.ToInt32(data, pos) * 16; break;
                    }

                    if (!string.IsNullOrEmpty(aname) && val != null)
                    {
                        elements[i].Attrs[aname] = val;
                    }
                }
            }

            var mesh = new SimpleMesh3D();

            foreach (var el in elements)
            {
                if (el.Type == "DmeMesh")
                {
                    int baseStateIdx = -1;
                    if (el.Attrs.TryGetValue("bindState", out var bs) && bs is int bsi && bsi >= 0 && bsi < elements.Count) baseStateIdx = bsi;
                    else if (el.Attrs.TryGetValue("currentState", out var cs) && cs is int csi && csi >= 0 && csi < elements.Count) baseStateIdx = csi;

                    if (baseStateIdx < 0 || baseStateIdx >= elements.Count) continue;
                    var vdata = elements[baseStateIdx];

                    if (!vdata.Attrs.TryGetValue("position", out var pObj) || pObj is not Vector3[] positions || positions.Length == 0) continue;

                    var pIndices = vdata.Attrs.TryGetValue("position", out var piObj) && piObj is int[] piArr ? piArr : null;

                    var convertedPositions = new Vector3[positions.Length];
                    for (int i = 0; i < positions.Length; i++)
                    {
                        convertedPositions[i] = new Vector3(positions[i].X, positions[i].Z, -positions[i].Y) * 0.0254f;
                    }

                    int baseVert = mesh.Vertices.Count;
                    mesh.Vertices.AddRange(convertedPositions);

                    if (pIndices != null && pIndices.Length >= 3)
                    {
                        if (el.Attrs.TryGetValue("faceSets", out var fsObj) && fsObj is int[] fsIndices)
                        {
                            foreach (var fsi in fsIndices)
                            {
                                if (fsi >= 0 && fsi < elements.Count)
                                {
                                    var fs = elements[fsi];
                                    if (fs.Attrs.TryGetValue("faces", out var fObj) && fObj is int[] faces)
                                    {
                                        int curPolyStart = 0;
                                        for (int fi = 0; fi < faces.Length; fi++)
                                        {
                                            if (faces[fi] == -1)
                                            {
                                                int polyLen = fi - curPolyStart;
                                                if (polyLen >= 3)
                                                {
                                                    for (int tri = 1; tri < polyLen - 1; tri++)
                                                    {
                                                        int i0 = faces[curPolyStart];
                                                        int i1 = faces[curPolyStart + tri];
                                                        int i2 = faces[curPolyStart + tri + 1];

                                                        if (i0 < pIndices.Length && i1 < pIndices.Length && i2 < pIndices.Length)
                                                        {
                                                            mesh.Indices.Add(baseVert + pIndices[i0]);
                                                            mesh.Indices.Add(baseVert + pIndices[i1]);
                                                            mesh.Indices.Add(baseVert + pIndices[i2]);
                                                        }
                                                    }
                                                }
                                                curPolyStart = fi + 1;
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }

            if (mesh.Vertices.Count > 0)
            {
                mesh.RecalculateBounds();
                return mesh;
            }
        }
        catch { }

        return null;
    }

    private static SimpleMesh3D? ParseDmxText(string dmxPath)
    {
        try
        {
            var text = File.ReadAllText(dmxPath);
            var mesh = new SimpleMesh3D();

            var posBlockMatch = Regex.Match(text, @"""position""\s+(?:""vector3_array""\s+)?\[([\s\S]*?)\]", RegexOptions.IgnoreCase);
            if (!posBlockMatch.Success) return null;

            var posLines = posBlockMatch.Groups[1].Value;
            var vecMatches = Regex.Matches(posLines, @"(?:""\s*)?(-?[\d\.]+e?-?\d*)\s+(-?[\d\.]+e?-?\d*)\s+(-?[\d\.]+e?-?\d*)(?:\s*"")?", RegexOptions.IgnoreCase);

            var positions = new List<Vector3>(vecMatches.Count);
            foreach (Match vm in vecMatches)
            {
                if (float.TryParse(vm.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out float x) &&
                    float.TryParse(vm.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out float y) &&
                    float.TryParse(vm.Groups[3].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out float z))
                {
                    positions.Add(new Vector3(x, z, -y) * 0.0254f);
                }
            }

            if (positions.Count == 0) return null;
            mesh.Vertices.AddRange(positions);

            var facesBlockMatch = Regex.Match(text, @"""faces""\s+(?:""int_array""\s+)?\[([\s\S]*?)\]", RegexOptions.IgnoreCase);
            if (facesBlockMatch.Success)
            {
                var faceTokens = Regex.Matches(facesBlockMatch.Groups[1].Value, @"-?\d+");
                var faces = new List<int>(faceTokens.Count);
                foreach (Match ft in faceTokens)
                {
                    if (int.TryParse(ft.Value, out int idx)) faces.Add(idx);
                }

                var piBlockMatch = Regex.Match(text, @"""positionIndices""\s+(?:""int_array""\s+)?\[([\s\S]*?)\]", RegexOptions.IgnoreCase);
                int[]? posIndices = null;
                if (piBlockMatch.Success)
                {
                    var piTokens = Regex.Matches(piBlockMatch.Groups[1].Value, @"-?\d+");
                    var piList = new List<int>(piTokens.Count);
                    foreach (Match pt in piTokens)
                    {
                        if (int.TryParse(pt.Value, out int idx)) piList.Add(idx);
                    }
                    posIndices = piList.ToArray();
                }

                int curStart = 0;
                for (int fi = 0; fi < faces.Count; fi++)
                {
                    if (faces[fi] == -1)
                    {
                        int polyLen = fi - curStart;
                        if (polyLen >= 3)
                        {
                            for (int tri = 1; tri < polyLen - 1; tri++)
                            {
                                int f0 = faces[curStart];
                                int f1 = faces[curStart + tri];
                                int f2 = faces[curStart + tri + 1];

                                int v0 = (posIndices != null && f0 < posIndices.Length) ? posIndices[f0] : f0;
                                int v1 = (posIndices != null && f1 < posIndices.Length) ? posIndices[f1] : f1;
                                int v2 = (posIndices != null && f2 < posIndices.Length) ? posIndices[f2] : f2;

                                if (v0 < positions.Count && v1 < positions.Count && v2 < positions.Count)
                                {
                                    mesh.Indices.Add(v0);
                                    mesh.Indices.Add(v1);
                                    mesh.Indices.Add(v2);
                                }
                            }
                        }
                        curStart = fi + 1;
                    }
                }
            }

            if (mesh.Indices.Count == 0 && mesh.Vertices.Count >= 3)
            {
                for (int i = 0; i < mesh.Vertices.Count - 2; i += 3)
                {
                    mesh.Indices.Add(i);
                    mesh.Indices.Add(i + 1);
                    mesh.Indices.Add(i + 2);
                }
            }

            mesh.RecalculateBounds();
            return mesh;
        }
        catch
        {
            return null;
        }
    }

    private static SimpleMesh3D? ParseObj(string objPath)
    {
        try
        {
            var lines = File.ReadAllLines(objPath);
            var mesh = new SimpleMesh3D();

            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("v "))
                {
                    var parts = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 4 &&
                        float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float x) &&
                        float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float y) &&
                        float.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out float z))
                    {
                        mesh.Vertices.Add(new Vector3(x, y, z));
                    }
                }
                else if (trimmed.StartsWith("f "))
                {
                    var parts = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 4)
                    {
                        var fIdx = new List<int>();
                        for (int i = 1; i < parts.Length; i++)
                        {
                            var seg = parts[i].Split('/')[0];
                            if (int.TryParse(seg, out int vi) && vi > 0)
                            {
                                fIdx.Add(vi - 1);
                            }
                        }

                        for (int tri = 1; tri < fIdx.Count - 1; tri++)
                        {
                            mesh.Indices.Add(fIdx[0]);
                            mesh.Indices.Add(fIdx[tri]);
                            mesh.Indices.Add(fIdx[tri + 1]);
                        }
                    }
                }
            }

            if (mesh.Vertices.Count > 0)
            {
                mesh.RecalculateBounds();
                return mesh;
            }
        }
        catch { }
        return null;
    }

    private static SimpleMesh3D? ParseSmd(string smdPath)
    {
        try
        {
            var lines = File.ReadAllLines(smdPath);
            var mesh = new SimpleMesh3D();
            bool inTriangles = false;
            var triVerts = new List<Vector3>();

            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (trimmed.Equals("triangles", StringComparison.OrdinalIgnoreCase))
                {
                    inTriangles = true;
                    continue;
                }
                if (trimmed.Equals("end", StringComparison.OrdinalIgnoreCase))
                {
                    inTriangles = false;
                    continue;
                }

                if (inTriangles)
                {
                    var parts = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 4 &&
                        float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float x) &&
                        float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float y) &&
                        float.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out float z))
                    {
                        triVerts.Add(new Vector3(x, z, -y) * 0.0254f);
                        if (triVerts.Count == 3)
                        {
                            int baseIdx = mesh.Vertices.Count;
                            mesh.Vertices.AddRange(triVerts);
                            mesh.Indices.Add(baseIdx);
                            mesh.Indices.Add(baseIdx + 1);
                            mesh.Indices.Add(baseIdx + 2);
                            triVerts.Clear();
                        }
                    }
                }
            }

            if (mesh.Vertices.Count > 0)
            {
                mesh.RecalculateBounds();
                return mesh;
            }
        }
        catch { }
        return null;
    }
}
