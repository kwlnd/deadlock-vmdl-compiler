using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
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

            // 1. Resolve material remaps from VMDL
            var materialDb = BuildMaterialDatabase(vmdlContent, vmdlDir, citadelDir);

            // 2. Resolve render meshes from RenderMeshList
            var dmxFiles = ExtractLod0RenderMeshes(vmdlContent, vmdlDir, citadelDir);
            LogDebug("[3D Loader] Found " + dmxFiles.Count + " mesh file(s): " + string.Join(", ", dmxFiles.Select(Path.GetFileName)));

            var compositeMesh = new SimpleMesh3D
            {
                MeshName = Path.GetFileName(vmdlPath)
            };

            foreach (var dmx in dmxFiles)
            {
                var partial = ParseDmx(dmx, materialDb, compositeMesh);
                if (partial != null && partial.Vertices.Count > 0)
                {
                    LogDebug("[3D Loader] Parsed " + Path.GetFileName(dmx) + ": " + partial.Vertices.Count + " verts, " + (partial.Indices.Count / 3) + " tris");
                }
            }

            if (compositeMesh.Vertices.Count > 0)
            {
                compositeMesh.RecalculateBounds();
                LogDebug("[3D Loader] Composite Mesh ready: " + compositeMesh.Vertices.Count + " verts, " + (compositeMesh.Indices.Count / 3) + " tris, " + compositeMesh.Materials.Count + " materials");
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

    private static Dictionary<string, MeshTexture> BuildMaterialDatabase(string vmdlContent, string vmdlDir, string? citadelDir)
    {
        var matDb = new Dictionary<string, MeshTexture>(StringComparer.OrdinalIgnoreCase);

        try
        {
            string? addonRoot = null;
            var cleanDir = vmdlDir.Replace('\\', '/');
            var mIdx = cleanDir.IndexOf("/models/", StringComparison.OrdinalIgnoreCase);
            if (mIdx >= 0)
            {
                addonRoot = cleanDir.Substring(0, mIdx).Replace('/', Path.DirectorySeparatorChar);
            }

            var remaps = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var matches = Regex.Matches(vmdlContent, @"from\s*=\s*""([^""]+)""\s*to\s*=\s*""([^""]+)""");
            foreach (Match m in matches)
            {
                var from = Path.GetFileNameWithoutExtension(m.Groups[1].Value);
                remaps[from] = m.Groups[2].Value;
                remaps[m.Groups[1].Value] = m.Groups[2].Value;
            }

            // Also search all .vmat files in vmdlDir and materials/
            var searchDirs = new List<string>();
            if (Directory.Exists(vmdlDir)) searchDirs.Add(vmdlDir);
            var matsDir = Path.Combine(vmdlDir, "materials");
            if (Directory.Exists(matsDir)) searchDirs.Add(matsDir);
            if (!string.IsNullOrEmpty(addonRoot))
            {
                var addonMats = Path.Combine(addonRoot, "materials");
                if (Directory.Exists(addonMats)) searchDirs.Add(addonMats);
            }

            var allVmats = new List<string>();
            foreach (var d in searchDirs)
            {
                allVmats.AddRange(Directory.GetFiles(d, "*.vmat", SearchOption.AllDirectories));
            }

            foreach (var vmat in allVmats)
            {
                var stem = Path.GetFileNameWithoutExtension(vmat);
                if (!matDb.ContainsKey(stem))
                {
                    var tex = LoadMeshTextureFromVmat(vmat, vmdlDir, addonRoot);
                    if (tex != null)
                    {
                        matDb[stem] = tex;
                        matDb[Path.GetFileName(vmat)] = tex;
                    }
                }
            }

            foreach (var kv in remaps)
            {
                if (!matDb.ContainsKey(kv.Key))
                {
                    var rel = kv.Value.Replace('/', Path.DirectorySeparatorChar);
                    var cand1 = Path.Combine(vmdlDir, Path.GetFileName(rel));
                    var cand2 = Path.Combine(vmdlDir, "materials", Path.GetFileName(rel));
                    var cand3 = !string.IsNullOrEmpty(addonRoot) ? Path.Combine(addonRoot, rel) : null;

                    string? actualVmat = null;
                    if (File.Exists(cand1)) actualVmat = cand1;
                    else if (File.Exists(cand2)) actualVmat = cand2;
                    else if (cand3 != null && File.Exists(cand3)) actualVmat = cand3;

                    if (actualVmat != null)
                    {
                        var tex = LoadMeshTextureFromVmat(actualVmat, vmdlDir, addonRoot);
                        if (tex != null)
                        {
                            matDb[kv.Key] = tex;
                        }
                    }
                }
            }
        }
        catch { }

        return matDb;
    }

    private static MeshTexture? LoadMeshTextureFromVmat(string vmatPath, string vmdlDir, string? addonRoot)
    {
        try
        {
            var text = File.ReadAllText(vmatPath);
            var texMatch = Regex.Match(text, @"TextureColor\d*\s*""([^""]+)""", RegexOptions.IgnoreCase);
            
            string? texFile = null;
            if (texMatch.Success)
            {
                var rel = texMatch.Groups[1].Value.Replace('/', Path.DirectorySeparatorChar);
                var fnOnly = Path.GetFileName(rel);

                var candidates = new List<string>
                {
                    Path.Combine(Path.GetDirectoryName(vmatPath) ?? vmdlDir, fnOnly),
                    Path.Combine(vmdlDir, fnOnly),
                    Path.Combine(vmdlDir, "materials", fnOnly),
                    Path.Combine(vmdlDir, rel)
                };

                if (!string.IsNullOrEmpty(addonRoot))
                {
                    candidates.Add(Path.Combine(addonRoot, rel));
                    candidates.Add(Path.Combine(addonRoot, "materials", fnOnly));
                }

                foreach (var c in candidates)
                {
                    if (File.Exists(c))
                    {
                        texFile = c;
                        break;
                    }
                }
            }

            int fallbackCol = unchecked((int)0xFF94A3B8);
            var colorMatch = Regex.Match(text, @"g_vColorTint\d*\s*""\[([\d\.\s]+)\]""", RegexOptions.IgnoreCase);
            if (colorMatch.Success)
            {
                var nums = colorMatch.Groups[1].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (nums.Length >= 3 &&
                    float.TryParse(nums[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float r) &&
                    float.TryParse(nums[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float g) &&
                    float.TryParse(nums[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float b))
                {
                    byte br = (byte)Math.Clamp((int)(r * 255), 0, 255);
                    byte bg = (byte)Math.Clamp((int)(g * 255), 0, 255);
                    byte bb = (byte)Math.Clamp((int)(b * 255), 0, 255);
                    fallbackCol = unchecked((int)(0xFF000000 | ((uint)br << 16) | ((uint)bg << 8) | bb));
                }
            }

            if (!string.IsNullOrEmpty(texFile) && File.Exists(texFile))
            {
                try
                {
                    using var stream = File.OpenRead(texFile);
                    var bmp = new Bitmap(stream);
                    int w = bmp.PixelSize.Width;
                    int h = bmp.PixelSize.Height;

                    // Downscale if too large to conserve RAM / cache
                    int maxDim = 512;
                    int targetW = w > maxDim ? maxDim : w;
                    int targetH = h > maxDim ? maxDim : h;

                    var wb = new WriteableBitmap(new PixelSize(targetW, targetH), new Avalonia.Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Unpremul);
                    using (var locked = wb.Lock())
                    {
                        bmp.CopyPixels(new PixelRect(0, 0, targetW, targetH), locked.Address, locked.RowBytes * targetH, locked.RowBytes);
                        var pixels = new int[targetW * targetH];
                        Marshal.Copy(locked.Address, pixels, 0, pixels.Length);

                        return new MeshTexture
                        {
                            Name = Path.GetFileNameWithoutExtension(vmatPath),
                            Width = targetW,
                            Height = targetH,
                            Pixels = pixels,
                            FallbackColor = fallbackCol
                        };
                    }
                }
                catch { }
            }

            return new MeshTexture
            {
                Name = Path.GetFileNameWithoutExtension(vmatPath),
                FallbackColor = fallbackCol
            };
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

    private static SimpleMesh3D? ParseDmx(string dmxPath, Dictionary<string, MeshTexture> matDb, SimpleMesh3D compositeMesh)
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
                        case 41:
                            int cnt41 = BitConverter.ToInt32(data, pos); pos += 4;
                            var uvArr = new Vector2[cnt41];
                            for (int p = 0; p < cnt41; p++)
                            {
                                uvArr[p] = new Vector2(
                                    BitConverter.ToSingle(data, pos + p * 8),
                                    BitConverter.ToSingle(data, pos + p * 8 + 4)
                                );
                            }
                            val = uvArr;
                            pos += cnt41 * 8; break;
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

            var dmxStem = Path.GetFileNameWithoutExtension(dmxPath);
            int baseVert = compositeMesh.Vertices.Count;

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
                        Vector3[]? normals = null;
                        int[]? normIndices = null;
                        Vector2[]? uvs = null;
                        int[]? uvIndices = null;

                        foreach (var kv in vd.Attrs)
                        {
                            if (kv.Key.StartsWith("position") && kv.Value is Vector3[] pts) positions = pts;
                            if (kv.Key.StartsWith("position") && kv.Key.EndsWith("Indices") && kv.Value is int[] pIdxs) posIndices = pIdxs;
                            if (kv.Key.StartsWith("normal") && kv.Value is Vector3[] nrms) normals = nrms;
                            if (kv.Key.StartsWith("normal") && kv.Key.EndsWith("Indices") && kv.Value is int[] nIdxs) normIndices = nIdxs;
                            if (kv.Key.StartsWith("texcoord") && kv.Value is Vector2[] uvsArr) uvs = uvsArr;
                            if (kv.Key.StartsWith("texcoord") && kv.Key.EndsWith("Indices") && kv.Value is int[] uIdxs) uvIndices = uIdxs;
                        }

                        if (positions != null && positions.Length > 0)
                        {
                            // Unified Vertex deduplication and insertion
                            int GetOrCreateVert(int faceIdx)
                            {
                                int pI = (posIndices != null && faceIdx < posIndices.Length) ? posIndices[faceIdx] : faceIdx;
                                var p = (pI >= 0 && pI < positions.Length) ? positions[pI] : Vector3.Zero;
                                var vYUp = new Vector3(p.X, p.Z, -p.Y) * 0.0254f;

                                var norm = Vector3.UnitY;
                                if (normals != null && normals.Length > 0)
                                {
                                    int nI = (normIndices != null && faceIdx < normIndices.Length) ? normIndices[faceIdx] : faceIdx;
                                    if (nI >= 0 && nI < normals.Length)
                                    {
                                        var rawN = normals[nI];
                                        norm = Vector3.Normalize(new Vector3(rawN.X, rawN.Z, -rawN.Y));
                                    }
                                }

                                var uv = Vector2.Zero;
                                if (uvs != null && uvs.Length > 0)
                                {
                                    int uI = (uvIndices != null && faceIdx < uvIndices.Length) ? uvIndices[faceIdx] : faceIdx;
                                    if (uI >= 0 && uI < uvs.Length)
                                    {
                                        uv = uvs[uI];
                                    }
                                }

                                int idx = compositeMesh.Vertices.Count;
                                compositeMesh.Vertices.Add(vYUp);
                                compositeMesh.Normals.Add(norm);
                                compositeMesh.TexCoords.Add(uv);
                                return idx;
                            }

                            if (el.Attrs.TryGetValue("faceSets", out var fsObj) && fsObj is int[] fsIndices)
                            {
                                foreach (var fsi in fsIndices)
                                {
                                    if (fsi >= 0 && fsi < elements.Count)
                                    {
                                        var fs = elements[fsi];

                                        // Resolve material
                                        MeshTexture? targetMat = null;
                                        if (matDb.TryGetValue(fs.Name, out var m1)) targetMat = m1;
                                        else if (matDb.TryGetValue(dmxStem, out var m2)) targetMat = m2;
                                        else if (matDb.TryGetValue(el.Name, out var m3)) targetMat = m3;

                                        if (targetMat == null)
                                        {
                                            targetMat = new MeshTexture { Name = fs.Name, FallbackColor = unchecked((int)0xFF94A3B8) };
                                        }

                                        int matId = compositeMesh.Materials.IndexOf(targetMat);
                                        if (matId < 0)
                                        {
                                            matId = compositeMesh.Materials.Count;
                                            compositeMesh.Materials.Add(targetMat);
                                        }

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
                                                            int f0 = curPolyStart;
                                                            int f1 = curPolyStart + tri;
                                                            int f2 = curPolyStart + tri + 1;

                                                            int v0 = GetOrCreateVert(f0);
                                                            int v1 = GetOrCreateVert(f1);
                                                            int v2 = GetOrCreateVert(f2);

                                                            compositeMesh.Indices.Add(v0);
                                                            compositeMesh.Indices.Add(v1);
                                                            compositeMesh.Indices.Add(v2);
                                                            compositeMesh.TriangleMaterialIds.Add(matId);
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

            return compositeMesh;
        }
        catch (Exception ex)
        {
            LogDebug("[DMX Parse Exception] " + ex.Message);
        }

        return null;
    }
}