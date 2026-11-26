// <copyright file="PluginDeployment.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace PluginUpdater
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Linq;
    using System.Reflection;
    using System.Threading;
    using Coroutine;
    using GameHelper;
    using GameHelper.CoroutineEvents;
    using Newtonsoft.Json;

    public sealed record InstalledPlugin(string PluginName, string Commit);

    public sealed record PendingPluginChange(
        string RepositoryName, string PluginName, string Commit, string OutputDirectory, bool Delete);

    /// <summary>Stages plugin changes and applies them after the current host has exited.</summary>
    public sealed class PluginDeployment
    {
        private readonly string hostDirectory;
        private readonly string managerDirectory;
        private readonly string stateDirectory;
        private readonly Action<string> log;
        private readonly object stateLock = new();

        public PluginDeployment(string hostDirectory, string managerDirectory, Action<string> log)
        {
            this.hostDirectory = Path.GetFullPath(hostDirectory);
            this.managerDirectory = Path.GetFullPath(managerDirectory);
            this.stateDirectory = Path.Combine(this.managerDirectory, "config");
            this.log = log;
            Directory.CreateDirectory(this.stateDirectory);
        }

        public bool HasPendingChanges => this.GetPendingChanges().Count != 0;

        public IReadOnlyList<PendingPluginChange> GetPendingChanges()
        {
            lock (this.stateLock)
            {
                return this.ReadState<List<PendingPluginChange>>("pending.json");
            }
        }

        public IReadOnlyDictionary<string, InstalledPlugin> GetInstalledPlugins()
        {
            lock (this.stateLock)
            {
                return new Dictionary<string, InstalledPlugin>(
                    this.ReadState<Dictionary<string, InstalledPlugin>>("installed.json"), StringComparer.OrdinalIgnoreCase);
            }
        }

        public string? InstalledPluginName(string repositoryName)
        {
            return this.GetInstalledPlugins().GetValueOrDefault(repositoryName)?.PluginName;
        }

        public void Stage(PluginBuildResult build)
        {
            ValidateName(build.RepositoryName);
            PluginCompiler.ValidatePluginName(build.PluginName);
            EnsureChild(build.OutputDirectory, Path.Combine(this.managerDirectory, "Staging"));
            if (!File.Exists(Path.Combine(build.OutputDirectory, build.PluginName + ".dll")))
            {
                throw new InvalidDataException("The staged plugin DLL is missing.");
            }

            lock (this.stateLock)
            {
                using var operationLock = this.AcquireOperationLock();
                var installed = this.GetInstalledPlugins();
                if (installed.Any(pair => !pair.Key.Equals(build.RepositoryName, StringComparison.OrdinalIgnoreCase) &&
                    pair.Value.PluginName.Equals(build.PluginName, StringComparison.OrdinalIgnoreCase)))
                {
                    throw new InvalidOperationException("Another repository already owns this plugin name.");
                }

                if (installed.TryGetValue(build.RepositoryName, out var oldPlugin) &&
                    !oldPlugin.PluginName.Equals(build.PluginName, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("Changing an installed plugin's assembly name requires removing it first.");
                }

                var pending = this.ReadState<List<PendingPluginChange>>("pending.json");
                if (pending.Any(change => !change.RepositoryName.Equals(build.RepositoryName, StringComparison.OrdinalIgnoreCase) &&
                    change.PluginName.Equals(build.PluginName, StringComparison.OrdinalIgnoreCase)))
                {
                    throw new InvalidOperationException("Another pending installation already uses this plugin name.");
                }

                pending.RemoveAll(change => change.RepositoryName.Equals(build.RepositoryName, StringComparison.OrdinalIgnoreCase));
                pending.Add(new PendingPluginChange(build.RepositoryName, build.PluginName, build.Commit,
                    Path.GetFullPath(build.OutputDirectory), false));
                this.WriteState("pending.json", pending);
            }

            this.log($"{build.PluginName} is compiled and ready to apply on restart.");
        }

        public void StageDelete(string repositoryName)
        {
            ValidateName(repositoryName);
            lock (this.stateLock)
            {
                using var operationLock = this.AcquireOperationLock();
                var pending = this.ReadState<List<PendingPluginChange>>("pending.json");
                // Source-only clones have no deployed directory to remove.
                var pluginName = this.InstalledPluginName(repositoryName) ?? string.Empty;
                if (pluginName.Length != 0)
                {
                    PluginCompiler.ValidatePluginName(pluginName);
                }
                pending.RemoveAll(change => change.RepositoryName.Equals(repositoryName, StringComparison.OrdinalIgnoreCase));
                pending.Add(new PendingPluginChange(repositoryName, pluginName, string.Empty, string.Empty, true));
                this.WriteState("pending.json", pending);
            }
        }

        public void CancelPending(string repositoryName)
        {
            ValidateName(repositoryName);
            lock (this.stateLock)
            {
                using var operationLock = this.AcquireOperationLock();
                var pending = this.ReadState<List<PendingPluginChange>>("pending.json");
                pending.RemoveAll(change => change.RepositoryName.Equals(repositoryName, StringComparison.OrdinalIgnoreCase));
                this.WriteState("pending.json", pending);
            }
        }

        /// <summary>Must be called on the host render thread so plugin settings save synchronously.</summary>
        public void RestartAndApply()
        {
            var changes = this.GetPendingChanges();
            if (changes.Count == 0)
            {
                return;
            }

            var launcher = Path.Combine(this.hostDirectory, "Launcher.exe");
            if (!File.Exists(launcher))
            {
                throw new FileNotFoundException("Launcher.exe is required to restart GameHelper.", launcher);
            }

            var saveEventField = typeof(GameHelperEvents).GetField("TimeToSaveAllSettings", BindingFlags.NonPublic | BindingFlags.Static);
            if (saveEventField?.GetValue(null) is not Event saveEvent)
            {
                throw new InvalidOperationException("The host's save-settings event is unavailable. Restart manually after saving settings.");
            }

            // This is the same save event used by the host updater. Reflection keeps the
            // compatibility bridge inside this ordinary plugin instead of changing core APIs.
            CoroutineHandler.RaiseEvent(saveEvent);
            var requestDirectory = Path.Combine(this.stateDirectory, "requests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(requestDirectory);
            var scriptPath = Path.Combine(requestDirectory, "apply.ps1");
            using (var resource = typeof(PluginDeployment).Assembly.GetManifestResourceStream("PluginUpdater.ApplyPluginChanges.ps1")
                ?? throw new InvalidOperationException("The plugin installation helper is missing."))
            using (var target = File.Create(scriptPath))
            {
                resource.CopyTo(target);
            }

            var requestPath = Path.Combine(requestDirectory, "request.json");
            using var currentProcess = Process.GetCurrentProcess();
            File.WriteAllText(requestPath, JsonConvert.SerializeObject(new
            {
                HostDirectory = this.hostDirectory,
                ManagerDirectory = this.managerDirectory,
                ParentId = Environment.ProcessId,
                ParentStartTimeUtcTicks = currentProcess.StartTime.ToUniversalTime().Ticks.ToString(),
                PendingState = JsonConvert.SerializeObject(changes, Formatting.Indented),
                Changes = changes,
            }, Formatting.Indented));
            var startInfo = new ProcessStartInfo("powershell.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = this.hostDirectory,
            };
            foreach (var argument in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", scriptPath, "-RequestPath", requestPath })
            {
                startInfo.ArgumentList.Add(argument);
            }

            using var helper = Process.Start(startInfo) ?? throw new InvalidOperationException("Cannot start the plugin installation helper.");
            var readyPath = Path.Combine(requestDirectory, "ready");
            var timer = Stopwatch.StartNew();
            while (!File.Exists(readyPath))
            {
                if (helper.HasExited || timer.Elapsed > TimeSpan.FromSeconds(5))
                {
                    throw new InvalidOperationException($"The installation helper did not start. See {Path.Combine(requestDirectory, "apply.log")}");
                }

                Thread.Sleep(25);
            }

            this.log("Settings saved. Restarting GameHelper to apply plugin changes.");
            Core.GHSettings.IsOverlayRunning = false;
        }

        internal static void EnsureChild(string path, string parent)
        {
            var fullParent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent));
            var fullPath = Path.GetFullPath(path);
            if (!fullPath.StartsWith(fullParent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("The plugin operation points outside its working directory.");
            }

            var current = new DirectoryInfo(fullPath);
            while (current != null && current.FullName.Length >= fullParent.Length)
            {
                if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidDataException("Plugin operation paths cannot contain filesystem links.");
                }

                current = current.Parent;
            }
        }

        private static void ValidateName(string name)
        {
            if (string.IsNullOrWhiteSpace(name) || name is "." or ".." ||
                name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.EndsWith('.') || name.EndsWith(' '))
            {
                throw new InvalidDataException("Invalid repository directory name.");
            }
        }

        private FileStream AcquireOperationLock()
        {
            return new FileStream(Path.Combine(this.stateDirectory, "apply.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }

        private T ReadState<T>(string filename)
            where T : new()
        {
            var path = Path.Combine(this.stateDirectory, filename);
            if (!File.Exists(path))
            {
                return new T();
            }

            return JsonConvert.DeserializeObject<T>(File.ReadAllText(path))
                ?? throw new InvalidDataException($"Invalid plugin update state in {filename}.");
        }

        private void WriteState(string filename, object value)
        {
            var path = Path.Combine(this.stateDirectory, filename);
            var temporaryPath = path + ".tmp";
            File.WriteAllText(temporaryPath, JsonConvert.SerializeObject(value, Formatting.Indented));
            File.Move(temporaryPath, path, overwrite: true);
        }
    }
}
