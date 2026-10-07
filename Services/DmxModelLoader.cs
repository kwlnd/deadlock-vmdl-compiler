using System;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using DeadlockVmdlCompiler.Models;

namespace DeadlockVmdlCompiler.Services;

public static class DmxModelLoader
{
    public static async Task<SimpleMesh3D?> LoadModelFromVmdlAsync(string vmdlPath, string? citadelDir = null)
    {
        if (string.IsNullOrWhiteSpace(vmdlPath) || !File.Exists(vmdlPath))
            return null;

        var fullPath = Path.GetFullPath(vmdlPath);
        return await Task.Run(() => LoadModelFromVmdlInternal(fullPath, citadelDir));
    }

    private static SimpleMesh3D? LoadModelFromVmdlInternal(string vmdlPath, string? citadelDir)
    {
        try
        {
            var vmdlDir = Path.GetDirectoryName(vmdlPath) ?? string.Empty;
            var vmdlContent = File.ReadAllText(vmdlPath);

            // 1. Resolve material database with VMDL remaps
            var materialDb = BuildMaterialDatabase(vmdlContent, vmdlDir, citadelDir);

            // 2. Resolve render meshes from RenderMeshList
            var dmxFiles = ExtractLod0RenderMeshes(vmdlContent, vmdlDir, citadelDir);

            var compositeMesh = new SimpleMesh3D
            {
                MeshName = Path.GetFileName(vmdlPath)
            };

            foreach (var dmx in dmxFiles)
            {
                ParseDmx(dmx, materialDb, compositeMesh);
            }

            if (compositeMesh.Vertices.Count > 0)
            {
                compositeMesh.RecalculateBounds();
                return compositeMesh;
            }

            return null;
        }
        catch (Exception)
        {
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
                remaps[Path.GetFileName(m.Groups[1].Value)] = m.Groups[2].Value;
            }

            // Map remaps first (highest priority)
            foreach (var kv in remaps)
            {
                var rel = kv.Value.Replace('/', Path.DirectorySeparatorChar);
                var candidates = new List<string>
                {
                    Path.Combine(addonRoot ?? vmdlDir, rel),
                    Path.Combine(vmdlDir, "materials", Path.GetFileName(rel)),
                    Path.Combine(vmdlDir, Path.GetFileName(rel))
                };
                if (!string.IsNullOrEmpty(citadelDir))
                {
                    candidates.Add(Path.Combine(citadelDir, rel));
                    candidates.Add(Path.Combine(citadelDir, "materials", Path.GetFileName(rel)));
                }

                foreach (var c in candidates)
                {
                    if (File.Exists(c))
                    {
                        var tex = LoadMeshTextureFromVmat(c, vmdlDir, addonRoot);
                        if (tex != null)
                        {
                            RegisterMaterialAliases(matDb, c, addonRoot, tex);
                            RegisterMaterialKey(matDb, kv.Key, tex, overwrite: true);
                            RegisterMaterialKey(matDb, Path.GetFileNameWithoutExtension(kv.Key), tex, overwrite: true);
                            break;
                        }
                    }
                }
            }

            // Also index any loose .vmat files in addon materials folder
            var searchDirs = new List<string>();
            var matsDir = Path.Combine(vmdlDir, "materials");
            if (Directory.Exists(matsDir)) searchDirs.Add(matsDir);
            if (Directory.Exists(vmdlDir)) searchDirs.Add(vmdlDir);
            if (!string.IsNullOrEmpty(addonRoot))
            {
                var addonMats = Path.Combine(addonRoot, "materials");
                if (Directory.Exists(addonMats)) searchDirs.Add(addonMats);
            }

            foreach (var dir in searchDirs)
            {
                foreach (var vmat in Directory.GetFiles(dir, "*.vmat", SearchOption.AllDirectories))
                {
                    var materialKey = NormalizeMaterialKey(Path.GetRelativePath(addonRoot ?? vmdlDir, vmat));
                    if (!matDb.ContainsKey(materialKey))
                    {
                        var tex = LoadMeshTextureFromVmat(vmat, vmdlDir, addonRoot);
                        if (tex != null)
                        {
                            RegisterMaterialAliases(matDb, vmat, addonRoot, tex);
                        }
                    }
                }
            }
        }
        catch { }

