using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using DeadlockVmdlCompiler.Models;
using DeadlockVmdlCompiler.Services;
using DeadlockVmdlCompiler.Views;

namespace DeadlockVmdlCompiler;

// Compile, VPK packaging, game launch and the source tools (fix for modeldoc, export, sync from game).
public partial class MainWindow
{
    private async void BtnCompile_Click(object? sender, RoutedEventArgs e)
    {
        // While resourcecompiler runs, the same button stops it.
        if (_compileCancellation != null)
        {
            _compileCancellation.Cancel();
            BtnCompile.IsEnabled = false;
            TxtCompileBtn.Text = "cancelling...";
            return;
        }
        if (_isProcessing) return;

        var targetPath = GetResolvedTargetPath();
        var csWinDir = TxtCsWinPath.Text?.Trim();
        var citadelDir = TxtCitadelPath.Text?.Trim();

        if (string.IsNullOrEmpty(targetPath) || !File.Exists(targetPath))
        {
            Log("[compile error] target .vmdl file does not exist.");
            await DialogService.ShowErrorAsync(this, "target missing", "target .vmdl file does not exist. please select a valid model.");
            return;
        }

        if (!CsWinCompiler.IsValidCsWinDir(csWinDir))
        {
            Log("[compile error] cswin64 compiler path is invalid or missing resourcecompiler.exe.");
            await DialogService.ShowErrorAsync(this, "compiler missing", "cswin64 compiler path is invalid or missing resourcecompiler.exe.");
            return;
        }

        var requestedSkel = TxtSkel.Text?.Trim();
        var requestedGraph = TxtGraph.Text?.Trim();
        var requestedUiGraph = TxtUiGraph.Text?.Trim();
        var addSkel = ChkSkel.IsChecked == true;
        var addGraph = ChkGraph.IsChecked == true;
        var addUiGraph = ChkUiGraph.IsEnabled && ChkUiGraph.IsChecked == true;
        var namedGraphs = addGraph ? _selectedNamedGraphs : null;
        var expected = ReadAg2Expectation(targetPath);
        var heroFallback = (CmbHeroPreset.SelectedItem as HeroPresetChoice)?.Hero?.VpkPath;

        // Hold the guard while the camera check and an optional sync run, so a second click
        // cannot start another compile; every target input above is already captured.
        _isProcessing = true;
        if (!await PrepareCameraFrameAsync(targetPath, heroFallback))
        {
            _isProcessing = false;
            return;
        }

        try
        {
            _isProcessing = true;
            _compileCancellation = new CancellationTokenSource();
            var cancellation = _compileCancellation.Token;
            var revert = ChkRevert.IsChecked == true;
            var disableAnimationList = ChkDisableAnimList.IsChecked == true;
            var autoDetectAnims = ChkAutoDetectAnims.IsChecked == true;
            var skipMaterialCompilation = ChkSkipMaterialCompilation.IsChecked == true;
            TxtCompileBtn.Text = "cancel compile";

            // Show real-time compilation progress bar under compile button
            PanelCompileProgress.IsVisible = true;
            PrgCompile.Value = 10;
            LblCompilePercent.Text = "10%";
            LblCompileStage.Text = "[1/5] preparing source";
            LblCompileDetail.Text = Path.GetFileName(targetPath);

            Log($"[compile] starting compilation for: {Path.GetFileName(targetPath)}");

            var progress = new Progress<CsWinCompiler.CompileProgress>(p =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    PrgCompile.Value = p.Percent;
                    LblCompilePercent.Text = $"{p.Percent}%";
                    LblCompileStage.Text = p.Stage;
                    LblCompileDetail.Text = p.Detail;
                });
            });

            var compilerErrors = new List<string>();
            // Parsing, asset sync and the compiler all stay off the UI thread;
            // the deploy callbacks touch window state, so they hop back.
            var (success, msg) = await Task.Run(() => CsWinCompiler.ProcessVmdlFileAsync(
                targetPath,
                skelPath: requestedSkel,
                graphPath: requestedGraph,
                uiGraphPath: requestedUiGraph,
                createBackup: true,
                addSkel: addSkel,
                addGraph: addGraph,
                addUiGraph: addUiGraph,
                upgradeHeader: true,
                compileCsWin: true,
                revertVmdl: revert,
                cswinDir: csWinDir,
                citadelAddonsDir: citadelDir,
                // Read Avalonia controls before entering Task.Run. Accessing a
                // control from the compiler worker thread causes "Call from
                // invalid thread" exceptions.
                disableAnimationList: disableAnimationList,
                autoDetectAnims: autoDetectAnims,
                skipMaterialCompilation: skipMaterialCompilation,
                namedGraphs: namedGraphs,
                progress: progress,
                onLog: Log,
                beforeDeploy: deployedPath => Dispatcher.UIThread.Invoke(() => _protected.ReleaseForOutput(deployedPath)),
                afterDeploy: (deployedPath, compilerOutputPath) => Dispatcher.UIThread.Invoke(() =>
                    _protected.Protect(targetPath, deployedPath, compilerOutputPath, expected)),
                cancellationToken: cancellation,
                compilerErrors: compilerErrors
            ), cancellation);
            EndCompileCancellation();

            if (success)
            {
                PrgCompile.Value = 100;
                LblCompilePercent.Text = "100%";
                LblCompileStage.Text = "[5/5] complete";
                LblCompileDetail.Text = "deployed successfully";

                // resourcecompiler can exit cleanly with a broken reference inside the model.
                var errorNote = compilerErrors.Count == 0 ? string.Empty :
                    $"resourcecompiler reported {compilerErrors.Count} error(s); the model was built, but these parts may be broken in game:\n\n" +
                    string.Join("\n", compilerErrors.Take(6).Select(error => "- " + error)) +
                    (compilerErrors.Count > 6 ? $"\n- and {compilerErrors.Count - 6} more in the log" : string.Empty) + "\n\n";
                if (compilerErrors.Count > 0)
                    Log($"[compile warning] model deployed with {compilerErrors.Count} compiler error(s), see ERROR lines above");

                var packVpk = await DialogService.ShowConfirmAsync(
                    this,
                    compilerErrors.Count == 0 ? "compilation successful" : "compiled with errors",
                    (compilerErrors.Count == 0 ? "model compiled and deployed successfully!\n\n" : errorNote) +
                    "would you like to package the addon into a .vpk archive now?"
                );

                if (packVpk)
                {
                    var packed = await MakeVpkAsync(
                        suppressSuccessDialog: false,
                        targetPathOverride: targetPath,
                        citadelDirOverride: citadelDir);
                    if (!packed)
                        await OfferProtectionReleaseAfterFailedPackAsync(targetPath, citadelDir);
                }
                else
                {
                    _protected.ReleaseForSource(targetPath);
                }
            }
            else
            {
                LblCompileStage.Text = "[error] failed";
                LblCompileDetail.Text = msg;

                Log($"[compile error] {msg}");
                await DialogService.ShowErrorAsync(this, "compilation failed", $"compilation failed:\n\n{msg}");
            }
        }
        catch (OperationCanceledException)
        {
            LblCompileStage.Text = "[cancelled]";
            LblCompileDetail.Text = "compilation stopped; nothing was deployed";
            Log("[compile] cancelled by user; nothing was deployed.");
        }
        catch (Exception ex)
        {
            LblCompileStage.Text = "[error] exception";
            LblCompileDetail.Text = ex.Message;

            Log($"[compile exception] {ex.Message}");
            await DialogService.ShowErrorAsync(this, "compile exception", ex.Message);
        }
        finally
        {
            EndCompileCancellation();
            _isProcessing = false;
        }
    }

    /// <summary>
    /// Offers to sync a model that still uses the pre-October 2026 camera frame.
    /// Returns false when the compile should not go ahead.
    /// </summary>
    private async Task<bool> PrepareCameraFrameAsync(string targetPath, string? heroFallback)
    {
        try
        {
            if (!await Task.Run(() => GameSync.UsesLegacyCameraFrameAsync(targetPath)))
                return true;
            var choice = await DialogService.ShowChoiceAsync(this, "old camera attachments",
                "this model was made before the october 2026 update. compiled as it is, its cameras will be misplaced in game.\n\n" +
                "sync and compile updates the .vmdl from the installed game first (camera frame, missing attachments, game data; a backup is written).",
                "sync and compile", "compile anyway");
            if (choice == DialogService.Choice.Cancel) return false;
            if (choice == DialogService.Choice.Alternative) return true;

            var deadlockInfo = DeadlockLocator.DetectDeadlockInstallation();
            var (synced, syncMessage, _) = await Task.Run(() => GameSync.SyncFromGameAsync(targetPath,
                new GameSync.GameSyncOptions(true, true, true),
                deadlockInfo.IsValid ? deadlockInfo.Pak01VpkPath : null, heroFallback, onLog: Log));
            Log(syncMessage);
            _ = UpdateLegacyFrameNoticeAsync(targetPath, _previewRequest);
            if (synced) return true;
            await DialogService.ShowErrorAsync(this, "sync from game failed", syncMessage + "\n\nthe model was not compiled.");
            return false;
        }
        catch (Exception ex)
        {
            Log($"[sync from game error] {ex.Message}");
            await DialogService.ShowErrorAsync(this, "sync from game error", ex.Message + "\n\nthe model was not compiled.");
            return false;
        }
    }

    private void EndCompileCancellation()
    {
        _compileCancellation?.Dispose();
        _compileCancellation = null;
        BtnCompile.IsEnabled = true;
        TxtCompileBtn.Text = "compile model";
    }

    private async Task<bool> MakeVpkAsync(
        bool suppressSuccessDialog = false,
        string? targetPathOverride = null,
        string? citadelDirOverride = null)
    {
        var targetPath = targetPathOverride ?? GetResolvedTargetPath();
        var citadelDir = citadelDirOverride ?? TxtCitadelPath.Text?.Trim();

        if (string.IsNullOrEmpty(targetPath) || string.IsNullOrEmpty(citadelDir))
        {
            Log("[make vpk] please select an addon and target model first.");
            await DialogService.ShowErrorAsync(this, "selection required", "please select an addon and target model first.");
            return false;
        }

        try
        {
            var (_, addonName, subpath) = CsdkPaths.ParseCsdkPath(targetPath, citadelDir);
            var gameAddonDir = CsdkPaths.ResolveGameAddonDir(targetPath, citadelDir, addonName);

            if (!Directory.Exists(gameAddonDir))
            {
                await DialogService.ShowErrorAsync(this, "compiled addon missing", $"compiled game addon directory does not exist:\n{gameAddonDir}\n\nCompile the addon before creating its VPK.");
                return false;
            }

            var compiledPath = Path.Combine(gameAddonDir, subpath + "_c");
            if (!File.Exists(compiledPath))
            {
                await DialogService.ShowErrorAsync(this, "compiled model missing", $"compiled model does not exist:\n{compiledPath}\n\nCompile the selected model before creating its VPK.");
                return false;
            }

            // A protected model is checked against what it was compiled with, not the current settings.
            var expected = _protected.ExpectedFor(targetPath) ?? ReadAg2Expectation(targetPath);
            // Parsing a large compiled model takes a noticeable moment; keep the window responsive.
            var verificationError = await Task.Run(() => expected.VerifyCompiled(compiledPath));
            if (verificationError != null)
            {
                await DialogService.ShowErrorAsync(this, "compiled model missing AG2 references", verificationError);
                return false;
            }

            // Prefer Deadlock's live game addon directory for the VPK save dialog.
            // Fall back to the compiled CSDK addon when Steam detection is unavailable.
            var deadlockInfo = DeadlockLocator.DetectDeadlockInstallation();
            var deadlockAddonsDir = deadlockInfo.IsValid
                ? Path.Combine(Path.GetDirectoryName(deadlockInfo.Pak01VpkPath) ?? string.Empty, "addons")
                : string.Empty;
            var suggestedDirectory = Directory.Exists(deadlockAddonsDir)
                ? deadlockAddonsDir : gameAddonDir;

            // Let user choose destination path & filename
            var suggestedStartLocation = await StorageProvider.TryGetFolderFromPathAsync(suggestedDirectory);
            var saveFile = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "save addon vpk file",
                DefaultExtension = "vpk",
                SuggestedFileName = "pak01_dir.vpk",
                SuggestedStartLocation = suggestedStartLocation,
                FileTypeChoices = new[]
                {
                    new FilePickerFileType("valve pack file (*.vpk)") { Patterns = new[] { "*.vpk" } }
                }
            });

            if (saveFile == null)
            {
                Log("[make vpk] packaging cancelled by user.");
                _protected.ReleaseForAddon(gameAddonDir);
                return false;
            }

            var outputVpk = saveFile.Path.LocalPath;

            var res = await VpkBuilder.PackAddonToVpkAsync(gameAddonDir, outputVpk);
            if (res.Success)
            {
                var packedModelError = await _protected.VerifyPackedAsync(outputVpk, gameAddonDir, compiledPath);
                if (packedModelError != null)
                {
                    Log($"[make vpk error] {packedModelError}");
                    await DialogService.ShowErrorAsync(this, "VPK verification failed", packedModelError);
                    return false;
                }

                _protected.ReleaseForAddon(gameAddonDir);
                Log($"[make vpk] addon packaged successfully: {res.OutputVpkPath} ({res.FileCount} files, {res.TotalBytes / 1024 / 1024:N1} mb)");
                if (!suppressSuccessDialog)
                {
                    await DialogService.ShowInfoAsync(this, "vpk created", "addon packaged into vpk successfully.");
                }
                return true;
            }
            else
            {
                Log($"[make vpk error] {res.Message}");
                await DialogService.ShowErrorAsync(this, "vpk packaging failed", res.Message);
                return false;
            }
        }
        catch (Exception ex)
        {
            Log($"[make vpk error] {ex.Message}");
            await DialogService.ShowErrorAsync(this, "vpk packaging error", ex.Message);
            return false;
        }
    }

    private async void BtnMakeVpk_Click(object? sender, RoutedEventArgs e)
    {
        if (_isProcessing) return;
        var targetPath = GetResolvedTargetPath();
        try
        {
            _isProcessing = true;
            var packed = await MakeVpkAsync(suppressSuccessDialog: false);
            if (!packed && !string.IsNullOrWhiteSpace(targetPath))
                await OfferProtectionReleaseAfterFailedPackAsync(targetPath, TxtCitadelPath.Text?.Trim());
        }
        finally
        {
            _isProcessing = false;
        }
    }

    private void UpdateProtectionStatus()
    {
        TxtMakeVpkBtn.Text = _protected.Count == 0
            ? "make vpk..."
            : $"make vpk... ({_protected.Count} protected)";
    }

    /// <summary>The AG2 references the current settings would inject into this model.</summary>
    private Ag2Expectation ReadAg2Expectation(string targetPath) => Ag2Expectation.ForCompile(targetPath,
        TxtSkel.Text?.Trim(), TxtGraph.Text?.Trim(), TxtUiGraph.Text?.Trim(),
        ChkSkel.IsChecked == true, ChkGraph.IsChecked == true, ChkUiGraph.IsEnabled && ChkUiGraph.IsChecked == true,
        _selectedNamedGraphs);

    private async Task OfferProtectionReleaseAfterFailedPackAsync(string targetPath, string? citadelDir)
    {
        var (_, addonName, _) = CsdkPaths.ParseCsdkPath(targetPath, citadelDir);
        var gameAddonDir = CsdkPaths.ResolveGameAddonDir(targetPath, citadelDir, addonName);
        if (!_protected.AnyInAddon(gameAddonDir))
            return;

        var keepProtected = await DialogService.ShowConfirmAsync(this,
            "VPK was not created",
            "Keep the compiled model protected while you retry packaging?\n\nChoose No to release the protection.");
        if (!keepProtected)
            _protected.ReleaseForAddon(gameAddonDir);
    }

    private async void BtnLaunchGame_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var targetPath = GetResolvedTargetPath();
            var citadelDir = TxtCitadelPath.Text?.Trim();
            if (string.IsNullOrWhiteSpace(targetPath) || string.IsNullOrWhiteSpace(citadelDir))
            {
                Log("[launch error] please select an addon and target model first.");
                await DialogService.ShowErrorAsync(this, "selection required", "please select an addon and target model first.");
                return;
            }

            var (_, addonName, _) = CsdkPaths.ParseCsdkPath(targetPath, citadelDir);

            var deadlockInfo = DeadlockLocator.DetectDeadlockInstallation();
            if (!deadlockInfo.IsValid || !File.Exists(deadlockInfo.DeadlockExePath))
            {
                Log("[launch] could not locate Deadlock automatically; asking for the installation folder.");
                var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
                {
                    Title = "select your Deadlock installation folder",
                    AllowMultiple = false
                });

                if (folders.Count == 0)
                {
                    Log("[launch] Deadlock location selection cancelled.");
                    return;
                }

                var selectedRoot = folders[0].Path.LocalPath;
                deadlockInfo = DeadlockLocator.DetectDeadlockInstallation(selectedRoot);
                if (!deadlockInfo.IsValid || !File.Exists(deadlockInfo.DeadlockExePath))
                {
                    Log($"[launch error] selected folder is not a valid Deadlock installation: {selectedRoot}");
                    await DialogService.ShowErrorAsync(
                        this,
                        "deadlock not found",
                        "the selected folder does not contain a valid Deadlock installation.\n\nselect the folder containing Deadlock's game and bin folders.");
                    return;
                }

                Log($"[launch] using Deadlock installation selected by user: {deadlockInfo.GameRootPath}");
            }

            // Check if the actual game instance is already running
            var runningGame = Process.GetProcessesByName("deadlock")
                .FirstOrDefault(p =>
                {
                    try
                    {
                        return string.Equals(p.MainModule?.FileName, deadlockInfo.DeadlockExePath, StringComparison.OrdinalIgnoreCase);
                    }
                    catch
                    {
                        return false;
                    }
                });

            if (runningGame != null)
            {
                var launchAnother = await DialogService.ShowConfirmAsync(
                    this,
                    "deadlock already running",
                    "an instance of deadlock game is already running.\n\nwould you like to launch another instance anyway?"
                );
                if (!launchAnother) return;
            }

            var psi = new ProcessStartInfo
            {
                FileName = deadlockInfo.DeadlockExePath,
                WorkingDirectory = Path.GetDirectoryName(deadlockInfo.DeadlockExePath)!,
                UseShellExecute = true
            };
            psi.ArgumentList.Add("-addon");
            psi.ArgumentList.Add(addonName);
            psi.ArgumentList.Add("-allowmultiple");

            Process.Start(psi);
            Log($"[launch] started deadlock: {deadlockInfo.DeadlockExePath} -addon {addonName} -allowmultiple");
        }
        catch (Exception ex)
        {
            Log($"[launch error] {ex.Message}");
            await DialogService.ShowErrorAsync(this, "launch failed", $"failed to launch deadlock:\n\n{ex.Message}");
        }
    }

    private async void BtnExportCsWin_Click(object? sender, RoutedEventArgs e)
    {
        if (_isProcessing) return;
        var targetPath = GetResolvedTargetPath();
        var csWinDir = TxtCsWinPath.Text?.Trim();
        var citadelDir = TxtCitadelPath.Text?.Trim();

        if (string.IsNullOrEmpty(targetPath) || string.IsNullOrEmpty(csWinDir))
        {
            Log("[export] cswin64 directory or target model not configured.");
            await DialogService.ShowErrorAsync(this, "configuration required", "cswin64 directory or target model is not configured.");
            return;
        }

        if (!CsWinCompiler.IsValidCsWinDir(csWinDir))
        {
            Log("[export] cswin64 directory is invalid or resourcecompiler.exe is missing.");
            await DialogService.ShowErrorAsync(this, "compiler missing", "cswin64 directory is invalid or missing resourcecompiler.exe.");
            return;
        }

        try
        {
            _isProcessing = true;
            var skel = TxtSkel.Text?.Trim();
            var graph = TxtGraph.Text?.Trim();
            var uiGraph = TxtUiGraph.Text?.Trim();
            var addSkel = ChkSkel.IsChecked == true;
            var addGraph = ChkGraph.IsChecked == true;
            var addUiGraph = ChkUiGraph.IsEnabled && ChkUiGraph.IsChecked == true;
            var namedGraphs = _selectedNamedGraphs;
            var (success, msg, filesCopied) = await Task.Run(() => CsWinCompiler.ExportToCsWinAddonAsync(
                targetPath,
                skelPath: skel,
                graphPath: graph,
                uiGraphPath: uiGraph,
                addSkel: addSkel,
                addGraph: addGraph,
                addUiGraph: addUiGraph,
                cswinDir: csWinDir,
                citadelAddonsDir: citadelDir,
                namedGraphs: namedGraphs
            ));

            Log(msg);
            if (success)
            {
                await DialogService.ShowInfoAsync(this, "export complete", "exported to cswin64 successfully.");
            }
            else
            {
                await DialogService.ShowErrorAsync(this, "export failed", msg);
            }
        }
        catch (Exception ex)
        {
            Log($"[export error] {ex.Message}");
            await DialogService.ShowErrorAsync(this, "export error", ex.Message);
        }
        finally
        {
            _isProcessing = false;
        }
    }

    private async void BtnSanitizeModelDoc_Click(object? sender, RoutedEventArgs e)
    {
        if (_isProcessing) return;
        var targetPath = GetResolvedTargetPath();
        if (string.IsNullOrEmpty(targetPath) || !File.Exists(targetPath))
        {
            Log("[fix modeldoc] please select a target .vmdl file first.");
            await DialogService.ShowErrorAsync(this, "selection required", "please select a target .vmdl model file first.");
            return;
        }

        try
        {
            _isProcessing = true;
            var disableAnimationList = ChkDisableAnimList.IsChecked == true;
            var (success, msg, changed) = await Task.Run(() => ModelDocTools.SanitizeVmdlForModelDocAsync(
                targetPath,
                disableAnimationList: disableAnimationList
            ));
            Log(msg);
            if (success)
            {
                await DialogService.ShowInfoAsync(this, changed ? "modeldoc fixed" : "nothing to fix", msg);
            }
            else
            {
                await DialogService.ShowErrorAsync(this, "modeldoc fix failed", msg);
            }
        }
        catch (Exception ex)
        {
            Log($"[fix modeldoc error] {ex.Message}");
            await DialogService.ShowErrorAsync(this, "fix modeldoc error", ex.Message);
        }
        finally
        {
            _isProcessing = false;
        }
    }

    private async void BtnSyncFromGame_Click(object? sender, RoutedEventArgs e)
    {
        if (_isProcessing) return;
        var targetPath = GetResolvedTargetPath();
        if (string.IsNullOrEmpty(targetPath) || !File.Exists(targetPath))
        {
            Log("[sync from game] please select a target .vmdl file first.");
            await DialogService.ShowErrorAsync(this, "selection required", "please select a target .vmdl model file first.");
            return;
        }

        var allModels = _discoveredAddons
            .SelectMany(addon => addon.HeroModels.Select(model => (Addon: addon.Name, Model: model)))
            .Where(entry => File.Exists(entry.Model.FullPath)).ToList();
        var dialog = new SyncFromGameWindow(targetPath, allModels.Count);
        await dialog.ShowDialog(this);
        if (dialog.Result is not { } options) return;

        try
        {
            _isProcessing = true;
            var deadlockInfo = DeadlockLocator.DetectDeadlockInstallation();
            var pak01 = deadlockInfo.IsValid ? deadlockInfo.Pak01VpkPath : null;
            if (!dialog.AllModels)
            {
                var fallback = (CmbHeroPreset.SelectedItem as HeroPresetChoice)?.Hero?.VpkPath;
                var (success, msg, changed) = await Task.Run(() =>
                    GameSync.SyncFromGameAsync(targetPath, options, pak01, fallback, onLog: Log));
                Log(msg);
                if (success)
                {
                    await DialogService.ShowInfoAsync(this, changed ? "model synced" : "nothing to sync", msg);
                }
                else
                {
                    await DialogService.ShowErrorAsync(this, "sync from game failed", msg);
                }
            }
            else
            {
                var batch = await Task.Run(() => GameSync.SyncAllAsync(allModels, options, pak01, Log));
                var summary = batch.Summary;
                Log($"[sync from game] {summary}");
                if (batch.Failed.Count > 0)
                    await DialogService.ShowErrorAsync(this, "sync finished with errors", summary);
                else
                    await DialogService.ShowInfoAsync(this, "all models synced", summary);
            }
            _ = UpdateLegacyFrameNoticeAsync(targetPath, _previewRequest);
        }
        catch (Exception ex)
        {
            Log($"[sync from game error] {ex.Message}");
            await DialogService.ShowErrorAsync(this, "sync from game error", ex.Message);
        }
        finally
        {
            _isProcessing = false;
        }
    }

    private async void BtnAddAddon_Click(object? sender, RoutedEventArgs e)
    {
        if (_isProcessing) return;
        var contentAddonsDir = TxtCitadelPath.Text?.Trim();
        if (string.IsNullOrWhiteSpace(contentAddonsDir) || !Directory.Exists(contentAddonsDir))
        {
            await DialogService.ShowErrorAsync(this, "CSDK12 folder required",
                "Select CSDK12's content/citadel_addons folder before creating an addon.");
            return;
        }

        var deadlockInfo = DeadlockLocator.DetectDeadlockInstallation();
        string? vpkPath = deadlockInfo.IsValid ? deadlockInfo.Pak01VpkPath : null;
        if (vpkPath == null)
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "select Deadlock pak01_dir.vpk",
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("Deadlock VPK (*.vpk)")
                    {
                        Patterns = new[] { "pak01_dir.vpk", "*.vpk" }
                    }
                }
            });
            if (files.Count == 0) return;
            vpkPath = files[0].Path.LocalPath;
        }

        _ = LoadPresetPortraitsAsync(vpkPath);

        try
        {
            _isProcessing = true;
            var dialog = new AddAddonWindow(contentAddonsDir, vpkPath, Log);
            await dialog.ShowDialog(this);
            if (dialog.Result is not { } result) return;

            Log($"[add addon] created {result.Name}: {result.FileCount} files, " +
                $"{result.ClothFileCount} cloth assets; model: {result.MainVmdlPath}");
            RescanModels();
            var addon = _discoveredAddons.FirstOrDefault(a =>
                a.Name.Equals(result.Name, StringComparison.OrdinalIgnoreCase));
            if (addon != null)
            {
                CmbDiscovered.SelectedItem = addon;
                var model = addon.HeroModels.FirstOrDefault(m =>
                    m.FullPath.Equals(result.MainVmdlPath, StringComparison.OrdinalIgnoreCase));
                if (model != null) CmbTargetVmdl.SelectedItem = model;
            }
            SaveConfig();
        }
        catch (Exception ex)
        {
            Log($"[add addon error] {ex.Message}");
            await DialogService.ShowErrorAsync(this, "Addon creation failed", ex.Message);
        }
        finally
        {
            _isProcessing = false;
        }
    }
}
