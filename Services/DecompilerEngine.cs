using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using ValvePak;
using ValveResourceFormat;
using ValveResourceFormat.IO;

namespace DeadlockVmdlCompiler.Services;

public record DecompileProgress(
    int ExtractedCount,
    string CurrentFile,
    string Stage
);

public record DecompileResult(
    bool Success,
    string Message,
    string? MainVmdlPath,
    List<string> ExtractedClothFiles,
    int TotalFilesExtracted,
    TimeSpan Elapsed
);

public static class DecompilerEngine
{
    public static async Task<DecompileResult> DecompileModelAsync(
        string inputSource, // either pak01_dir.vpk or loose .vmdl_c
        string? vpkInternalModelPath, // e.g. "models/heroes_staging/yamato_v2/yamato.vmdl_c" if inputSource is VPK
        string outputDirectory,
        string? gameinfoPath,
        IProgress<DecompileProgress>? progress = null,
        Action<string>? onLog = null,
        CancellationToken cancellationToken = default)
    {
        return await Task.Run(() =>
        {
            var stopwatch = Stopwatch.StartNew();

            var cleanInput = Path.GetFullPath(inputSource.Trim().Trim('\"'));
            if (!File.Exists(cleanInput))
            {
                return new DecompileResult(
                    false,
                    $"input file not found: {cleanInput}",
                    null,
                    [],
                    0,
                    stopwatch.Elapsed
                );
            }

            var cleanOutput = Path.GetFullPath(outputDirectory.Trim().Trim('\"')).TrimEnd('\\', '/');
            Directory.CreateDirectory(cleanOutput);

            string OutputPath(string relativePath)
            {
                var normalized = relativePath.Replace('\\', '/').TrimStart('/');
                if (Path.IsPathRooted(relativePath) ||
                    normalized.Split('/').Any(part => part is "" or "." or ".."))
                    throw new InvalidDataException($"Unsafe resource path in model export: {relativePath}");

                var target = Path.GetFullPath(Path.Combine(cleanOutput,
                    normalized.Replace('/', Path.DirectorySeparatorChar)));
                if (!target.StartsWith(cleanOutput + Path.DirectorySeparatorChar,
                        StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"Resource path leaves addon directory: {relativePath}");
                return target;
            }

            var isVpk = cleanInput.EndsWith(".vpk", StringComparison.OrdinalIgnoreCase);

            var extractedFiles = new List<string>();
            var clothFiles = new List<string>();
            var materialPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string? mainVmdl = null;
            int animCount = 0;
            var lastReportTime = DateTime.MinValue;

            onLog?.Invoke("[native engine] initializing in-process ValveResourceFormat decompiler...");

            try
            {
                Package? package = null;
                GameFileLoader? fileLoader = null;
                Resource? resource = null;

                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (isVpk)
                    {
                        if (string.IsNullOrWhiteSpace(vpkInternalModelPath))
                        {
                            return new DecompileResult(
                                false,
                                "no internal model path specified for vpk archive.",
                                null,
                                [],
                                0,
                                stopwatch.Elapsed
                            );
                        }

                        var cleanFilter = vpkInternalModelPath.Replace('\\', '/').TrimStart('/').Trim();
                        // ValveResourceFormat's LoadFileCompiled automatically appends _c if not present,
                        // so strip _c if user or scanner provided a path ending in _c
                        if (cleanFilter.EndsWith("_c", StringComparison.OrdinalIgnoreCase))
                        {
                            cleanFilter = cleanFilter[..^2];
                        }

                        onLog?.Invoke($"[vpk] opening archive: {Path.GetFileName(cleanInput)}");
                        package = new Package();
                        package.Read(cleanInput);

                        onLog?.Invoke("[vpk] mounting search paths & addons...");
                        fileLoader = new GameFileLoader(package, cleanInput);
                        if (!string.IsNullOrEmpty(gameinfoPath) && File.Exists(gameinfoPath))
                        {
                            fileLoader.FindAndLoadSearchPaths(gameinfoPath);
                        }

                        onLog?.Invoke($"[native vrf] loading compiled model resource: {cleanFilter}");
                        resource = fileLoader.LoadFileCompiled(cleanFilter);
                    }
                    else
                    {
                        // Loose file mode
                        onLog?.Invoke($"[loose] reading compiled model file: {Path.GetFileName(cleanInput)}");
                        resource = new Resource();
                        resource.Read(cleanInput);

                        fileLoader = new GameFileLoader(null, cleanInput);
                        if (!string.IsNullOrEmpty(gameinfoPath) && File.Exists(gameinfoPath))
                        {
                            fileLoader.FindAndLoadSearchPaths(gameinfoPath);
                        }
                    }

                    if (resource == null)
                    {
                        return new DecompileResult(
                            false,
                            "failed to load model resource from archive or disk.",
                            null,
                            [],
                            0,
                            stopwatch.Elapsed
                        );
                    }

                    onLog?.Invoke("[native vrf] reconstructing ModelDoc vmdl & FeModel cloth physics...");
                    var modelExtract = new ModelExtract(resource, fileLoader);
                    using var contentFile = modelExtract.ToContentFile();
                    cancellationToken.ThrowIfCancellationRequested();

                    onLog?.Invoke("[native vrf] saving extracted assets to disk...");

                    void ProcessContentFile(ContentFile cf)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var baseDir = Path.GetDirectoryName(cf.FileName) ?? string.Empty;

                        if (cf.Data != null && cf.Data.Length > 0)
                        {
                            var target = OutputPath(cf.FileName);
                            Directory.CreateDirectory(Path.GetDirectoryName(target)!);

                            byte[] dataBytes = cf.Data;
                            if (target.EndsWith(".vmdl", StringComparison.OrdinalIgnoreCase))
                            {
                                var text = System.Text.Encoding.UTF8.GetString(cf.Data);
                                var (cleanText, changes) = Ag2Sanitizer.SanitizeVmdlContent(text);
                                dataBytes = System.Text.Encoding.UTF8.GetBytes(cleanText);

                                mainVmdl ??= target;
                                onLog?.Invoke($"[vmdl] {Path.GetFileName(target)} ({dataBytes.Length:N0} bytes)");
                                if (changes.Count > 0)
                                {
                                    onLog?.Invoke($"[ag2 clean] {string.Join(", ", changes)}");
                                }
                            }

                            File.WriteAllBytes(target, dataBytes);
                            extractedFiles.Add(target);
                        }

                        foreach (var sub in cf.SubFiles)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            var rel = (sub.FileName.Contains('/') || sub.FileName.Contains('\\'))
                                ? sub.FileName
                                : Path.Combine(baseDir, sub.FileName);

                            var target = OutputPath(rel);
                            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                            var data = sub.Extract?.Invoke();
                            if (data == null) continue;

                            if (target.EndsWith(".vmdl", StringComparison.OrdinalIgnoreCase))
                            {
                                var text = System.Text.Encoding.UTF8.GetString(data);
                                var (cleanText, changes) = Ag2Sanitizer.SanitizeVmdlContent(text);
                                data = System.Text.Encoding.UTF8.GetBytes(cleanText);
                                if (changes.Count > 0)
                                {
                                    onLog?.Invoke($"[ag2 clean] {string.Join(", ", changes)}");
                                }
                            }

                            File.WriteAllBytes(target, data);
                            extractedFiles.Add(target);

                            // ModelExtract writes mesh DMX files, but does not export their materials.
                            // Their binary string table contains the original VPK-relative .vmat paths.
                            if (target.EndsWith(".dmx", StringComparison.OrdinalIgnoreCase))
                            {
                                foreach (Match match in Regex.Matches(Encoding.Latin1.GetString(data),
                                             @"(?<![A-Za-z0-9_./-])(?:[A-Za-z0-9_-]+/)+[A-Za-z0-9_./-]+\.vmat\b",
                                             RegexOptions.IgnoreCase))
                                    materialPaths.Add(match.Value);
                            }

                            var filename = Path.GetFileName(target);
                            var isCloth = filename.EndsWith(".dmx", StringComparison.OrdinalIgnoreCase) &&
                                          (filename.Contains("cloth", StringComparison.OrdinalIgnoreCase) ||
                                           filename.Contains("proxy", StringComparison.OrdinalIgnoreCase) ||
                                           filename.Contains("grid", StringComparison.OrdinalIgnoreCase));

                            if (isCloth)
                            {
                                clothFiles.Add(target);
                                onLog?.Invoke($"[cloth physics] {filename} ({data.Length:N0} bytes)");
                            }
                            else if (filename.EndsWith(".dmx", StringComparison.OrdinalIgnoreCase))
                            {
                                animCount++;
                                if (animCount % 15 == 0 || animCount <= 3)
                                {
                                    onLog?.Invoke($"[dmx] {filename} (#{animCount})");
                                }
                            }

                            var now = DateTime.UtcNow;
                            if ((now - lastReportTime).TotalMilliseconds >= 60)
                            {
                                lastReportTime = now;
                                progress?.Report(new DecompileProgress(
                                    extractedFiles.Count,
                                    filename,
                                    $"extracting: {filename}"
                                ));
                            }
                        }

                        foreach (var add in cf.AdditionalFiles)
                        {
                            ProcessContentFile(add);
                        }
                    }

                    ProcessContentFile(contentFile);

                    if (isVpk)
                    {
                        onLog?.Invoke($"[materials] exporting {materialPaths.Count} model materials and textures...");
                        foreach (var materialPath in materialPaths.OrderBy(path => path))
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            using var materialResource = fileLoader!.LoadFileCompiled(materialPath);
                            if (materialResource == null)
                            {
                                // One absent material must not discard the whole export.
                                onLog?.Invoke($"[material] missing from game files, skipped: {materialPath}");
                                continue;
                            }
                            materialResource.FileName = materialPath + "_c";
                            using var materialFile = new MaterialExtract(materialResource, fileLoader).ToContentFile();
                            materialFile.FileName = materialPath;
                            ProcessContentFile(materialFile);
                            onLog?.Invoke($"[material] {materialPath}");
                        }
                    }

                    // Mandatory post-processing: Ensure ALL .vmdl files in output directory are 100% CSDK12 ModelDoc compatible
                    onLog?.Invoke("[ag2 sanitizer] verifying and cleaning AG2 nodes for CSDK12 ModelDoc...");
                    var allVmdls = Directory.GetFiles(cleanOutput, "*.vmdl", SearchOption.AllDirectories);
                    foreach (var vmdl in allVmdls)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        try
                        {
                            var text = File.ReadAllText(vmdl);
                            var (clean, changes) = Ag2Sanitizer.SanitizeVmdlContent(text);
                            if (changes.Count > 0)
                            {
                                File.WriteAllText(vmdl, clean);
                                onLog?.Invoke($"[ag2 sanitizer] {Path.GetFileName(vmdl)}: {string.Join(", ", changes)}");
                            }
                            else
                            {
                                onLog?.Invoke($"[ag2 sanitizer] {Path.GetFileName(vmdl)} is clean (CSDK12 compatible)");
                            }
                        }
                        catch (Exception ex)
                        {
                            onLog?.Invoke($"[warn] could not post-sanitize {Path.GetFileName(vmdl)}: {ex.Message}");
                        }
                    }
                }
                finally
                {
                    resource?.Dispose();
                    fileLoader?.Dispose();
                    package?.Dispose();
                }

