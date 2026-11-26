// <copyright file="PluginUpdaterCore.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace PluginUpdater
{
    using GameHelper.Plugin;
    using ImGuiNET;
    using Newtonsoft.Json;

    /// <summary>Ports the upstream PluginUpdater into an ordinary GameHelper2 plugin.</summary>
    public sealed class PluginUpdaterCore : PCore<PluginUpdaterSettings>
    {
        private PluginController? controller;
        private PluginRenderer? renderer;
        private string initializationError = string.Empty;

        private string SettingsPath => Path.Combine(this.DllDirectory, "config", "settings.json");

        public override void OnEnable(bool isGameOpened)
        {
            string? settingsWarning = null;
            if (File.Exists(this.SettingsPath))
            {
                try
                {
                    this.Settings = JsonConvert.DeserializeObject<PluginUpdaterSettings>(File.ReadAllText(this.SettingsPath))
                        ?? new PluginUpdaterSettings();
                }
                catch (Exception exception) when (exception is JsonException or IOException)
                {
                    settingsWarning = $"Cannot read PluginUpdater settings: {exception.Message}";
                    this.Settings = new PluginUpdaterSettings();
                    try
                    {
                        File.Copy(this.SettingsPath, this.SettingsPath + $".corrupt-{DateTime.UtcNow:yyyyMMddHHmmssfff}");
                    }
                    catch (IOException) { }
                }
            }

            this.Settings.CatalogUrl ??= string.Empty;
            try
            {
                this.controller = new PluginController(this.Settings, Path.GetFullPath(this.DllDirectory));
                this.renderer = new PluginRenderer(this.controller, this.Settings, this.PluginText);
                this.initializationError = string.Empty;
                if (settingsWarning != null)
                {
                    this.controller.Log.LogWarning(settingsWarning);
                }
            }
            catch (Exception exception)
            {
                this.controller?.Dispose();
                this.controller = null;
                this.renderer = null;
                this.initializationError = exception.Message;
                Console.WriteLine($"[PluginUpdater] {exception}");
            }
        }

        public override void OnDisable()
        {
            this.controller?.Dispose();
            this.controller = null;
            this.renderer = null;
        }

        public override void DrawSettings()
        {
            if (this.renderer != null)
            {
                this.renderer.Render();
                return;
            }

            ImGui.TextUnformatted(this.initializationError);
            if (ImGui.Button(this.PluginText.Label("updater.retry_initialization", "Retry initialization", "RetryInitialization")))
            {
                this.OnEnable(false);
            }
        }

        public override void DrawUI()
        {
            this.controller?.Tick();
            this.renderer?.RenderNotification();
        }

        public override void SaveSettings()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(this.SettingsPath)!);
            var temporaryPath = this.SettingsPath + ".tmp";
            File.WriteAllText(temporaryPath, JsonConvert.SerializeObject(this.Settings, Formatting.Indented));
            File.Move(temporaryPath, this.SettingsPath, overwrite: true);
        }
    }
}