        return matDb;
    }

    private static string NormalizeMaterialKey(string key) => key.Trim().Replace('\\', '/').TrimStart('/');

    private static void RegisterMaterialKey(Dictionary<string, MeshTexture> materialDb, string key,
        MeshTexture material, bool overwrite = false)
    {
        var normalized = NormalizeMaterialKey(key);
        if (normalized.Length > 0 && (overwrite || !materialDb.ContainsKey(normalized)))
            materialDb[normalized] = material;
    }

    private static void RegisterMaterialAliases(Dictionary<string, MeshTexture> materialDb,
        string vmatPath, string? addonRoot, MeshTexture material)
    {
        if (!string.IsNullOrEmpty(addonRoot))
        {
            var relative = NormalizeMaterialKey(Path.GetRelativePath(addonRoot, vmatPath));
            RegisterMaterialKey(materialDb, relative, material);
            RegisterMaterialKey(materialDb, Path.ChangeExtension(relative, null) ?? relative, material);
        }

        RegisterMaterialKey(materialDb, Path.GetFileName(vmatPath), material);
        RegisterMaterialKey(materialDb, Path.GetFileNameWithoutExtension(vmatPath), material);
    }

    private static MeshTexture? ResolveMaterial(Dictionary<string, MeshTexture> materialDb, string name)
    {
        var key = NormalizeMaterialKey(name);
        if (materialDb.TryGetValue(key, out var material)) return material;
        if (materialDb.TryGetValue(Path.GetFileName(key), out material)) return material;
        if (materialDb.TryGetValue(Path.GetFileNameWithoutExtension(key), out material)) return material;
        // Blender duplicates such as "gun.001" still mean the "gun" material.
        var duplicate = Regex.Match(Path.GetFileName(key), @"^(.+)\.\d{3}$");
        return duplicate.Success && materialDb.TryGetValue(duplicate.Groups[1].Value, out material) ? material : null;
    }

    private static MeshTexture? LoadMeshTextureFromVmat(string vmatPath, string vmdlDir, string? addonRoot)
    {
        try
        {
            var text = File.ReadAllText(vmatPath);
            string? texFile = null;
            // Material Editor writes keys bare; decompiled materials quote them.
            var colorReferences = Regex.Matches(text, @"^\s*""?(?:TextureColor|g_tColor)\d*""?\s+""([^""]+)""",
                RegexOptions.IgnoreCase | RegexOptions.Multiline).Cast<Match>();
            foreach (var colorReference in colorReferences)
            {
                var reference = colorReference.Groups[1].Value;
                if (!Regex.IsMatch(reference, @"\.(png|jpe?g|tga|bmp|vtex(?:_c)?)$", RegexOptions.IgnoreCase))
                    continue;

                var rel = reference.Replace('/', Path.DirectorySeparatorChar);
                if (rel.EndsWith(".vtex", StringComparison.OrdinalIgnoreCase) ||
                    rel.EndsWith(".vtex_c", StringComparison.OrdinalIgnoreCase))
                    rel = Path.ChangeExtension(rel, ".png");
                var fnOnly = Path.GetFileName(rel);

                var candidates = new List<string>
                {
                    Path.Combine(Path.GetDirectoryName(vmatPath) ?? vmdlDir, fnOnly),
                    Path.Combine(vmdlDir, "materials", fnOnly),
                    Path.Combine(vmdlDir, fnOnly),
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
                if (texFile != null) break;
            }

            int fallbackCol = GetFallbackColorFromStem(Path.GetFileNameWithoutExtension(vmatPath));
            var additive = Regex.IsMatch(text, @"^\s*""?F_ADDITIVE_BLEND""?\s+""?1", RegexOptions.IgnoreCase | RegexOptions.Multiline);

            var baseColor = Regex.Match(text, @"^\s*""?TextureColor\d*""?\s+""\[([^\]]+)\]""",
                RegexOptions.IgnoreCase | RegexOptions.Multiline);
            if (TryParseColorVector(baseColor, out var baseR, out var baseG, out var baseB))
                fallbackCol = PackColor(baseR, baseG, baseB);

            var colorTint = Regex.Match(text, @"^\s*""?g_vColorTint\d*""?\s+""\[([^\]]+)\]""",
                RegexOptions.IgnoreCase | RegexOptions.Multiline);
            if (TryParseColorVector(colorTint, out var tintR, out var tintG, out var tintB))
            {
                fallbackCol = PackColor(
                    ((fallbackCol >> 16) & 0xFF) / 255f * tintR,
                    ((fallbackCol >> 8) & 0xFF) / 255f * tintG,
                    (fallbackCol & 0xFF) / 255f * tintB);
            }

            if (!string.IsNullOrEmpty(texFile) && File.Exists(texFile))
            {
                try
                {
                    using var stream = File.OpenRead(texFile);
                    using var bmp = new Bitmap(stream);
                    int w = bmp.PixelSize.Width;
                    int h = bmp.PixelSize.Height;

                    const int maxDim = 1024;
                    var scale = Math.Min(1f, maxDim / (float)Math.Max(w, h));
                    int targetW = Math.Max(1, (int)MathF.Round(w * scale));
                    stream.Position = 0;
                    using var decoded = WriteableBitmap.DecodeToWidth(stream, targetW, BitmapInterpolationMode.MediumQuality);
                    targetW = decoded.PixelSize.Width;
                    int targetH = decoded.PixelSize.Height;
                    using (var locked = decoded.Lock())
                    {
                        if (locked.Format != PixelFormat.Bgra8888)
                            throw new NotSupportedException("Unsupported decoded pixel format: " + locked.Format);
                        var pixels = new int[targetW * targetH];
                        for (var row = 0; row < targetH; row++)
                            Marshal.Copy(IntPtr.Add(locked.Address, row * locked.RowBytes), pixels, row * targetW, targetW);

                        return new MeshTexture
                        {
                            Name = Path.GetFileNameWithoutExtension(vmatPath),
                            Width = targetW,
                            Height = targetH,
                            Pixels = pixels,
                            FallbackColor = fallbackCol,
                            IsAdditive = additive
                        };
                    }
                }
                catch (Exception)
                {
                    // An undecodable texture falls back to the material's flat colour.
                }
            }

            return new MeshTexture
            {
                Name = Path.GetFileNameWithoutExtension(vmatPath),
                FallbackColor = fallbackCol,
                IsAdditive = additive
            };
        }
        catch
        {
            return null;
        }
    }

    private static bool TryParseColorVector(Match match, out float r, out float g, out float b)
    {
        r = g = b = 0;
        if (!match.Success) return false;

        var values = match.Groups[1].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var style = System.Globalization.NumberStyles.Float;
        var culture = System.Globalization.CultureInfo.InvariantCulture;
        return values.Length >= 3 &&
               float.TryParse(values[0], style, culture, out r) &&
               float.TryParse(values[1], style, culture, out g) &&
               float.TryParse(values[2], style, culture, out b);
    }

    private static int PackColor(float r, float g, float b)
    {
        var red = (uint)Math.Clamp((int)(r * 255), 0, 255);
        var green = (uint)Math.Clamp((int)(g * 255), 0, 255);
        var blue = (uint)Math.Clamp((int)(b * 255), 0, 255);
        return unchecked((int)(0xFF000000 | (red << 16) | (green << 8) | blue));
    }

    private static int GetFallbackColorFromStem(string stem)
    {
        var s = stem.ToLowerInvariant();
        if (s.Contains("skin") || s.Contains("head") || s.Contains("face")) return unchecked((int)0xFFFFD7BA);
        if (s.Contains("hair")) return unchecked((int)0xFF3D271D);
        if (s.Contains("eye")) return unchecked((int)0xFF3B82F6);
        if (s.Contains("teeth")) return unchecked((int)0xFFF5F5F0);
        if (s.Contains("beret") || s.Contains("hat")) return unchecked((int)0xFF1E293B);
        if (s.Contains("lower") || s.Contains("skirt") || s.Contains("dress")) return unchecked((int)0xFF881337);
        if (s.Contains("upper") || s.Contains("jacket") || s.Contains("vest") || s.Contains("torso")) return unchecked((int)0xFF4C0519);
        if (s.Contains("book")) return unchecked((int)0xFF78350F);
        if (s.Contains("gun") || s.Contains("weapon")) return unchecked((int)0xFF64748B);
        return unchecked((int)0xFF94A3B8);
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
                if (trimmed.Contains("AnimationList") || trimmed.Contains("AnimFile") ||
                    trimmed.Contains("ClothProxyMesh") || trimmed.Contains("Physics") ||
                    trimmed.Contains("Hitbox") || trimmed.Contains("AttachmentList"))
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

                        // A mesh can carry several UV sets; the material uses the first one.
                        (T[]? Data, int[]? Indices) Stream<T>(string semantic)
                        {
                            var key = vd.Attrs.Keys
                                .Where(name => name.StartsWith(semantic, StringComparison.OrdinalIgnoreCase) &&
                                               !name.EndsWith("Indices", StringComparison.OrdinalIgnoreCase) &&
                                               vd.Attrs[name] is T[])
                                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
                            if (key == null) return (null, null);
                            return ((T[])vd.Attrs[key], vd.Attrs.TryGetValue(key + "Indices", out var found) ? found as int[] : null);
                        }
                        (positions, posIndices) = Stream<Vector3>("position");
                        (normals, normIndices) = Stream<Vector3>("normal");
                        (uvs, uvIndices) = Stream<Vector2>("texcoord");

                        if (positions != null && positions.Length > 0)
                        {
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

                                        string matName = fs.Name;
                                        if (fs.Attrs.TryGetValue("material", out var mObj) && mObj is int mIdx && mIdx >= 0 && mIdx < elements.Count)
                                        {
                                            var materialElement = elements[mIdx];
                                            matName = materialElement.Attrs.TryGetValue("mtlName", out var materialName) &&
                                                      materialName is string name && !string.IsNullOrWhiteSpace(name)
                                                ? name
                                                : materialElement.Name;
                                        }

                                        MeshTexture? targetMat = ResolveMaterial(matDb, matName)
                                            ?? ResolveMaterial(matDb, fs.Name)
                                            ?? ResolveMaterial(matDb, dmxStem)
                                            ?? ResolveMaterial(matDb, el.Name);

                                        if (targetMat == null)
                                        {
                                            targetMat = new MeshTexture { Name = matName, FallbackColor = GetFallbackColorFromStem(matName) };
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
                                                            int f0 = faces[curPolyStart];
                                                            int f1 = faces[curPolyStart + tri];
                                                            int f2 = faces[curPolyStart + tri + 1];

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
        catch (Exception)
        {
            // A DMX the preview cannot read is skipped; the other meshes still show.
        }

        return null;
    }
}
