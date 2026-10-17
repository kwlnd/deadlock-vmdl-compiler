namespace DeadlockVmdlCompiler.Services;

/// <summary>Manual fixes of a source .vmdl and the backups written before changing one.</summary>
public static class ModelDocTools
{
    internal static string CreateUniqueBackup(string sourcePath)
    {
        var basePath = sourcePath + ".bak";
        var content = File.ReadAllBytes(sourcePath);
        for (var number = 0; ; number++)
        {
            var destination = number == 0 ? basePath : basePath + "." + number;
            try
            {
                if (File.Exists(destination))
                {
                    // Preserve every previous backup, but do not pile up identical copies.
                    if (File.ReadAllBytes(destination).AsSpan().SequenceEqual(content)) return destination;
                    continue;
                }
                File.Copy(sourcePath, destination, overwrite: false);
                return destination;
            }
            catch (IOException) when (File.Exists(destination))
            {
            }
        }
    }

    public static async Task<OperationResult> SanitizeVmdlForModelDocAsync(
        string vmdlPath,
        bool createBackup = true,
        bool disableAnimationList = true)
    {
        vmdlPath = Path.GetFullPath(vmdlPath);
        if (!File.Exists(vmdlPath))
            return OperationResult.Fail($"File not found: {vmdlPath}");

        var content = await File.ReadAllTextAsync(vmdlPath);
        var (clean, changes) = Ag2Sanitizer.SanitizeVmdlContent(content, disableAnimationList);
        if (clean == content)
            return OperationResult.Ok("VMDL is already clean and ModelDoc compatible; nothing was changed", changed: false);

        if (createBackup)
            CreateUniqueBackup(vmdlPath);
        await File.WriteAllTextAsync(vmdlPath, clean);
        return OperationResult.Ok($"ModelDoc Fix Applied: {string.Join(", ", changes)}", changed: true);
    }
}
