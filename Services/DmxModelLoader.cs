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
using SkiaSharp;
using ValveResourceFormat;
using ValveResourceFormat.ResourceTypes;
using ValveResourceFormat.TextureDecoders;

namespace DeadlockVmdlCompiler.Services;

public static class DmxModelLoader
{
    private static readonly ConcurrentDictionary<string, SimpleMesh3D> _modelCache = new(StringComparer.OrdinalIgnoreCase);

    public static Action<string>? DebugLogger { get; set; }

    private static void LogDebug(string msg)
    {
        try { DebugLogger?.Invoke(msg); } catch { }
    }

    private static string GetModelCacheKey(string fullPath, string? citadelDir)
    {
        var normalizedCitadelDir = string.IsNullOrWhiteSpace(citadelDir)
            ? string.Empty
            : Path.GetFullPath(citadelDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return fullPath + "\0" + normalizedCitadelDir;
    }

    public static async Task<SimpleMesh3D?> LoadModelFromVmdlAsync(string vmdlPath, string? citadelDir = null)
    {
        if (string.IsNullOrWhiteSpace(vmdlPath) || !File.Exists(vmdlPath))
        {
            LogDebug("[3D Loader] VMDL path is invalid: " + vmdlPath);
            return null;
        }

        var fullPath = Path.GetFullPath(vmdlPath);
        var cacheKey = GetModelCacheKey(fullPath, citadelDir);
        if (_modelCache.TryGetValue(cacheKey, out var cached) && cached != null && cached.Vertices.Count > 0)
        {
            LogDebug("[3D Loader] Model loaded from cache: " + cached.MeshName + " (" + cached.Vertices.Count + " verts)");
            return cached;
        }

        return await Task.Run(() =>
        {
            var res = LoadModelFromVmdlInternal(fullPath, citadelDir);
            if (res != null && res.Vertices.Count > 0)
            {
                _modelCache[cacheKey] = res;
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

            // 1. Resolve material database with VMDL remaps
            var materialDb = BuildMaterialDatabase(vmdlContent, vmdlDir, citadelDir);

            // 2. Resolve render meshes from RenderMeshList
            var dmxFiles = ExtractLod0RenderMeshes(vmdlContent, vmdlDir, citadelDir);
            LogDebug("[3D Loader] Found " + dmxFiles.Count + " render mesh file(s): " + string.Join(", ", dmxFiles.Select(Path.GetFileName)));

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
                LogDebug("[3D Loader] Composite Mesh ready: " + compositeMesh.Vertices.Count + " verts, " + (compositeMesh.Indices.Count / 3) + " tris, " + compositeMesh.Materials.Count + " active materials");
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
                        var tex = LoadMeshTextureFromVmat(c, vmdlDir, addonRoot, citadelDir);
                        if (tex != null)
                        {
                            matDb[kv.Key] = tex;
                            matDb[Path.GetFileNameWithoutExtension(kv.Key)] = tex;
                            matDb[Path.GetFileNameWithoutExtension(c)] = tex;
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
            if (!string.IsNullOrEmpty(citadelDir))
            {
                var citadelMats = Path.Combine(citadelDir, "materials");
                if (Directory.Exists(citadelMats)) searchDirs.Add(citadelMats);
            }

            foreach (var dir in searchDirs)
            {
                foreach (var vmat in Directory.GetFiles(dir, "*.vmat", SearchOption.AllDirectories))
                {
                    var stem = Path.GetFileNameWithoutExtension(vmat);
                    if (!matDb.ContainsKey(stem))
                    {
                        var tex = LoadMeshTextureFromVmat(vmat, vmdlDir, addonRoot, citadelDir);
                        if (tex != null)
                        {
                            matDb[stem] = tex;
                            matDb[Path.GetFileName(vmat)] = tex;
                            matDb[Path.Combine("materials", stem).Replace(Path.DirectorySeparatorChar, '/')] = tex;
                        }
                    }
                }
            }
        }
        catch { }

        return matDb;
    }

    private static MeshTexture? LoadMeshTextureFromVmat(string vmatPath, string vmdlDir, string? addonRoot, string? citadelDir)
    {
        try
        {
            var text = File.ReadAllText(vmatPath);
            // Source 2 materials may expose the base color as TextureColor, TextureColor1,
            // TextureAlbedo, or BaseTexture depending on the shader used by the model.
            var texMatch = Regex.Match(text,
                @"""?(?:TextureColor\d*|TextureAlbedo\d*|BaseTexture|g_tColor)""?\s*""([^""]+)""",
                RegexOptions.IgnoreCase);

            string? texFile = null;
            if (texMatch.Success)
            {
                texFile = ResolveSource2Asset(texMatch.Groups[1].Value, vmatPath, vmdlDir, addonRoot, citadelDir);
            }

            int fallbackCol = unchecked((int)0xFFFFFFFF);
            var tint = Vector3.One;

            // Some materials store TextureColor as an inline vector instead of a
            // texture path. Preserve that authored color for fallback rendering.
            var textureColorMatch = Regex.Match(text,
                @"""?TextureColor\d*""?\s*""\[([\d.+\-eE\s]+)\]""", RegexOptions.IgnoreCase);
            if (textureColorMatch.Success)
            {
                var nums = Regex.Split(textureColorMatch.Groups[1].Value.Trim(), @"\s+");
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

            var colorMatch = Regex.Match(text, @"""?g_vColorTint\d*""?\s*""\[([\d\.\s]+)\]""", RegexOptions.IgnoreCase);
            if (colorMatch.Success)
            {
                var nums = colorMatch.Groups[1].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (nums.Length >= 3 &&
                    float.TryParse(nums[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float r) &&
                    float.TryParse(nums[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float g) &&
                    float.TryParse(nums[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float b))
                {
                    tint = new Vector3(r, g, b);
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
                    // Compiled VTex files are the normal Source 2 case. Decode them with
                    // ValveResourceFormat, then fall back to Avalonia for loose PNG/JPG files.
                    if (texFile.EndsWith(".vtex_c", StringComparison.OrdinalIgnoreCase))
                    {
                        var compiled = LoadCompiledVtex(texFile);
                        if (compiled != null)
                        {
                            compiled.Name = Path.GetFileNameWithoutExtension(vmatPath);
                            compiled.FallbackColor = fallbackCol;
                            compiled.Tint = tint;
                            compiled.AlphaTest = Regex.IsMatch(text, @"F_ALPHA_TEST\s+1\b", RegexOptions.IgnoreCase);
                            return compiled;
                        }
                    }

                    using var bitmap = SKBitmap.Decode(texFile);
                    if (bitmap == null) throw new InvalidDataException("Image decoder did not recognize the texture.");
                    var material = FromBitmap(bitmap);
                    material.Name = Path.GetFileNameWithoutExtension(vmatPath);
                    material.FallbackColor = fallbackCol;
                    material.Tint = tint;
                    material.AlphaTest = Regex.IsMatch(text, @"F_ALPHA_TEST\s+1\b", RegexOptions.IgnoreCase);
                    LogDebug($"[3D Loader] Loaded {texFile}: {bitmap.Width}x{bitmap.Height} -> {material.Width}x{material.Height}");
                    return material;
                }
                catch (Exception ex)
                {
                    LogDebug($"[3D Loader] Texture decode failed [{vmatPath}] ({texFile}): {ex.Message}");
                }
            }
            else if (texMatch.Success)
                LogDebug($"[3D Loader] Missing texture [{vmatPath}]: {texMatch.Groups[1].Value}");
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

    private static string? ResolveSource2Asset(string reference, string vmatPath, string vmdlDir, string? addonRoot, string? citadelDir)
    {
        var rel = reference.Trim().Trim('"').Replace('/', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar);
        if (rel.StartsWith("materials" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            rel = rel[("materials" + Path.DirectorySeparatorChar).Length..];

        var stem = Path.Combine(Path.GetDirectoryName(rel) ?? string.Empty, Path.GetFileNameWithoutExtension(rel));
        var names = new[] { rel, stem, stem + ".vtex_c", stem + ".vtex", stem + ".png", stem + ".jpg", stem + ".jpeg" }
            .Distinct(StringComparer.OrdinalIgnoreCase);
        var roots = new List<string>
        {
            Path.GetDirectoryName(vmatPath) ?? vmdlDir,
            vmdlDir,
            Path.Combine(vmdlDir, "materials")
        };
        if (!string.IsNullOrWhiteSpace(addonRoot))
        {
            roots.Add(addonRoot!);
            roots.Add(Path.Combine(addonRoot!, "materials"));
        }
        if (!string.IsNullOrWhiteSpace(citadelDir))
        {
            roots.Add(citadelDir!);
            roots.Add(Path.Combine(citadelDir!, "materials"));
        }

        foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach (var name in names)
            {
                var candidate = Path.Combine(root, name);
                if (File.Exists(candidate)) return candidate;
            }
        }

        // Some addons keep material references in a sibling/shared addon. A filename-only
        // fallback is safe here because it is only used after exact path candidates fail.
        var leaf = Path.GetFileNameWithoutExtension(rel);
        foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(root)) continue;
            try
            {
                var found = Directory.EnumerateFiles(root, leaf + ".vtex_c", SearchOption.AllDirectories).FirstOrDefault();
                if (found != null) return found;
                found = Directory.EnumerateFiles(root, leaf + ".png", SearchOption.AllDirectories).FirstOrDefault();
                if (found != null) return found;
            }
            catch { }
        }
        return null;
    }

    private static MeshTexture? LoadCompiledVtex(string path)
    {
        try
        {
            using var resource = new Resource();
            resource.Read(path);
            if (resource.DataBlock is not Texture texture) return null;
            using var bitmap = texture.GenerateBitmap(0, Texture.CubemapFace.PositiveX, 0, TextureCodec.None);
            if (bitmap == null || bitmap.Width <= 0 || bitmap.Height <= 0) return null;

            var material = FromBitmap(bitmap);
            material.Name = Path.GetFileNameWithoutExtension(path);
            LogDebug($"[3D Loader] Loaded compiled VTex: {path} ({material.Width}x{material.Height})");
            return material;
        }        catch (Exception ex)
        {
            LogDebug($"[3D Loader] VTex decode failed for {Path.GetFileName(path)}: {ex.Message}");
            return null;
        }
    }

    private static MeshTexture FromBitmap(SKBitmap bitmap)
    {
        // Resize the ENTIRE texture. CopyPixels with a smaller rectangle crops the
        // top-left of the atlas and maps that crop across every UV on the model.
        float ratio = MathF.Min(1, 1024f / Math.Max(bitmap.Width, bitmap.Height));
        int width = Math.Max(1, (int)(bitmap.Width * ratio));
        int height = Math.Max(1, (int)(bitmap.Height * ratio));
        using var resized = bitmap.Resize(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Unpremul),
            new SKSamplingOptions(SKFilterMode.Linear));
        if (resized == null) throw new InvalidDataException("Texture resize failed.");
        var pixels = new int[width * height];
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            var c = resized.GetPixel(x, y);
            pixels[y * width + x] = unchecked((int)(((uint)c.Alpha << 24) | ((uint)c.Red << 16) | ((uint)c.Green << 8) | c.Blue));
        }
        return new MeshTexture { Width = width, Height = height, Pixels = pixels };
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
                                        if (vd.Attrs.TryGetValue("flipVCoordinates", out var flip) && flip is true) uv.Y = 1 - uv.Y;
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
                                    matName = materialElement.Attrs.TryGetValue("mtlName", out var materialPath) && materialPath is string name
                                        ? name : materialElement.Name;
                                        }

                                        MeshTexture? targetMat = null;
                                        if (matDb.TryGetValue(matName, out var m1)) targetMat = m1;
                                        else if (matDb.TryGetValue(Path.GetFileNameWithoutExtension(matName.Replace('/', Path.DirectorySeparatorChar)), out var byStem)) targetMat = byStem;
                                        else if (matDb.TryGetValue(fs.Name, out var m2)) targetMat = m2;
                                        else if (matDb.TryGetValue(dmxStem, out var m3)) targetMat = m3;
                                        else if (matDb.TryGetValue(el.Name, out var m4)) targetMat = m4;

                                        LogDebug($"[3D Loader] Face material {matName} -> {targetMat?.Name ?? "UNRESOLVED"}");
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
        catch (Exception ex)
        {
            LogDebug("[DMX Parse Exception] " + ex.Message);
        }

        return null;
    }
}
