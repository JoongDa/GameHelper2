// <copyright file="PluginRenderer.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace PluginUpdater
{
    using System;
    using System.Diagnostics;
    using System.Globalization;
    using System.Linq;
    using System.Numerics;
    using GameHelper;
    using GameHelper.Localization;
    using ImGuiNET;

    /// <summary>
    ///     Renders the source-plugin browser and Git management controls.
    ///     Operations run through the controller and only snapshots are read by ImGui.
    /// </summary>
    internal sealed class PluginRenderer
    {
        private readonly PluginController controller;
        private readonly PluginUpdaterSettings settings;
        private readonly PluginLocalization text;
        private string repositoryUrl = string.Empty;
        private string pluginFilter = string.Empty;
        private string browserFilter = string.Empty;
        private string? confirmationPlugin;
        private string? confirmationOperation;
        private string uiError = string.Empty;

        public PluginRenderer(PluginController controller, PluginUpdaterSettings settings, PluginLocalization text)
        {
            this.controller = controller;
            this.settings = settings;
            this.text = text;
        }

        public void Render()
        {
            this.RenderStatus();

            if (ImGui.BeginTabBar("PluginUpdaterTabs"))
            {
                if (ImGui.BeginTabItem(this.text.Title("updater.tabs.manage", "Manage", "Manage")))
                {
                    this.RenderManage();
                    ImGui.EndTabItem();
                }

                if (ImGui.BeginTabItem(this.text.Title("updater.tabs.add", "Add", "Add")))
                {
                    this.RenderAdd();
                    ImGui.EndTabItem();
                }

                if (ImGui.BeginTabItem(this.text.Title("updater.tabs.browse", "Browse", "Browse")))
                {
                    this.RenderBrowser();
                    ImGui.EndTabItem();
                }

                if (ImGui.BeginTabItem(this.text.Title("updater.tabs.settings", "Settings", "Settings")))
                {
                    this.RenderSettings();
                    ImGui.EndTabItem();
                }

                ImGui.EndTabBar();
            }

            this.RenderConfirmation();
            this.RenderConsole();
        }

        public void RenderNotification()
        {
            var message = this.controller.Notification;
            if (Core.IsSettingsMenuOpen || string.IsNullOrEmpty(message))
            {
                return;
            }

            var viewport = ImGui.GetMainViewport();
            ImGui.SetNextWindowPos(viewport.WorkPos + new Vector2(viewport.WorkSize.X - 16f, 16f),
                ImGuiCond.Always, new Vector2(1f, 0f));
            ImGui.SetNextWindowSizeConstraints(new Vector2(220f, 0f), new Vector2(400f, float.MaxValue));
            var visible = true;
            if (ImGui.Begin(this.text.Title("updater.notification.title", "PluginUpdater", "PluginUpdaterNotification"),
                ref visible, ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoSavedSettings |
                ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoMove))
            {
                ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + 350f);
                ImGui.TextUnformatted(message);
                ImGui.PopTextWrapPos();
                if (ImGui.SmallButton(this.text.Label("updater.dismiss", "Dismiss", "DismissGameNotification")))
                {
                    visible = false;
                }
            }

            ImGui.End();
            if (!visible)
            {
                this.controller.ClearNotification();
            }
        }

        private void RenderStatus()
        {
            if (!string.IsNullOrEmpty(this.controller.Notification))
            {
                ColoredText(new Vector4(1f, 0.8f, 0.2f, 1f), this.controller.Notification);
                ImGui.SameLine();
                if (ImGui.SmallButton(this.text.Label("updater.dismiss", "Dismiss", "DismissNotification")))
                {
                    this.controller.ClearNotification();
                }
            }

            if (this.controller.IsBusy)
            {
                WrappedText(this.controller.OperationStatus);
                if (this.controller.TotalProgress > 0)
                {
                    var progress = (float)this.controller.CurrentProgress / this.controller.TotalProgress;
                    ImGui.ProgressBar(Math.Clamp(progress, 0f, 1f), new Vector2(-1f, 0f),
                        this.text.F("updater.progress", "{0} / {1}",
                            this.controller.CurrentProgress, this.controller.TotalProgress));
                }
            }

            if (this.controller.PendingCount > 0)
            {
                WrappedText(this.text.F("updater.pending", "{0} plugin changes are ready to apply after restart.",
                    this.controller.PendingCount));
                ImGui.BeginDisabled(this.controller.IsBusy);
                if (ImGui.Button(this.text.Label("updater.restart", "Restart and apply", "RestartAndApply")))
                {
                    this.controller.RestartAndApply();
                }

                ImGui.EndDisabled();
            }

            if (!string.IsNullOrEmpty(this.uiError))
            {
                ColoredText(new Vector4(1f, 0.3f, 0.3f, 1f), this.uiError);
            }
        }

        private void RenderManage()
        {
            var plugins = this.controller.GetPluginInfo();
            ImGui.BeginDisabled(this.controller.IsBusy);
            if (ImGui.Button(this.text.Label("updater.check_updates", "Check for updates", "CheckUpdates")))
            {
                _ = this.controller.CheckForUpdatesAsync();
            }

            ImGui.SameLine();
            if (ImGui.Button(this.text.Label("updater.refresh_local", "Refresh local", "RefreshLocal")))
            {
                _ = this.controller.RefreshLocalAsync();
            }

            if (plugins.Any(this.controller.NeedsUpdate))
            {
                ImGui.SameLine();
                if (ImGui.Button(this.text.Label("updater.update_all", "Update all", "UpdateAll")))
                {
                    _ = this.controller.UpdateAllPluginsAsync();
                }
            }

            ImGui.EndDisabled();
            ImGui.SetNextItemWidth(Math.Max(140f, ImGui.GetContentRegionAvail().X));
            ImGui.InputTextWithHint("##ManagedPluginFilter", this.text.T("updater.filter", "Filter"), ref this.pluginFilter, 200);

            var visible = plugins.Where(plugin => plugin.Name.Contains(this.pluginFilter, StringComparison.OrdinalIgnoreCase))
                .OrderBy(plugin => plugin.Name, StringComparer.OrdinalIgnoreCase).ToArray();
            if (visible.Length == 0)
            {
                ImGui.TextDisabled(this.text.T("updater.no_managed_plugins", "No matching source plugins. Use Browse or Add to install one."));
            }
            else if (ImGui.BeginTable("ManagedPlugins", 5, TableFlags(), TableSize(visible.Length)))
            {
                ImGui.TableSetupColumn(this.text.T("updater.column.plugin", "Plugin"), ImGuiTableColumnFlags.WidthFixed, 140f);
                ImGui.TableSetupColumn(this.text.T("updater.column.branch", "Branch"), ImGuiTableColumnFlags.WidthFixed, 115f);
                ImGui.TableSetupColumn(this.text.T("updater.column.commit", "Commit"), ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableSetupColumn(this.text.T("updater.column.status", "Installation"), ImGuiTableColumnFlags.WidthFixed, 140f);
                ImGui.TableSetupColumn(this.text.T("updater.column.actions", "Actions"), ImGuiTableColumnFlags.WidthFixed, 230f);
                ImGui.TableHeadersRow();

                foreach (var plugin in visible)
                {
                    ImGui.PushID(plugin.Name);
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(plugin.Name);
                    ImGui.TableNextColumn();
                    this.RenderBranchSelector(plugin);
                    ImGui.TableNextColumn();
                    this.RenderCommit(plugin);
                    ImGui.TableNextColumn();
                    WrappedText(this.controller.GetInstallationStatus(plugin.Name));
                    ImGui.TableNextColumn();
                    this.RenderActions(plugin);
                    ImGui.PopID();
                }

                ImGui.EndTable();
            }

            var manualPlugins = this.controller.GetManualPlugins().ToArray();
            if (manualPlugins.Length > 0 && ImGui.TreeNode(this.text.Label("updater.manual_plugins", "Manually installed plugins", "ManualPlugins")))
            {
                WrappedText(this.text.T("updater.manual_plugins_help", "These plugins have no managed Git source repository."));
                foreach (var plugin in manualPlugins.OrderBy(plugin => plugin.Name, StringComparer.OrdinalIgnoreCase))
                {
                    ImGui.PushID(plugin.Name);
                    ImGui.TextUnformatted(plugin.Name);
                    ImGui.SameLine();
                    if (ImGui.SmallButton(this.text.Label("updater.open_folder", "Open folder", "OpenManualFolder")))
                    {
                        this.OpenFolder(plugin.Path);
                    }

                    ImGui.PopID();
                }

                ImGui.TreePop();
            }
        }

        private void RenderBranchSelector(PluginInfo plugin)
        {
            ImGui.BeginDisabled(this.controller.IsBusy);
            ImGui.SetNextItemWidth(-1f);
            if (ImGui.BeginCombo("##Branch", plugin.CurrentBranch))
            {
                foreach (var branch in plugin.AvailableBranches)
                {
                    var selected = branch == plugin.CurrentBranch;
                    if (ImGui.Selectable(branch, selected) && !selected)
                    {
                        _ = this.controller.ChangeBranchAsync(plugin.Name, branch);
                    }

                    if (selected)
                    {
                        ImGui.SetItemDefaultFocus();
                    }
                }

                ImGui.EndCombo();
            }

            ImGui.EndDisabled();
        }

        private void RenderCommit(PluginInfo plugin)
        {
            if (string.IsNullOrEmpty(plugin.CurrentCommit))
            {
                ImGui.TextDisabled(this.text.T("updater.no_commit", "No commit information"));
            }
            else if (plugin.CurrentCommit == plugin.LatestCommit)
            {
                WrappedText(this.text.F("updater.current_commit", "On latest commit {0}", plugin.CurrentCommit));
            }
            else
            {
                WrappedText(this.text.F("updater.commit_update", "{0} → {1}", plugin.CurrentCommit, plugin.LatestCommit));
                WrappedText(this.text.F("updater.commit_distance", "{0} behind, {1} ahead", plugin.BehindBy, plugin.AheadBy));
                if (!string.IsNullOrEmpty(plugin.LatestCommitMessage))
                {
                    WrappedText(plugin.LatestCommitMessage);
                }
            }

            if (plugin.UncommittedChangeCount > 0)
            {
                ColoredText(new Vector4(1f, 0.8f, 0.2f, 1f),
                    this.text.F("updater.uncommitted", "{0} uncommitted changes", plugin.UncommittedChangeCount));
            }

            if (!string.IsNullOrEmpty(plugin.LastMessage) && ImGui.IsItemHovered())
            {
                Tooltip(plugin.LastMessage);
            }
        }

        private void RenderActions(PluginInfo plugin)
        {
            ImGui.BeginDisabled(this.controller.IsBusy);
            ImGui.BeginDisabled(plugin.CurrentCommit == plugin.LatestCommit);
            if (ImGui.SmallButton(this.text.Label("updater.update", "Update", "UpdatePlugin")))
            {
                _ = this.controller.UpdatePluginAsync(plugin.Name);
            }

            ImGui.EndDisabled();
            ImGui.SameLine();
            if (ImGui.SmallButton(this.text.Label("updater.force_update", "Force update", "ForceUpdate")))
            {
                this.RequestConfirmation("force", plugin.Name);
            }

            ImGui.SameLine();
            ImGui.BeginDisabled(string.IsNullOrEmpty(plugin.PreviousCommit));
            if (ImGui.SmallButton(this.text.Label("updater.revert", "Revert", "RevertPlugin")))
            {
                this.RequestConfirmation("revert", plugin.Name);
            }

            ImGui.EndDisabled();
            if (ImGui.IsItemHovered() && !string.IsNullOrEmpty(plugin.PreviousCommit))
            {
                Tooltip(this.text.F("updater.revert_commit", "Revert to {0}", plugin.PreviousCommit));
            }

            if (ImGui.SmallButton(this.text.Label("updater.build", "Build", "BuildPlugin")))
            {
                _ = this.controller.BuildPluginAsync(plugin.Name);
            }

            ImGui.SameLine();
            if (ImGui.SmallButton(this.text.Label("updater.delete", "Delete", "DeletePlugin")))
            {
                this.RequestConfirmation("delete", plugin.Name);
            }

            ImGui.EndDisabled();
            ImGui.SameLine();
            if (ImGui.SmallButton(this.text.Label("updater.open_folder", "Open folder", "OpenFolder")))
            {
                this.OpenFolder(plugin.Path);
            }
        }

        private void RenderAdd()
        {
            ImGui.TextUnformatted(this.text.T("updater.repository_url", "Repository URL"));
            ImGui.SetNextItemWidth(-1f);
            ImGui.InputText("##RepositoryUrl", ref this.repositoryUrl, 2048);
            ImGui.BeginDisabled(this.controller.IsBusy || string.IsNullOrWhiteSpace(this.repositoryUrl));
            if (ImGui.Button(this.text.Label("updater.clone", "Clone and build", "CloneRepository")))
            {
                _ = this.controller.CloneRepositoryAsync(this.repositoryUrl.Trim());
            }

            ImGui.EndDisabled();
            WrappedText(this.text.T("updater.add_help", "Enter a GitHub repository URL or a branch URL ending in /tree/branch. The source is cloned and compiled for GameHelper2."));
            ImGui.Spacing();
            ImGui.TextUnformatted(this.text.T("updater.source_directory", "Source directory"));
            WrappedText(this.controller.SourceDirectory);
        }

        private void RenderBrowser()
        {
            if (!this.controller.IsCatalogLoaded && string.IsNullOrEmpty(this.controller.CatalogError) && !this.controller.IsBusy)
            {
                _ = this.controller.LoadCatalogAsync();
            }

            ImGui.BeginDisabled(this.controller.IsBusy);
            if (ImGui.Button(this.text.Label("updater.refresh_catalog", "Refresh directory", "RefreshCatalog")))
            {
                _ = this.controller.LoadCatalogAsync(true);
            }

            ImGui.EndDisabled();
            ImGui.SameLine();
            ImGui.SetNextItemWidth(Math.Max(140f, ImGui.GetContentRegionAvail().X));
            ImGui.InputTextWithHint("##BrowserFilter", this.text.T("updater.filter", "Filter"), ref this.browserFilter, 200);

            if (!string.IsNullOrEmpty(this.controller.CatalogError))
            {
                ColoredText(new Vector4(1f, 0.3f, 0.3f, 1f), this.controller.CatalogError);
            }

            var plugins = this.controller.AvailablePlugins.Where(plugin =>
                plugin.Name.Contains(this.browserFilter, StringComparison.OrdinalIgnoreCase) ||
                (plugin.Description ?? string.Empty).Contains(this.browserFilter, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (plugins.Length == 0)
            {
                ImGui.TextDisabled(this.text.T("updater.no_catalog_plugins", "No matching plugins in the directory. Configure a directory URL in Settings or use Add."));
                return;
            }

            if (!ImGui.BeginTable("AvailablePlugins", 6, TableFlags() | ImGuiTableFlags.Hideable, TableSize(plugins.Length)))
            {
                return;
            }

            ImGui.TableSetupColumn(this.text.T("updater.column.plugin", "Plugin"), ImGuiTableColumnFlags.WidthFixed, 120f);
            ImGui.TableSetupColumn(this.text.T("updater.column.description", "Description"), ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn(this.text.T("updater.column.author", "Original author"), ImGuiTableColumnFlags.WidthFixed, 100f);
            ImGui.TableSetupColumn(this.text.T("updater.column.fork", "Endorsed fork"), ImGuiTableColumnFlags.WidthFixed, 100f);
            ImGui.TableSetupColumn(this.text.T("updater.column.updated", "Last updated"), ImGuiTableColumnFlags.WidthFixed, 90f);
            ImGui.TableSetupColumn(this.text.T("updater.column.actions", "Actions"), ImGuiTableColumnFlags.WidthFixed, 130f);
            ImGui.TableHeadersRow();
            var installed = this.controller.GetPluginInfo();

            foreach (var plugin in plugins)
            {
                var fork = string.IsNullOrWhiteSpace(plugin.EndorsedAuthor)
                    ? plugin.Forks.FirstOrDefault()
                    : plugin.Forks.FirstOrDefault(candidate => string.Equals(candidate.Author, plugin.EndorsedAuthor, StringComparison.OrdinalIgnoreCase));
                ImGui.PushID(plugin.Name);
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(plugin.Name);
                ImGui.TableNextColumn();
                WrappedText(plugin.Description ?? string.Empty);
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(plugin.OriginalAuthor ?? "-");
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(fork?.Author ?? "-");
                ImGui.TableNextColumn();
                var date = fork?.LatestCommit?.Date;
                ImGui.TextUnformatted(DateTimeOffset.TryParse(date, CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var updated) ? updated.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : "-");
                ImGui.TableNextColumn();

                if (fork == null)
                {
                    ImGui.TextDisabled(this.text.T("updater.no_fork", "No recommended repository"));
                }
                else
                {
                    var isInstalled = installed.Any(existing => string.Equals(existing.Name, fork.Name, StringComparison.OrdinalIgnoreCase));
                    ImGui.BeginDisabled(this.controller.IsBusy);
                    if (isInstalled)
                    {
                        WrappedText(this.controller.GetInstallationStatus(fork.Name));
                        if (ImGui.SmallButton(this.text.Label("updater.delete", "Delete", "DeleteFromBrowser")))
                        {
                            this.RequestConfirmation("delete", fork.Name);
                        }
                    }
                    else if (ImGui.Button(this.text.Label("updater.download", "Download", "DownloadPlugin")))
                    {
                        var url = $"https://github.com/{fork.Location}/{fork.Name}";
                        url += string.IsNullOrWhiteSpace(fork.Branch)
                            ? ".git"
                            : $"/tree/{Uri.EscapeDataString(fork.Branch).Replace("%2F", "/", StringComparison.OrdinalIgnoreCase)}";
                        _ = this.controller.CloneRepositoryAsync(url);
                    }

                    ImGui.EndDisabled();
                    if (ImGui.IsItemHovered() && !string.IsNullOrEmpty(fork.LatestCommit?.Message))
                    {
                        Tooltip(fork.LatestCommit.Message);
                    }
                }

                ImGui.PopID();
            }

            ImGui.EndTable();
        }

        private void RenderSettings()
        {
            ImGui.Checkbox(this.text.Label("updater.settings.check_startup", "Check for updates on startup", "CheckOnStartup"), ref this.settings.CheckUpdatesOnStartup);
            ImGui.Checkbox(this.text.Label("updater.settings.check_periodically", "Check for updates periodically", "PeriodicChecks"), ref this.settings.AutoCheckUpdates);
            ImGui.BeginDisabled(!this.settings.AutoCheckUpdates);
            var intervals = new[] { 15, 30, 60, 120, 180, 360, 720, 1440 };
            var preview = this.text.F("updater.settings.interval_minutes", "{0} minutes", this.settings.UpdateCheckIntervalMinutes);
            ImGui.SetNextItemWidth(160f);
            if (ImGui.BeginCombo(this.text.Label("updater.settings.interval", "Check every", "CheckInterval"), preview))
            {
                foreach (var interval in intervals)
                {
                    var selected = interval == this.settings.UpdateCheckIntervalMinutes;
                    if (ImGui.Selectable(this.text.F("updater.settings.interval_minutes", "{0} minutes", interval), selected))
                    {
                        this.settings.UpdateCheckIntervalMinutes = interval;
                    }

                    if (selected)
                    {
                        ImGui.SetItemDefaultFocus();
                    }
                }

                ImGui.EndCombo();
            }

            ImGui.EndDisabled();
            ImGui.Checkbox(this.text.Label("updater.settings.wrap_log", "Wrap log messages", "WrapLogs"), ref this.settings.WrapLogMessages);
            ImGui.Checkbox(this.text.Label("updater.settings.auto_restart", "Restart automatically after successful changes", "AutoRestart"), ref this.settings.AutoRestartAfterChanges);
            ImGui.Separator();
            ImGui.TextUnformatted(this.text.T("updater.settings.catalog_url", "Plugin directory JSON URL"));
            ImGui.SetNextItemWidth(-1f);
            ImGui.BeginDisabled(this.controller.IsBusy);
            ImGui.InputText("##CatalogUrl", ref this.settings.CatalogUrl, 2048);
            if (ImGui.Button(this.text.Label("updater.settings.load_catalog", "Load directory", "LoadConfiguredCatalog")))
            {
                _ = this.controller.LoadCatalogAsync(true);
            }

            ImGui.EndDisabled();
            WrappedText(this.text.T("updater.settings.catalog_help", "Leave blank to use the bundled GameHelper2 directory. Add accepts individual repository URLs."));
        }

        private void RequestConfirmation(string operation, string pluginName)
        {
            this.confirmationOperation = operation;
            this.confirmationPlugin = pluginName;
        }

        private void RenderConfirmation()
        {
            var title = this.text.Title("updater.confirm.title", "Confirm plugin change", "ConfirmPluginChange");
            if (this.confirmationPlugin != null && !ImGui.IsPopupOpen(title))
            {
                ImGui.OpenPopup(title);
            }

            if (!ImGui.BeginPopupModal(title, ImGuiWindowFlags.AlwaysAutoResize))
            {
                return;
            }

            var pluginName = this.confirmationPlugin ?? string.Empty;
            switch (this.confirmationOperation)
            {
                case "force":
                    ImGui.TextUnformatted(this.text.F("updater.confirm.force", "Force update {0}? Local source changes will be discarded.", pluginName));
                    break;
                case "revert":
                    ImGui.TextUnformatted(this.text.F("updater.confirm.revert", "Revert {0} to its previous commit? Local source changes will be discarded.", pluginName));
                    break;
                case "delete":
                    ImGui.TextUnformatted(this.text.F("updater.confirm.delete", "Delete {0} and its source repository?", pluginName));
                    break;
            }

            ImGui.Spacing();
            ImGui.BeginDisabled(this.controller.IsBusy);
            if (ImGui.Button(this.text.Label("updater.confirm.apply", "Confirm", "ConfirmAction"), new Vector2(120f, 0f)))
            {
                switch (this.confirmationOperation)
                {
                    case "force":
                        _ = this.controller.UpdatePluginAsync(pluginName, true);
                        break;
                    case "revert":
                        _ = this.controller.RevertPluginAsync(pluginName);
                        break;
                    case "delete":
                        _ = this.controller.DeletePluginAsync(pluginName);
                        break;
                }

                this.confirmationPlugin = null;
                this.confirmationOperation = null;
                ImGui.CloseCurrentPopup();
            }

            ImGui.EndDisabled();
            ImGui.SameLine();
            if (ImGui.Button(this.text.Label("updater.cancel", "Cancel", "CancelAction"), new Vector2(120f, 0f)))
            {
                this.confirmationPlugin = null;
                this.confirmationOperation = null;
                ImGui.CloseCurrentPopup();
            }

            ImGui.EndPopup();
        }

        private void RenderConsole()
        {
            ImGui.Spacing();
            ImGui.Separator();
            ImGui.TextUnformatted(this.text.T("updater.console", "Console log"));
            ImGui.SameLine();
            if (ImGui.SmallButton(this.text.Label("updater.clear_log", "Clear", "ClearLog")))
            {
                this.controller.Log.Clear();
            }

            var flags = this.settings.WrapLogMessages ? ImGuiWindowFlags.None : ImGuiWindowFlags.HorizontalScrollbar;
            if (ImGui.BeginChild("PluginUpdaterConsole", new Vector2(-1f, Math.Max(100f, ImGui.GetContentRegionAvail().Y)), ImGuiChildFlags.Borders, flags))
            {
                var follow = ImGui.GetScrollY() >= ImGui.GetScrollMaxY();
                foreach (var entry in this.controller.Log.GetEntries())
                {
                    var color = entry.Level switch
                    {
                        LogLevel.Success => new Vector4(0.3f, 1f, 0.4f, 1f),
                        LogLevel.Warning => new Vector4(1f, 0.8f, 0.2f, 1f),
                        LogLevel.Error => new Vector4(1f, 0.3f, 0.3f, 1f),
                        _ => new Vector4(0.9f, 0.9f, 0.9f, 1f),
                    };
                    ImGui.PushStyleColor(ImGuiCol.Text, color);
                    if (this.settings.WrapLogMessages)
                    {
                        ImGui.PushTextWrapPos(0f);
                    }

                    ImGui.TextUnformatted($"[{entry.Timestamp:HH:mm:ss}] {entry.Message}");
                    if (this.settings.WrapLogMessages)
                    {
                        ImGui.PopTextWrapPos();
                    }

                    ImGui.PopStyleColor();
                }

                if (follow)
                {
                    ImGui.SetScrollHereY(1f);
                }
            }

            ImGui.EndChild();
        }

        private void OpenFolder(string path)
        {
            try
            {
                var startInfo = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
                startInfo.ArgumentList.Add(path);
                using var process = Process.Start(startInfo);
                this.uiError = string.Empty;
            }
            catch (Exception ex)
            {
                this.uiError = this.text.F("updater.open_folder_failed", "Cannot open folder. {0}", ex.Message);
            }
        }

        // Git messages and catalog descriptions can contain printf format characters.
        private static void WrappedText(string value)
        {
            ImGui.PushTextWrapPos(0f);
            ImGui.TextUnformatted(value);
            ImGui.PopTextWrapPos();
        }

        private static void ColoredText(Vector4 color, string value)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, color);
            ImGui.TextUnformatted(value);
            ImGui.PopStyleColor();
        }

        private static void Tooltip(string value)
        {
            ImGui.BeginTooltip();
            ImGui.TextUnformatted(value);
            ImGui.EndTooltip();
        }

        private static ImGuiTableFlags TableFlags() => ImGuiTableFlags.Borders | ImGuiTableFlags.Resizable |
            ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.ScrollX | ImGuiTableFlags.SizingFixedFit;

        private static Vector2 TableSize(int rowCount)
        {
            var availableHeight = Math.Max(140f, ImGui.GetContentRegionAvail().Y * 0.60f);
            var rowHeight = ImGui.GetTextLineHeightWithSpacing() * 3f;
            return new Vector2(-1f, Math.Min(Math.Max(rowCount + 1, 3) * rowHeight, availableHeight));
        }
    }
}