                stopwatch.Stop();

                // Locate main .vmdl if not found during stream
                if (string.IsNullOrEmpty(mainVmdl))
                {
                    mainVmdl = extractedFiles.FirstOrDefault(f => f.EndsWith(".vmdl", StringComparison.OrdinalIgnoreCase));
                }

                if (string.IsNullOrEmpty(mainVmdl) && extractedFiles.Count == 0)
                {
                    return new DecompileResult(
                        false,
                        "decompilation finished, but no files could be generated.",
                        null,
                        clothFiles,
                        0,
                        stopwatch.Elapsed
                    );
                }

                var successMsg = $"successfully decompiled {extractedFiles.Count} file(s) in {stopwatch.Elapsed.TotalSeconds:F1}s!";
                if (clothFiles.Count > 0)
                {
                    successMsg += $" (including {clothFiles.Count} cloth physics asset(s))";
                }

                return new DecompileResult(
                    true,
                    successMsg,
                    mainVmdl,
                    clothFiles,
                    extractedFiles.Count,
                    stopwatch.Elapsed
                );
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                onLog?.Invoke($"[fatal error] {ex.Message}");
                return new DecompileResult(
                    false,
                    $"decompiler error: {ex.Message}",
                    null,
                    clothFiles,
                    extractedFiles.Count,
                    stopwatch.Elapsed
                );
            }
        });
    }
}
