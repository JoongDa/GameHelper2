// <copyright file="PluginUpdaterSettings.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace PluginUpdater
{
    using GameHelper.Plugin;

    /// <summary>Settings for the source-based plugin manager.</summary>
    public sealed class PluginUpdaterSettings : IPSettings
    {
        public bool CheckUpdatesOnStartup = true;
        public bool AutoCheckUpdates;
        public int UpdateCheckIntervalMinutes = 60;
        public bool WrapLogMessages = true;
        public bool AutoRestartAfterChanges = true;
        public string CatalogUrl = string.Empty;
        public DateTime LastUpdateCheck = DateTime.MinValue;
    }
}
