using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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

    public static Action<string>? DebugLogger { get; set; }

    private static void LogDebug(string msg)
    {
        try { DebugLogger?.Invoke(msg); } catch { }
    }

    public static async Task<SimpleMesh3D?> LoadModelFromVmdlAsync(string vmdlPath, string? citadelDir = null)
    {
        if (string.IsNullOrWhiteSpace(vmdlPath) || !File.Exists(vmdlPath))
        {
            LogDebug("[3D Loader] VMDL path is invalid: " + vmdlPath);
            return null;
        }

        var fullPath = Path.GetFullPath(vmdlPath);
        if (_modelCache.TryGetValue(fullPath, out var cached) && cached != null && cached.Vertices.Count > 0)
        {
            LogDebug("[3D Loader] Model loaded from cache: " + cached.MeshName + " (" + cached.Vertices.Count + " verts)");
            return cached;
        }

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
            LogDebug("[3D Loader] Parsing VMDL: " + vmdlPath);
            var vmdlDir = Path.GetDirectoryName(vmdlPath) ?? string.Empty;
            var vmdlContent = File.ReadAllText(vmdlPath);

            var dmxFiles = ExtractLod0RenderMeshes(vmdlContent, vmdlDir, citadelDir);
            LogDebug("[3D Loader] Found " + dmxFiles.Count + " mesh file(s): " + string.Join(", ", dmxFiles.Select(Path.GetFileName)));

            var compositeMesh = new SimpleMesh3D
            {
                MeshName = Path.GetFileName(vmdlPath)
            };

            foreach (var dmx in dmxFiles)
            {
                var partial = LoadMeshFile(dmx);
                if (partial != null && partial.Vertices.Count > 0)
                {
                    LogDebug("[3D Loader] Parsed " + Path.GetFileName(dmx) + ": " + partial.Vertices.Count + " verts, " + (partial.Indices.Count / 3) + " tris");
                    int baseIndex = compositeMesh.Vertices.Count;
                    compositeMesh.Vertices.AddRange(partial.Vertices);
                    compositeMesh.Normals.AddRange(partial.Normals);

                    foreach (var idx in partial.Indices)
                    {
                        compositeMesh.Indices.Add(baseIndex + idx);
                    }
                    compositeMesh.TriangleColors.AddRange(partial.TriangleColors);
                    compositeMesh.BoneCount = Math.Max(compositeMesh.BoneCount, partial.BoneCount);
                }
            }

            if (compositeMesh.Vertices.Count > 0)
            {
                compositeMesh.RecalculateBounds();
                LogDebug("[3D Loader] Mesh ready: " + compositeMesh.Vertices.Count + " verts, " + (compositeMesh.Indices.Count / 3) + " tris");
                return compositeMesh;
            }

            LogDebug("[3D Loader] No vertices loaded.");
            return null;
        }
        catch (Exception ex)
        {
            LogDebug("[3D Loader Exception] " + ex.Message);
            return null;
        }
    }

    private static List<string> ExtractLod0RenderMeshes(string vmdlContent, string vmdlDir, string? citadelDir)
    {
        var result = new List<string>();
        try
        {
            string? addonRoot = null;
            var cleanDir = vmdlDir.Replace('\\', '/');
            var mIdx = cleanDir.IndexOf("/models/", StringComparison.OrdinalIgnoreCase);
            if (mIdx >= 0)
            {
                addonRoot = cleanDir.Substring(0, mIdx).Replace('/', Path.DirectorySeparatorChar);
            }

            var lines = vmdlContent.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            bool inRenderMeshList = false;

            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (trimmed.Contains("RenderMeshList") || trimmed.Contains("RenderMeshFile"))
                    inRenderMeshList = true;
                if (trimmed.Contains("AnimationList") || trimmed.Contains("AnimFile"))
                    inRenderMeshList = false;

                if (inRenderMeshList && trimmed.StartsWith("filename ="))
                {
                    var q1 = trimmed.IndexOf('\"');
                    var q2 = trimmed.LastIndexOf('\"');
                    if (q1 >= 0 && q2 > q1)
                    {
                        var raw = trimmed.Substring(q1 + 1, q2 - q1 - 1);
                        var fn = Path.GetFileName(raw).ToLowerInvariant();

                        if (fn.Contains("_lod") || fn.Contains("lod1") || fn.Contains("lod2") || fn.Contains("lod3") || fn.Contains("lod4"))
                            continue;

                        var rel = raw.Replace('/', Path.DirectorySeparatorChar);
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
                }
            }

            if (result.Count == 0 && Directory.Exists(vmdlDir))
            {
                var allDmx = Directory.GetFiles(vmdlDir, "*.dmx", SearchOption.AllDirectories)
                    .Where(f => {
                        var fn = Path.GetFileName(f).ToLowerInvariant();
                        return !fn.Contains("_lod") && !fn.Contains("idle") && !fn.Contains("pose") && !fn.Contains("countdown");
                    })
                    .ToList();
                result.AddRange(allDmx);
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
            return ParseDmx(filePath);
        }

        return null;
    }

    private static uint GetMaterialColor(string meshName, string faceSetName)
    {
        var combined = (meshName + " " + faceSetName).ToLowerInvariant();

        if (combined.Contains("skin") || combined.Contains("face") || combined.Contains("head") || combined.Contains("neck") || combined.Contains("arm"))
            return 0xFFFFD7BA;
        if (combined.Contains("hair") || combined.Contains("eyebrow"))
            return 0xFF3D271D;
        if (combined.Contains("eye"))
            return 0xFF3B82F6;
        if (combined.Contains("teeth"))
            return 0xFFF5F5F0;
        if (combined.Contains("beret") || combined.Contains("hat") || combined.Contains("cap"))
            return 0xFF1E293B;
        if (combined.Contains("skirt") || combined.Contains("lower") || combined.Contains("dress") || combined.Contains("cloth"))
            return 0xFF881337;
        if (combined.Contains("upper") || combined.Contains("jacket") || combined.Contains("vest") || combined.Contains("body") || combined.Contains("torso"))
            return 0xFF4C0519;
        if (combined.Contains("book_page") || combined.Contains("page"))
            return 0xFFEDE8D0;
        if (combined.Contains("book"))
            return 0xFF78350F;
        if (combined.Contains("gun") || combined.Contains("weapon") || combined.Contains("metal") || combined.Contains("barrel"))
            return 0xFF64748B;
        if (combined.Contains("dragon") || combined.Contains("summon"))
            return 0xFFDC2626;

        return 0xFF94A3B8;
    }

    private static SimpleMesh3D? ParseDmx(string dmxPath)
    {
        try
        {
            var data = File.ReadAllBytes(dmxPath);
            if (data.Length < 64) return null;

            int pos = 0;
            while (pos < 200 && data[pos] != (byte)'>') pos++;
            pos++;
            while (pos < 200 && (data[pos] == 0x0A || data[pos] == 0x0D || data[pos] == 0x00)) pos++;

            int stringCount = BitConverter.ToInt32(data, pos); pos += 4;
            if (stringCount <= 0 || stringCount > 50000) return null;

            var strings = new List<string>(stringCount);
            for (int s = 0; s < stringCount; s++)
            {
                int start = pos;
                while (pos < data.Length && data[pos] != 0) pos++;
                strings.Add(Encoding.UTF8.GetString(data, start, pos - start));
                pos++;
            }

            int elemCount = BitConverter.ToInt32(data, pos); pos += 4;
            if (elemCount <= 0 || elemCount > 200000) return null;

            var elements = new List<(string Type, string Name, Dictionary<string, object> Attrs)>(elemCount);
            for (int i = 0; i < elemCount; i++)
            {
                int typeIdx = BitConverter.ToInt32(data, pos); pos += 4;
                int nameIdx = BitConverter.ToInt32(data, pos); pos += 4;
                pos += 16;

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
                        case 1: val = BitConverter.ToInt32(data, pos); pos += 4; break;
                        case 2: val = BitConverter.ToInt32(data, pos); pos += 4; break;
                        case 3: val = BitConverter.ToSingle(data, pos); pos += 4; break;
                        case 4: val = data[pos] != 0; pos += 1; break;
                        case 5:
                            int strIdx = BitConverter.ToInt32(data, pos);
                            val = (strIdx >= 0 && strIdx < strings.Count) ? strings[strIdx] : string.Empty;
                            pos += 4; break;
                        case 6: pos += 4 + BitConverter.ToInt32(data, pos); break;
                        case 7: pos += 16; break;
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
                        case 31:
                        case 32:
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
                        default:
                            break;
                    }

                    if (!string.IsNullOrEmpty(aname) && val != null)
                    {
                        elements[i].Attrs[aname] = val;
                    }
                }
            }

            var mesh = new SimpleMesh3D();
            var meshStem = Path.GetFileNameWithoutExtension(dmxPath);

            foreach (var el in elements)
            {
                if (el.Type == "DmeMesh")
                {
                    int bindIdx = -1;
                    if (el.Attrs.TryGetValue("bindState", out var bs) && bs is int bsi && bsi >= 0 && bsi < elements.Count)
                        bindIdx = bsi;
                    else if (el.Attrs.TryGetValue("currentState", out var cs) && cs is int csi && csi >= 0 && csi < elements.Count)
                        bindIdx = csi;

                    if (bindIdx >= 0)
                    {
                        var vd = elements[bindIdx];
                        Vector3[]? positions = null;
                        int[]? posIndices = null;

                        foreach (var kv in vd.Attrs)
                        {
                            if ((kv.Key == "position" || kv.Key == "position" || kv.Key.StartsWith("position")) && kv.Value is Vector3[] pts)
                            {
                                positions = pts;
                            }
                            if ((kv.Key == "positionIndices" || kv.Key == "position" || kv.Key.StartsWith("position") && kv.Key.EndsWith("Indices")) && kv.Value is int[] idxs)
                            {
                                posIndices = idxs;
                            }
                        }

                        if (positions != null && positions.Length > 0)
                        {
                            int baseVert = mesh.Vertices.Count;
                            for (int i = 0; i < positions.Length; i++)
                            {
                                mesh.Vertices.Add(new Vector3(positions[i].X, positions[i].Z, -positions[i].Y) * 0.0254f);
                            }

                            if (el.Attrs.TryGetValue("faceSets", out var fsObj) && fsObj is int[] fsIndices)
                            {
                                foreach (var fsi in fsIndices)
                                {
                                    if (fsi >= 0 && fsi < elements.Count)
                                    {
                                        var fs = elements[fsi];
                                        uint faceColor = GetMaterialColor(meshStem + " " + el.Name, fs.Name);

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
                                                            int f0 = faces[curPolyStart];
                                                            int f1 = faces[curPolyStart + tri];
                                                            int f2 = faces[curPolyStart + tri + 1];

                                                            int v0 = (posIndices != null && f0 >= 0 && f0 < posIndices.Length) ? posIndices[f0] : f0;
                                                            int v1 = (posIndices != null && f1 >= 0 && f1 < posIndices.Length) ? posIndices[f1] : f1;
                                                            int v2 = (posIndices != null && f2 >= 0 && f2 < posIndices.Length) ? posIndices[f2] : f2;

                                                            if (v0 >= 0 && v0 < positions.Length &&
                                                                v1 >= 0 && v1 < positions.Length &&
                                                                v2 >= 0 && v2 < positions.Length)
                                                            {
                                                                mesh.Indices.Add(baseVert + v0);
                                                                mesh.Indices.Add(baseVert + v1);
                                                                mesh.Indices.Add(baseVert + v2);
                                                                mesh.TriangleColors.Add(faceColor);
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
            }

            if (mesh.Vertices.Count > 0)
            {
                mesh.RecalculateBounds();
                return mesh;
            }
        }
        catch (Exception ex)
        {
            LogDebug("[DMX Parse Exception] " + ex.Message);
        }

        return null;
    }
}