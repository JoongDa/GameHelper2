// <copyright file="StashUtilityCore.SettingsPersistence.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace StashUtility
{
    using System;
    using System.IO;
    using Newtonsoft.Json;

    public sealed partial class StashUtilityCore
    {
        private const int SettingsSaveDebounceMs = 500;
        private const int SettingsSaveMaximumDelayMs = 2000;
        private bool settingsSavePending;
        private long settingsFirstChangeAt;
        private long settingsLastChangeAt;
        private long settingsRetryAt;

        private void RequestSettingsSave()
        {
            long now = Environment.TickCount64;
            if (!settingsSavePending) settingsFirstChangeAt = now;
            settingsLastChangeAt = now;
            settingsSavePending = true;
        }

        private void FlushPendingSettings()
        {
            if (!settingsSavePending) return;
            long now = Environment.TickCount64;
            if (now < settingsRetryAt) return;
            if (now - settingsLastChangeAt >= SettingsSaveDebounceMs ||
                now - settingsFirstChangeAt >= SettingsSaveMaximumDelayMs)
                PersistSettings();
        }

        private void PersistSettings()
        {
            try
            {
                var path = SettingPathname;
                var directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

                // Replace only after the complete JSON has been written successfully.
                var temporaryPath = path + ".tmp";
                File.WriteAllText(temporaryPath, JsonConvert.SerializeObject(Settings, Formatting.Indented));
                File.Move(temporaryPath, path, true);
                if (equipmentDiagnosticsEnabled) equipmentPerformance.SettingsWrites++;
                settingsSavePending = false;
                settingsRetryAt = 0;
            }
            catch
            {
                // Keep edits in memory and retry on the overlay thread after a short backoff.
                if (!settingsSavePending) RequestSettingsSave();
                settingsRetryAt = Environment.TickCount64 + SettingsSaveDebounceMs;
            }
        }
    }
}
