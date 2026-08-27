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

    public static async Task<SimpleMesh3D?> LoadModelFromVmdlAsync(string vmdlPath)
    {
        if (string.IsNullOrWhiteSpace(vmdlPath) || !File.Exists(vmdlPath))
            return null;

        var fullPath = Path.GetFullPath(vmdlPath);
        if (_modelCache.TryGetValue(fullPath, out var cached))
            return cached;

        return await Task.Run(() =>
        {
            var res = LoadModelFromVmdlInternal(fullPath);
            if (res != null) _modelCache[fullPath] = res;
            return res;
        });
    }

    private static SimpleMesh3D? LoadModelFromVmdlInternal(string vmdlPath)
    {
        try
        {
            var content = File.ReadAllText(vmdlPath);
            var vmdlDir = Path.GetDirectoryName(vmdlPath) ?? string.Empty;
            var dmxFiles = ExtractLod0RenderMeshes(content, vmdlDir);

            var compositeMesh = new SimpleMesh3D
            {
                MeshName = Path.GetFileName(vmdlPath)
            };

            foreach (var dmx in dmxFiles)
            {
                var partial = ParseDmxBinary(dmx);
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

            compositeMesh.RecalculateBounds();
            return compositeMesh;
        }
        catch
        {
            return null;
        }
    }

    private static List<string> ExtractLod0RenderMeshes(string vmdlContent, string vmdlDir)
    {
        var result = new List<string>();
        try
        {
            var dmxMatches = Regex.Matches(vmdlContent, "\"([^\\r\\n\"]+\\.dmx)\"", RegexOptions.IgnoreCase);
            foreach (Match m in dmxMatches)
            {
                var rawFile = m.Groups[1].Value.Trim();
                var fn = Path.GetFileNameWithoutExtension(rawFile).ToLowerInvariant();

                if (fn.Contains("_lod") || fn.Contains("lod1") || fn.Contains("lod2") || fn.Contains("lod3") || fn.Contains("lod4"))
                    continue;

                var rel = rawFile.Replace('/', Path.DirectorySeparatorChar);
                var cand1 = Path.Combine(vmdlDir, Path.GetFileName(rel));
                var cand2 = Path.Combine(vmdlDir, rel);

                if (File.Exists(cand1) && !result.Contains(cand1)) result.Add(cand1);
                else if (File.Exists(cand2) && !result.Contains(cand2)) result.Add(cand2);
            }
        }
        catch { }

        return result;
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

                    // Convert Valve Z-up to Standard Y-up
                    var convertedPositions = new Vector3[positions.Length];
                    for (int i = 0; i < positions.Length; i++)
                    {
                        convertedPositions[i] = new Vector3(positions[i].X, positions[i].Z, -positions[i].Y) * 0.0254f; // Inches to meters
                    }

                    int baseVert = mesh.Vertices.Count;
                    mesh.Vertices.AddRange(convertedPositions);

                    if (pIndices != null && pIndices.Length >= 3)
                    {
                        // Triangularize indices
                        for (int k = 0; k < pIndices.Length; k++)
                        {
                            if (pIndices[k] == -1) continue;
                        }

                        // Parse FaceSets
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

            mesh.RecalculateBounds();
            return mesh;
        }
        catch
        {
            return null;
        }
    }
}
