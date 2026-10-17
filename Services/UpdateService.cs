using System.IO.Compression;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DeadlockVmdlCompiler.Services;

public sealed record UpdateResult(
    bool Success,
    bool UpdateAvailable,
    string Message,
    Version? CurrentVersion = null,
    Version? LatestVersion = null,
    string? ReleaseUrl = null,
    string? DownloadedExecutablePath = null,
    string? AssetUrl = null,
    string? AssetName = null);

public static class UpdateService
{
    private const string LatestReleaseApi =
        "https://api.github.com/repos/kwlnd/deadlock-vmdl-compiler/releases/latest";
    private const string ExecutableName = "DeadlockVmdlCompiler.exe";
    private const string PendingUpdateName = "DeadlockVmdlCompiler.update.exe";
    private static readonly HttpClient Http = CreateHttpClient();

    public static Version CurrentVersion
    {
        get
        {
            // Prefer the version stamped into the actual executable. This
            // prevents a release built with a newer FileVersion from being
            // reported as the older assembly version.
            try
            {
                var fileVersion = FileVersionInfo.GetVersionInfo(CurrentExecutablePath).FileVersion;
                var parsed = ParseVersion(fileVersion);
                if (parsed != null) return parsed;
            }
            catch
            {
                // Fall back to the assembly metadata below.
            }

            return NormalizeVersion(
                Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0));
        }
    }

    public static string CurrentExecutablePath
    {
        get
        {
            var processPath = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(processPath) &&
                Path.GetFileName(processPath).Equals(ExecutableName, StringComparison.OrdinalIgnoreCase))
                return Path.GetFullPath(processPath);

            return Path.Combine(AppContext.BaseDirectory, ExecutableName);
        }
    }

    public static async Task<UpdateResult> CheckAsync(
        CancellationToken cancellationToken = default)
    {
        var current = CurrentVersion;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseApi);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();

            await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(responseStream, cancellationToken: cancellationToken);
            var root = document.RootElement;
            var tag = root.TryGetProperty("tag_name", out var tagProperty)
                ? tagProperty.GetString() : null;
            var latest = ParseVersion(tag);
            if (latest == null)
                return new UpdateResult(false, false, "GitHub release did not contain a valid version tag.", current);

            var releaseUrl = root.TryGetProperty("html_url", out var urlProperty)
                ? urlProperty.GetString() : null;
            if (latest <= current)
            {
                return new UpdateResult(true, false,
                    $"current version {current} is up to date.", current, latest, releaseUrl);
            }

            var asset = SelectWindowsAsset(root);
            if (asset == null)
            {
                return new UpdateResult(false, true,
                    $"version {latest} is available, but no DeadlockVmdlCompiler Windows asset was found.",
                    current, latest, releaseUrl);
            }

            return new UpdateResult(true, true,
                $"version {latest} is available.", current, latest, releaseUrl,
                AssetUrl: asset.Value.Url, AssetName: asset.Value.Name);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or JsonException or TaskCanceledException)
        {
            return new UpdateResult(false, false, $"update check failed: {ex.Message}", current);
        }
    }

    public static async Task<UpdateResult> DownloadAsync(
        UpdateResult update,
        CancellationToken cancellationToken = default)
    {
        if (!update.UpdateAvailable || update.LatestVersion == null ||
            string.IsNullOrWhiteSpace(update.AssetUrl) || string.IsNullOrWhiteSpace(update.AssetName))
        {
            return update with { Success = false, Message = "No downloadable update was selected." };
        }

        try
        {
            var downloadedPath = await DownloadExecutableAsync(
                update.AssetUrl, update.AssetName, cancellationToken);
            return update with
            {
                Success = true,
                Message = $"downloaded version {update.LatestVersion}.",
                DownloadedExecutablePath = downloadedPath
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or TaskCanceledException)
        {
            return update with { Success = false, Message = $"update download failed: {ex.Message}" };
        }
    }

    public static bool TryStartRestartWithUpdate(string stagedPath)
    {
        try
        {
            if (!IsValidExecutable(stagedPath)) return false;

            var helper = new ProcessStartInfo
            {
                FileName = stagedPath,
                WorkingDirectory = Path.GetDirectoryName(CurrentExecutablePath) ?? AppContext.BaseDirectory,
                UseShellExecute = true
            };
            helper.ArgumentList.Add("--apply-update");
            helper.ArgumentList.Add(stagedPath);
            helper.ArgumentList.Add(CurrentExecutablePath);
            helper.ArgumentList.Add(Environment.ProcessId.ToString());
            Process.Start(helper);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool TryApplyUpdateCommand(string[] args)
    {
        if (args.Length < 4 || !args[0].Equals("--apply-update", StringComparison.OrdinalIgnoreCase))
            return false;

        var stagedPath = Path.GetFullPath(args[1]);
        var targetPath = Path.GetFullPath(args[2]);
        if (!int.TryParse(args[3], out var parentProcessId))
            return true;

        try
        {
            WaitForParentProcess(parentProcessId);
            if (!IsValidExecutable(stagedPath))
                throw new InvalidDataException("The staged update executable is missing or invalid.");

            var backupPath = targetPath + ".previous.exe";
            if (File.Exists(backupPath)) File.Delete(backupPath);
            File.Move(targetPath, backupPath, overwrite: true);
            try
            {
                File.Copy(stagedPath, targetPath, overwrite: true);
            }
            catch
            {
                File.Move(backupPath, targetPath, overwrite: true);
                throw;
            }

            try { File.Delete(backupPath); } catch { }
            Process.Start(new ProcessStartInfo
            {
                FileName = targetPath,
                WorkingDirectory = Path.GetDirectoryName(targetPath) ?? AppContext.BaseDirectory,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            try
            {
                var logPath = Path.Combine(Path.GetDirectoryName(targetPath) ?? AppContext.BaseDirectory,
                    "update_error.log");
                File.WriteAllText(logPath, ex.ToString());
            }
            catch { }
        }

        return true;
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(2)
        };
        client.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("DeadlockVmdlCompiler", CurrentVersion.ToString()));
        return client;
    }

    private static (string Name, string Url)? SelectWindowsAsset(JsonElement release)
    {
        if (!release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            return null;

        var candidates = assets.EnumerateArray()
            .Select(asset =>
            {
                var name = asset.TryGetProperty("name", out var nameProperty)
                    ? nameProperty.GetString() : null;
                var url = asset.TryGetProperty("browser_download_url", out var urlProperty)
                    ? urlProperty.GetString() : null;
                return (Name: name, Url: url);
            })
            .Where(asset => !string.IsNullOrWhiteSpace(asset.Name) &&
                            !string.IsNullOrWhiteSpace(asset.Url) &&
                            asset.Name.Contains("DeadlockVmdlCompiler", StringComparison.OrdinalIgnoreCase) &&
                            (asset.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
                             asset.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)))
            .Select(asset => (asset.Name!, asset.Url!))
            .ToList();

        if (candidates.Count == 0)
            return null;

        var executable = candidates.FirstOrDefault(asset =>
            asset.Item1.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
        return !string.IsNullOrWhiteSpace(executable.Item1)
            ? executable
            : candidates.FirstOrDefault();
    }

    private static async Task<string> DownloadExecutableAsync(
        string url,
        string assetName,
        CancellationToken cancellationToken)
    {
        var updateDirectory = Path.GetDirectoryName(CurrentExecutablePath) ?? AppContext.BaseDirectory;
        var destination = Path.Combine(updateDirectory, PendingUpdateName);

        var temporary = Path.Combine(updateDirectory,
            $"{PendingUpdateName}.{Guid.NewGuid():N}.download");
        try
        {
            using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 128 * 1024, useAsync: true))
            {
                await input.CopyToAsync(output, cancellationToken);
            }

            if (assetName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                ExtractExecutableFromZip(temporary, destination);
            }
            else
            {
                File.Move(temporary, destination, overwrite: true);
            }

            if (!IsValidExecutable(destination))
                throw new InvalidDataException("The downloaded update is not a valid Windows executable.");

            return destination;
        }
        finally
        {
            try
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
            catch { }
        }
    }

    private static void WaitForParentProcess(int processId)
    {
        try
        {
            using var parent = Process.GetProcessById(processId);
            if (!parent.WaitForExit(30_000))
                throw new IOException("The previous program instance did not exit in time.");
        }
        catch (ArgumentException)
        {
            // The parent already exited.
        }
    }

    private static void ExtractExecutableFromZip(string archivePath, string destination)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        var entry = archive.Entries.FirstOrDefault(item =>
            Path.GetFileName(item.FullName).Equals(ExecutableName, StringComparison.OrdinalIgnoreCase));
        if (entry == null)
            throw new InvalidDataException($"The update archive does not contain {ExecutableName}.");

        var temporary = destination + ".extracting";
        try
        {
            using var input = entry.Open();
            using var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None);
            input.CopyTo(output);
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
            catch { }
        }
    }

    private static bool IsValidExecutable(string path)
    {
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length < 2) return false;
            using var stream = File.OpenRead(path);
            return stream.ReadByte() == 'M' && stream.ReadByte() == 'Z';
        }
        catch
        {
            return false;
        }
    }

    private static Version? ParseVersion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var match = Regex.Match(value, @"\d+(?:\.\d+){1,3}");
        return match.Success && Version.TryParse(match.Value, out var parsed)
            ? NormalizeVersion(parsed) : null;
    }

    private static Version NormalizeVersion(Version version) =>
        new(version.Major, Math.Max(0, version.Minor), Math.Max(0, version.Build));
}
