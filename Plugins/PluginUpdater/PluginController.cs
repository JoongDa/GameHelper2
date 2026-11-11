// <copyright file="PluginController.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace PluginUpdater
{
    using GameHelper;
    using GameHelper.Localization;
    using Newtonsoft.Json;

    /// <summary>Serializes Git, compilation and deployment while the UI reads snapshots.</summary>
    public sealed class PluginController : IDisposable
    {
        private readonly PluginUpdaterSettings settings;
        private readonly PluginLocalization text;
        private readonly string managerDirectory;
        private readonly GitUpdater updater;
        private readonly PluginCompiler compiler;
        private readonly PluginDeployment deployment;
        private readonly CancellationTokenSource cancellation = new();
        private readonly object lifetimeLock = new();
        private readonly HttpClient httpClient = new() { Timeout = TimeSpan.FromSeconds(30) };
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> buildErrors = new(StringComparer.OrdinalIgnoreCase);
        private IReadOnlyList<PluginDescription> availablePlugins = Array.Empty<PluginDescription>();
        private string operationStatus = string.Empty;
        private string notification = string.Empty;
        private string catalogError = string.Empty;
        private int busy;
        private int disposed;
        private int currentProgress;
        private int totalProgress;
        private int restarting;
        private bool startupCheckStarted;
        private volatile bool autoRestartRequested;
        private DateTime lastPeriodicAttempt = DateTime.MinValue;

        public PluginController(PluginUpdaterSettings settings, string managerDirectory)
        {
            this.settings = settings;
            this.managerDirectory = managerDirectory;
            this.text = new PluginLocalization(managerDirectory);
            var hostDirectory = Path.GetDirectoryName(typeof(Core).Assembly.Location)!;
            this.SourceDirectory = Path.Combine(managerDirectory, "Source");
            this.updater = new GitUpdater(this.SourceDirectory, message => this.Log.LogInfo(message), refreshOnStartup: false);
            this.compiler = new PluginCompiler(hostDirectory, managerDirectory, message => this.Log.LogInfo(message));
            this.deployment = new PluginDeployment(hostDirectory, managerDirectory, message => this.Log.LogInfo(message));
            this.updater.ProgressChanged += (current, total) =>
            {
                Volatile.Write(ref this.currentProgress, current);
                Volatile.Write(ref this.totalProgress, total);
            };
            this.httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("GameHelper2-PluginUpdater");
            this.Log.LogInfo(this.text.T("log.ready", "PluginUpdater is ready. Source repositories are kept separately from installed DLLs."));
            this.ReadApplyResult();
            _ = this.RefreshLocalAsync();
        }

        public ConsoleLog Log { get; } = new();
        public string SourceDirectory { get; }
        public bool IsBusy => Volatile.Read(ref this.busy) != 0;
        public string OperationStatus => Volatile.Read(ref this.operationStatus);
        public int CurrentProgress => Volatile.Read(ref this.currentProgress);
        public int TotalProgress => Volatile.Read(ref this.totalProgress);
        public int PendingCount => this.deployment.GetPendingChanges().Count;
        public string Notification => Volatile.Read(ref this.notification);
        public string CatalogError => Volatile.Read(ref this.catalogError);
        public bool IsCatalogLoaded { get; private set; }
        public IReadOnlyList<PluginDescription> AvailablePlugins => Volatile.Read(ref this.availablePlugins);

        public IReadOnlyList<PluginInfo> GetPluginInfo() => this.updater.GetPluginInfo().ToArray();

        public IEnumerable<PluginInfo> GetManualPlugins() => this.updater.GetManualPlugins();

        public void ClearNotification() => this.notification = string.Empty;

        public void Tick()
        {
            if (Volatile.Read(ref this.disposed) != 0 || Volatile.Read(ref this.restarting) != 0)
            {
                return;
            }

            if (this.autoRestartRequested && !this.IsBusy)
            {
                this.autoRestartRequested = false;
                if (this.settings.AutoRestartAfterChanges && this.deployment.HasPendingChanges)
                {
                    this.RestartAndApply();
                    return;
                }
            }

            if (!this.startupCheckStarted && !this.IsBusy)
            {
                this.startupCheckStarted = true;
                if (this.settings.CheckUpdatesOnStartup)
                {
                    _ = this.CheckForUpdatesAsync();
                }
            }

            var now = DateTime.Now;
            if (this.IsBusy || (now - this.lastPeriodicAttempt).TotalMinutes < 1)
            {
                return;
            }

            this.lastPeriodicAttempt = now;
            if (this.settings.AutoCheckUpdates &&
                (now - this.settings.LastUpdateCheck).TotalMinutes >= Math.Max(1, this.settings.UpdateCheckIntervalMinutes))
            {
                _ = this.CheckForUpdatesAsync();
            }
        }

        public Task CheckForUpdatesAsync() => this.RunAsync(
            this.text.T("operation.check", "Checking for updates"), async () =>
            {
                await this.updater.UpdateGitInfoAsync().ConfigureAwait(false);
                this.settings.LastUpdateCheck = DateTime.Now;
                var count = this.updater.GetPluginInfo().Count(this.NeedsUpdate);
                var message = count == 0
                    ? this.text.T("notification.up_to_date", "All tracked repositories are up to date.")
                    : this.text.F("notification.updates", "{0} plugin updates are available.", count);
                this.notification = count == 0 ? string.Empty : message;
                this.Log.LogInfo(message);
            });

        public Task RefreshLocalAsync() => this.RunAsync(
            this.text.T("operation.refresh", "Refreshing local repositories"),
            () => Task.Run(this.updater.UpdateLocal, this.cancellation.Token));

        public Task LoadCatalogAsync(bool force = false)
        {
            if (this.IsCatalogLoaded && !force)
            {
                return Task.CompletedTask;
            }

            return this.RunAsync(this.text.T("operation.catalog", "Loading plugin catalog"), this.ReadCatalogAsync);
        }

        private async Task ReadCatalogAsync()
        {
            this.catalogError = string.Empty;
            try
            {
                string json;
                var source = this.settings.CatalogUrl?.Trim() ?? string.Empty;
                if (string.IsNullOrEmpty(source))
                {
                    json = await File.ReadAllTextAsync(Path.Combine(this.managerDirectory, "catalog.json"), this.cancellation.Token).ConfigureAwait(false);
                }
                else if (Uri.TryCreate(source, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps)
                {
                    json = await this.httpClient.GetStringAsync(uri, this.cancellation.Token).ConfigureAwait(false);
                }
                else
                {
                    var path = Path.GetFullPath(source, this.managerDirectory);
                    json = await File.ReadAllTextAsync(path, this.cancellation.Token).ConfigureAwait(false);
                }

                var catalog = JsonConvert.DeserializeObject<PluginRepositoryData>(json)
                    ?? throw new InvalidDataException("The catalog is empty.");
                var descriptions = (catalog.PluginDescriptions ?? new()).Where(plugin =>
                    plugin != null && !string.IsNullOrWhiteSpace(plugin.Name)).ToArray();
                foreach (var description in descriptions)
                {
                    description.Forks = (description.Forks ?? new()).Where(fork =>
                        fork != null && !string.IsNullOrWhiteSpace(fork.Name) && !string.IsNullOrWhiteSpace(fork.Location)).ToList();
                }

                this.availablePlugins = descriptions;
                this.IsCatalogLoaded = true;
                this.Log.LogSuccess(this.text.F("log.catalog_loaded", "Loaded {0} catalog entries.", this.availablePlugins.Count));
            }
            catch (Exception exception)
            {
                this.catalogError = exception.Message;
                throw;
            }
        }

        public Task CloneRepositoryAsync(string repoUrl) => this.RunAsync(
            this.text.T("operation.clone", "Downloading and compiling plugin"), async () =>
            {
                var plugin = await this.updater.CloneRepositoryAsync(repoUrl).ConfigureAwait(false);
                await this.CompileAsync(plugin).ConfigureAwait(false);
            }, restart: true);

        public Task UpdatePluginAsync(string pluginName, bool force = false) => this.RunAsync(
            this.text.F("operation.update", "Updating {0}", pluginName), async () =>
            {
                if (force)
                {
                    await this.updater.ForceUpdatePluginAsync(pluginName).ConfigureAwait(false);
                }
                else
                {
                    await this.updater.UpdatePluginAsync(pluginName).ConfigureAwait(false);
                }

                await this.CompileAsync(this.FindPlugin(pluginName)).ConfigureAwait(false);
                this.ClearNotification();
            }, restart: true);

        public Task UpdateAllPluginsAsync() => this.RunAsync(
            this.text.T("operation.update_all", "Updating all plugins"), async () =>
            {
                var plugins = this.updater.GetPluginInfo().Where(this.NeedsUpdate).ToArray();
                for (var index = 0; index < plugins.Length; index++)
                {
                    this.cancellation.Token.ThrowIfCancellationRequested();
                    this.currentProgress = index;
                    this.totalProgress = plugins.Length;
                    try
                    {
                        await this.updater.UpdatePluginAsync(plugins[index].Name).ConfigureAwait(false);
                        await this.CompileAsync(this.FindPlugin(plugins[index].Name)).ConfigureAwait(false);
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        this.Log.LogError($"{plugins[index].Name}: {exception.Message}");
                    }
                }

                this.currentProgress = plugins.Length;
                this.ClearNotification();
            }, restart: true);

        public Task RevertPluginAsync(string pluginName) => this.RunAsync(
            this.text.F("operation.revert", "Reverting {0}", pluginName), async () =>
            {
                await this.updater.RevertPluginAsync(pluginName).ConfigureAwait(false);
                await this.CompileAsync(this.FindPlugin(pluginName)).ConfigureAwait(false);
            }, restart: true);

        public Task ChangeBranchAsync(string pluginName, string branch) => this.RunAsync(
            this.text.F("operation.branch", "Switching {0} to {1}", pluginName, branch), async () =>
            {
                await this.updater.ChangeBranchAsync(pluginName, branch).ConfigureAwait(false);
                await this.CompileAsync(this.FindPlugin(pluginName)).ConfigureAwait(false);
            }, restart: true);

        public Task BuildPluginAsync(string pluginName) => this.RunAsync(
            this.text.F("operation.build", "Compiling {0}", pluginName),
            () => this.CompileAsync(this.FindPlugin(pluginName)), restart: true);

        public Task DeletePluginAsync(string pluginName) => this.RunAsync(
            this.text.F("operation.delete", "Deleting {0}", pluginName), () => Task.Run(() =>
            {
                if (this.deployment.InstalledPluginName(pluginName) != null ||
                    this.deployment.GetPendingChanges().Any(change => change.RepositoryName.Equals(pluginName, StringComparison.OrdinalIgnoreCase)))
                {
                    this.deployment.StageDelete(pluginName);
                }
                else
                {
                    this.updater.DeletePlugin(pluginName);
                }

                this.buildErrors.TryRemove(pluginName, out _);
            }, this.cancellation.Token), restart: true);

        public string GetInstallationStatus(string repositoryName)
        {
            if (this.buildErrors.TryGetValue(repositoryName, out var error))
            {
                return this.text.F("status.build_failed", "Compilation failed: {0}", error);
            }

            var pending = this.deployment.GetPendingChanges().FirstOrDefault(change =>
                change.RepositoryName.Equals(repositoryName, StringComparison.OrdinalIgnoreCase));
            if (pending != null)
            {
                return pending.Delete
                    ? this.text.T("status.delete_pending", "Deletion pending restart")
                    : this.text.F("status.build_pending", "Compiled {0}; pending restart", ShortCommit(pending.Commit));
            }

            if (this.deployment.GetInstalledPlugins().TryGetValue(repositoryName, out var installed))
            {
                var enabled = GetEnabledState(installed.PluginName);
                if (enabled.HasValue)
                {
                    return enabled.Value
                        ? this.text.F("status.loaded_enabled", "Loaded and enabled {0} ({1})", installed.PluginName, ShortCommit(installed.Commit))
                        : this.text.F("status.loaded_disabled", "Loaded and disabled {0} ({1})", installed.PluginName, ShortCommit(installed.Commit));
                }

                return this.text.F("status.installed", "Installed {0} ({1})", installed.PluginName, ShortCommit(installed.Commit));
            }

            return this.text.T("status.source_only", "Source downloaded; build to install");
        }

        public void RestartAndApply()
        {
            if (this.IsBusy || !this.deployment.HasPendingChanges || Interlocked.CompareExchange(ref this.restarting, 1, 0) != 0)
            {
                return;
            }

            this.autoRestartRequested = false;
            try
            {
                this.deployment.RestartAndApply();
            }
            catch (Exception exception)
            {
                Volatile.Write(ref this.restarting, 0);
                this.Log.LogError(exception.Message);
                this.notification = this.text.F("notification.restart_failed", "Restart failed: {0}", exception.Message);
            }
        }

        public void Dispose()
        {
            lock (this.lifetimeLock)
            {
                if (Interlocked.Exchange(ref this.disposed, 1) != 0)
                {
                    return;
                }

                this.cancellation.Cancel();
                this.updater.Dispose();
                this.httpClient.Dispose();
            }
        }

        private static string ShortCommit(string commit) => commit.Length > 7 ? commit[..7] : commit;

        private PluginInfo FindPlugin(string name) => this.updater.GetPluginInfo().FirstOrDefault(plugin =>
            plugin.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"The source repository {name} could not be found.");

        private async Task CompileAsync(PluginInfo plugin)
        {
            this.cancellation.Token.ThrowIfCancellationRequested();
            this.deployment.CancelPending(plugin.Name);
            try
            {
                if (!this.IsCatalogLoaded)
                {
                    try
                    {
                        await this.ReadCatalogAsync().ConfigureAwait(false);
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        this.Log.LogWarning(exception.Message);
                    }
                }

                var projectPath = this.AvailablePlugins.SelectMany(description => description.Forks)
                    .FirstOrDefault(fork => IsSameRepository(plugin.RemoteUrl, fork))?.ProjectPath;
                var build = await this.compiler.BuildAsync(plugin, this.cancellation.Token, projectPath).ConfigureAwait(false);
                lock (this.lifetimeLock)
                {
                    this.cancellation.Token.ThrowIfCancellationRequested();
                    this.deployment.Stage(build);
                }
                this.buildErrors.TryRemove(plugin.Name, out _);
                this.Log.LogSuccess(this.text.F("log.compiled", "Compiled {0}. Ready to apply after restart.", build.PluginName));
            }
            catch (Exception exception)
            {
                this.buildErrors[plugin.Name] = exception.Message;
                throw;
            }
        }

        public bool NeedsUpdate(PluginInfo plugin)
        {
            var pending = this.deployment.GetPendingChanges().FirstOrDefault(change =>
                change.RepositoryName.Equals(plugin.Name, StringComparison.OrdinalIgnoreCase));
            if (pending?.Delete == true)
            {
                return false;
            }

            if (plugin.CurrentCommit != plugin.LatestCommit || this.buildErrors.ContainsKey(plugin.Name))
            {
                return true;
            }

            if (pending != null)
            {
                return !pending.Commit.Equals(plugin.FullCommit, StringComparison.OrdinalIgnoreCase);
            }

            return !this.deployment.GetInstalledPlugins().TryGetValue(plugin.Name, out var installed) ||
                !installed.Commit.Equals(plugin.FullCommit, StringComparison.OrdinalIgnoreCase);
        }

        private static bool? GetEnabledState(string pluginName)
        {
            var manager = typeof(Core).Assembly.GetType("GameHelper.Plugin.PManager");
            var plugins = manager?.GetField("Plugins", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)
                ?.GetValue(null) as System.Collections.IEnumerable;
            if (plugins == null)
            {
                return null;
            }

            lock (plugins)
            {
                foreach (var container in plugins)
                {
                    var type = container.GetType();
                    if (!string.Equals(type.GetProperty("Name")?.GetValue(container) as string, pluginName, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var metadata = type.GetProperty("Metadata")?.GetValue(container);
                    return metadata?.GetType().GetProperty("Enable")?.GetValue(metadata) as bool?;
                }
            }

            return null;
        }

        private static bool IsSameRepository(string remoteUrl, Fork fork)
        {
            if (!Uri.TryCreate(remoteUrl, UriKind.Absolute, out var uri) ||
                !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var path = uri.AbsolutePath.Trim('/');
            if (path.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            {
                path = path[..^4];
            }

            return path.Equals($"{fork.Location}/{fork.Name}", StringComparison.OrdinalIgnoreCase);
        }

        private void ReadApplyResult()
        {
            var path = Path.Combine(this.managerDirectory, "config", "apply-result.json");
            if (!File.Exists(path))
            {
                return;
            }

            try
            {
                var result = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(path));
                if (result.Value<bool?>("Success") == false)
                {
                    this.notification = result.Value<string>("Error") ?? "The previous installation failed.";
                    this.Log.LogError(this.notification);
                }
            }
            catch (Exception exception) when (exception is IOException or JsonException)
            {
                this.Log.LogWarning(exception.Message);
            }
        }

        private async Task RunAsync(string status, Func<Task> operation, bool restart = false)
        {
            if (Volatile.Read(ref this.disposed) != 0 || Volatile.Read(ref this.restarting) != 0 ||
                Interlocked.CompareExchange(ref this.busy, 1, 0) != 0)
            {
                return;
            }

            this.operationStatus = status;
            this.currentProgress = 0;
            this.totalProgress = 0;
            this.Log.LogInfo(status);
            try
            {
                await operation().ConfigureAwait(false);
                this.autoRestartRequested = restart && this.deployment.HasPendingChanges;
            }
            catch (OperationCanceledException)
            {
                this.Log.LogWarning(this.text.T("log.cancelled", "The operation was cancelled."));
            }
            catch (Exception exception)
            {
                this.Log.LogError(exception.Message);
                this.notification = exception.Message;
            }
            finally
            {
                this.operationStatus = string.Empty;
                Volatile.Write(ref this.busy, 0);
            }
        }
    }
}
