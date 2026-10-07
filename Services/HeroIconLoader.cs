using DeadlockVmdlCompiler.Models;
using ValveResourceFormat;
using ValveResourceFormat.IO;

namespace DeadlockVmdlCompiler.Services;

public static class HeroIconLoader
{
    public static IReadOnlyDictionary<string, byte[]> LoadSmallPortraits(
        string pak01VpkPath, IReadOnlyList<DeadlockHeroModel> heroes)
    {
        var wanted = heroes.Where(hero => hero.IconVpkPath != null).ToDictionary(hero => hero.IconVpkPath!,
            hero => hero.HeroKey, StringComparer.OrdinalIgnoreCase);
        var result = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        var vpkDirectory = Path.GetDirectoryName(pak01VpkPath) ?? string.Empty;
        var vpkBaseName = Path.GetFileNameWithoutExtension(pak01VpkPath);
        if (vpkBaseName.EndsWith("_dir", StringComparison.OrdinalIgnoreCase))
            vpkBaseName = vpkBaseName[..^4];

        foreach (var entry in VpkHeroScanner.ReadVpkDirectory(pak01VpkPath))
        {
            var entryPath = $"{entry.Directory}/{entry.FileName}.{entry.Extension}";
            if (!wanted.TryGetValue(entryPath, out var heroKey)) continue;
            try
            {
                var bytes = VpkHeroScanner.ExtractVpkEntryBytes(
                    entry, pak01VpkPath, vpkDirectory, vpkBaseName);
                if (bytes == null) continue;

                using var resource = new Resource();
                using var input = new MemoryStream(bytes);
                resource.Read(input);
                resource.FileName = entryPath;
                using var texture = new TextureExtract(resource).ToContentFile();
                var png = texture.SubFiles
                    .Where(file => file.FileName.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                    .Select(file => file.Extract?.Invoke())
                    .FirstOrDefault(data => data is { Length: > 0 });
                if (png == null && texture.Data is { Length: >= 8 } directPng &&
                    directPng[0] == 0x89 && directPng[1] == 0x50 &&
                    directPng[2] == 0x4e && directPng[3] == 0x47)
                    png = directPng;
                if (png != null) result[heroKey] = png;
            }
            catch
            {
                // A single unreadable portrait should not hide the rest of the hero list.
            }
        }

        return result;
    }
}
