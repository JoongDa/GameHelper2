namespace Autopot
{
    using System;
    using System.Diagnostics;
    using System.IO;
    using System.Numerics;
    using ClickableTransparentOverlay.Win32;
    using GameHelper;
    using GameHelper.Plugin;
    using GameHelper.RemoteEnums;
    using GameHelper.RemoteObjects.Components;
    using GameHelper.Utils;
    using ImGuiNET;
    using Newtonsoft.Json;

    public sealed class AutopotCore : PCore<AutopotSettings>
    {
        private readonly Stopwatch key1Cooldown = Stopwatch.StartNew();
        private readonly Stopwatch key2Cooldown = Stopwatch.StartNew();

        private VitalsSnapshot lastVitals;
        private string serviceStatus = "Stopped";
        private string statusDetail = string.Empty;
        private bool safetyLogoutTriggered;
        private readonly Stopwatch safetyLogoutCooldown = new();
        private bool safetyLogoutWaitingRecovery;

        private string SettingPathname => Path.Join(DllDirectory, "config", "settings.txt");

        public override void OnDisable() { }

        public override void OnEnable(bool isGameOpened)
        {
            if (File.Exists(SettingPathname))
            {
                var content = File.ReadAllText(SettingPathname);
                Settings = JsonConvert.DeserializeObject<AutopotSettings>(content) ?? new AutopotSettings();
            }
        }

        public override void SaveSettings()
        {
            var dir = Path.GetDirectoryName(SettingPathname);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllText(SettingPathname, JsonConvert.SerializeObject(Settings, Formatting.Indented));
        }

        public override void DrawSettings()
        {
            RefreshVitals();
            ImGui.Columns(2, "autopot_settings", false);

            DrawVitalsColumn();
            ImGui.NextColumn();
            DrawConfigurationColumn();

            ImGui.Columns(1);
        }

        public override void DrawUI()
        {
            RefreshVitals();
            UpdateServiceStatus();

            if (Settings.ShowVitalsOverlay && lastVitals.Valid)
                DrawVitalsOverlay();

            if (TrySafetyLogout())
                return;

            if (!Settings.EnableAutoPot)
                return;

            if (!CanExecute(out _))
                return;

            if (Core.GHSettings.EnableControllerMode)
                return;

            if (Core.States.InGameStateObject.GameUi.ChatParent.IsChatActive)
                return;

            if (!lastVitals.Valid)
                return;

            EvaluateTriggers();
        }

        private void DrawVitalsColumn()
        {
            ImGui.SeparatorText("Current Vitals");
            DrawVitalBar("Life", lastVitals.HpPercent, new Vector4(0.85f, 0.15f, 0.15f, 1f));
            DrawVitalBar("Energy Shield", lastVitals.EsPercent, new Vector4(0.92f, 0.92f, 0.92f, 1f));
            DrawVitalBar("Mana", lastVitals.MpPercent, new Vector4(0.2f, 0.45f, 0.95f, 1f));

            ImGui.Spacing();
            ImGui.SeparatorText("Vitals Overlay");
            ImGui.Checkbox("Show in game", ref Settings.ShowVitalsOverlay);

            ImGui.Spacing();
            ImGui.SeparatorText("Input Device");
            DrawDeviceStatus();

            ImGui.Spacing();
            ImGui.SeparatorText("Bound Keys");
            ImGui.Text($"Key 1: {Settings.Key1}");
            ImGui.Text($"Key 2: {Settings.Key2}");
        }

        private void DrawConfigurationColumn()
        {
            ImGui.SeparatorText("Configuration");
            ImGui.Checkbox("Enable AutoPot", ref Settings.EnableAutoPot);
            ImGui.SameLine();
            var statusColor = Settings.EnableAutoPot && serviceStatus == "Running"
                ? new Vector4(0.3f, 1f, 0.3f, 1f)
                : new Vector4(0.7f, 0.7f, 0.7f, 1f);
            ImGui.TextColored(statusColor,
                $"Service Status: {LocalizeServiceStatus(serviceStatus)}");
            if (!string.IsNullOrEmpty(statusDetail))
                ImGuiHelper.ToolTip(LocalizeReason(statusDetail));

            ImGui.Spacing();
            DrawLogicModeCombo();

            ImGui.Spacing();
            DrawThresholdSliders();

            ImGui.Spacing();
            ImGui.SeparatorText("Safety (Auto-Logout)");
            ImGui.TextDisabled(
                "Returns to character select via connection drop.");
            DrawSafetyLogoutRow(ref Settings.HpDisconnectEnabled, ref Settings.HpDisconnectPercent, "##hpdc", "HP");
            DrawSafetyLogoutRow(ref Settings.EsDisconnectEnabled, ref Settings.EsDisconnectPercent, "##esdc", "ES");
            DrawSafetyLogoutRow(ref Settings.MpDisconnectEnabled, ref Settings.MpDisconnectPercent, "##mpdc", "MP");
            ImGui.SetNextItemWidth(220);
            ImGui.SliderInt(
                "Cooldown after logout",
                ref Settings.SafetyLogoutCooldownSeconds,
                0,
                600,
                Settings.SafetyLogoutCooldownSeconds <= 0
                    ? "Off"
                    : $"{Settings.SafetyLogoutCooldownSeconds} s");
            ImGuiHelper.ToolTip(
                "Prevents instant re-logout after reconnecting with low life/mana. 0 disables the timer.");
            ImGui.Checkbox(
                "Re-arm only after vitals recover",
                ref Settings.SafetyLogoutRequireRecovery);
            ImGuiHelper.ToolTip(
                "After a logout, auto-logout stays disabled until each enabled vital is above its threshold again.");

            ImGui.Spacing();
            ImGui.SeparatorText("Hotkeys");
            DrawHotkeyRow("key1", "Key 1 (Life/Hybrid)",
                ref Settings.Key1Enabled, ref Settings.Key1);
            DrawHotkeyRow("key2", "Key 2 (Mana/Utility)",
                ref Settings.Key2Enabled, ref Settings.Key2);

            ImGui.Spacing();
            ImGui.SeparatorText("Input Delays");
            ImGui.SetNextItemWidth(220);
            ImGui.SliderInt("Key1 Delay", ref Settings.Key1DelayMs, 100, 10000,
                $"{Settings.Key1DelayMs} ms");
            ImGui.SetNextItemWidth(220);
            ImGui.SliderInt("Key2 Delay", ref Settings.Key2DelayMs, 100, 10000,
                $"{Settings.Key2DelayMs} ms");

            ImGui.Spacing();
            ImGui.Checkbox("Run in hideout", ref Settings.RunInHideout);
        }

        private void DrawLogicModeCombo()
        {
            if (ImGui.BeginCombo("Logic Mode", LogicModeLabels.Display(Settings.LogicMode)))
            {
                foreach (var mode in LogicModeLabels.All)
                {
                    bool selected = Settings.LogicMode == mode;
                    if (ImGui.Selectable(LogicModeLabels.Display(mode), selected))
                        Settings.LogicMode = mode;
                    if (selected)
                        ImGui.SetItemDefaultFocus();
                }
                ImGui.EndCombo();
            }
        }

        private void DrawThresholdSliders()
        {
            bool showHp = Settings.LogicMode is LogicMode.ManaAndLife or LogicMode.LifeOnly
                or LogicMode.EnergyShield or LogicMode.HybridLifeEs or LogicMode.ManaAndEs
                or LogicMode.LifeEsMana;
            bool showMp = Settings.LogicMode is LogicMode.ManaAndLife or LogicMode.ManaOnly
                or LogicMode.ManaAndEs or LogicMode.LifeEsMana;

            if (showHp)
            {
                string hpLabel = Settings.LogicMode switch
                {
                    LogicMode.EnergyShield => "ES",
                    LogicMode.ManaAndEs => "ES",
                    LogicMode.HybridLifeEs => "Life+ES",
                    _ => "HP",
                };
                ImGui.SetNextItemWidth(220);
                ImGui.SliderInt($"##hpthresh", ref Settings.HpThresholdPercent, 1, 99, $"{hpLabel} %d%%");
            }

            if (showMp)
            {
                ImGui.SetNextItemWidth(220);
                ImGui.SliderInt("##mpthresh", ref Settings.MpThresholdPercent, 1, 99, "MP %d%%");
            }
        }

        private static void DrawSafetyLogoutRow(
            ref bool enabled,
            ref int thresholdPercent,
            string sliderId,
            string vitalLabel)
        {
            ImGui.Checkbox($"##en{sliderId}", ref enabled);
            ImGui.SameLine();
            ImGui.Text("Logout at");
            ImGui.SameLine();
            ImGui.SetNextItemWidth(180);
            ImGui.SliderInt(sliderId, ref thresholdPercent, 1, 99, $"%d%% {vitalLabel}");
        }

        private static void DrawHotkeyRow(string id, string label, ref bool enabled, ref VK key)
        {
            ImGui.Checkbox($"##en{id}", ref enabled);
            ImGui.SameLine();
            ImGui.Text(label);
            ImGui.SameLine();
            var tmp = key;
            if (ImGuiHelper.NonContinuousEnumComboBox($"##{id}", ref tmp))
                key = tmp;
        }

        private static void DrawDeviceStatus()
        {
            bool vigem = InputDeviceStatus.IsViGEmBusInstalled();
            if (vigem)
                ImGui.TextColored(new Vector4(0.3f, 1f, 0.3f, 1f), "ViGEmBus: Installed");
            else
            {
                ImGui.TextColored(new Vector4(1f, 0.35f, 0.35f, 1f),
                    "ViGEmBus: Not installed");
                ImGui.SameLine();
                if (ImGui.SmallButton("[Download]"))
                    System.Diagnostics.Process.Start(new ProcessStartInfo(InputDeviceStatus.ViGEmDownloadLink) { UseShellExecute = true });
            }

            bool controller = InputDeviceStatus.IsControllerConnected();
            if (controller)
                ImGui.TextColored(new Vector4(0.3f, 1f, 0.3f, 1f), "Controller: Detected");
            else
                ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1f),
                    "Controller: Not detected");

            ImGuiHelper.ToolTip(
                "Keyboard autopot uses GameHelper key simulation (like AutoHotKey Trigger). " +
                "ViGEmBus is optional and only needed for virtual-controller setups.");
        }

        private static void DrawVitalBar(string label, int percent, Vector4 fillColor)
        {
            ImGui.Text($"{label}");
            var avail = ImGui.GetContentRegionAvail().X;
            var barHeight = 18f;
            var pos = ImGui.GetCursorScreenPos();
            var drawList = ImGui.GetWindowDrawList();
            var bgMin = pos;
            var bgMax = new Vector2(pos.X + avail, pos.Y + barHeight);
            drawList.AddRectFilled(bgMin, bgMax, ImGuiHelper.Color(new Vector4(0.15f, 0.15f, 0.15f, 1f)), 3f);
            float fillW = avail * Math.Clamp(percent, 0, 100) / 100f;
            if (fillW > 0)
                drawList.AddRectFilled(bgMin, new Vector2(pos.X + fillW, pos.Y + barHeight), ImGuiHelper.Color(fillColor), 3f);
            var text = $"{percent}%";
            var textSize = ImGui.CalcTextSize(text);
            drawList.AddText(new Vector2(pos.X + (avail - textSize.X) * 0.5f, pos.Y + (barHeight - textSize.Y) * 0.5f),
                ImGuiHelper.Color(new Vector4(1f, 1f, 1f, 1f)), text);
            ImGui.Dummy(new Vector2(avail, barHeight + 4f));
        }

        private void DrawVitalsOverlay()
        {
            ImGui.SetNextWindowBgAlpha(0.55f);
            ImGui.SetNextWindowSize(new Vector2(220, 0), ImGuiCond.FirstUseEver);
            if (!ImGui.Begin($"AutoPot Vitals###AutopotVitals",
                    ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.AlwaysAutoResize))
            {
                ImGui.End();
                return;
            }

            DrawVitalBar("Life", lastVitals.HpPercent, new Vector4(0.85f, 0.15f, 0.15f, 1f));
            DrawVitalBar("ES", lastVitals.EsPercent, new Vector4(0.92f, 0.92f, 0.92f, 1f));
            DrawVitalBar("Mana", lastVitals.MpPercent, new Vector4(0.2f, 0.45f, 0.95f, 1f));
            ImGui.End();
        }

        private void EvaluateTriggers()
        {
            bool key1Trigger = Settings.Key1Enabled && ShouldTriggerKey1(lastVitals);
            bool key2Trigger = Settings.Key2Enabled && ShouldTriggerKey2(lastVitals);

            if (key1Trigger && key1Cooldown.ElapsedMilliseconds >= Settings.Key1DelayMs)
            {
                if (MiscHelper.KeyUp(Settings.Key1))
                    key1Cooldown.Restart();
            }

            if (key2Trigger && key2Cooldown.ElapsedMilliseconds >= Settings.Key2DelayMs)
            {
                if (MiscHelper.KeyUp(Settings.Key2))
                    key2Cooldown.Restart();
            }
        }

        private bool ShouldTriggerKey1(VitalsSnapshot v) => Settings.LogicMode switch
        {
            LogicMode.ManaAndLife => v.HpPercent <= Settings.HpThresholdPercent,
            LogicMode.LifeOnly => v.HpPercent <= Settings.HpThresholdPercent,
            LogicMode.EnergyShield => v.EsPercent <= Settings.HpThresholdPercent,
            LogicMode.HybridLifeEs => v.HybridPercent <= Settings.HpThresholdPercent,
            LogicMode.ManaOnly => false,
            LogicMode.ManaAndEs => v.EsPercent <= Settings.HpThresholdPercent,
            LogicMode.LifeEsMana => v.HpPercent <= Settings.HpThresholdPercent || v.EsPercent <= Settings.HpThresholdPercent,
            _ => false,
        };

        private bool ShouldTriggerKey2(VitalsSnapshot v) => Settings.LogicMode switch
        {
            LogicMode.ManaAndLife => v.MpPercent <= Settings.MpThresholdPercent,
            LogicMode.LifeOnly => false,
            LogicMode.EnergyShield => false,
            LogicMode.HybridLifeEs => false,
            LogicMode.ManaOnly => v.MpPercent <= Settings.MpThresholdPercent,
            LogicMode.ManaAndEs => v.MpPercent <= Settings.MpThresholdPercent,
            LogicMode.LifeEsMana => v.MpPercent <= Settings.MpThresholdPercent,
            _ => false,
        };

        private bool TrySafetyLogout()
        {
            if (safetyLogoutTriggered)
                return true;

            if (!Settings.HpDisconnectEnabled && !Settings.EsDisconnectEnabled && !Settings.MpDisconnectEnabled)
                return false;

            if (!IsSafetyLogoutRearmed())
                return false;

            if (!CanSafetyLogout(out _))
                return false;

            if (!lastVitals.Valid)
                return false;

            if (Settings.HpDisconnectEnabled && lastVitals.HpPercent <= Settings.HpDisconnectPercent)
            {
                TriggerSafetyLogout("HP");
                return true;
            }

            if (Settings.EsDisconnectEnabled && lastVitals.HasEnergyShield
                && lastVitals.EsPercent <= Settings.EsDisconnectPercent)
            {
                TriggerSafetyLogout("ES");
                return true;
            }

            if (Settings.MpDisconnectEnabled && lastVitals.MpPercent <= Settings.MpDisconnectPercent)
            {
                TriggerSafetyLogout("MP");
                return true;
            }

            return false;
        }

        private void TriggerSafetyLogout(string vital)
        {
            safetyLogoutTriggered = true;
            safetyLogoutCooldown.Restart();
            safetyLogoutWaitingRecovery = Settings.SafetyLogoutRequireRecovery;
            MiscHelper.KillTCPConnectionForProcess(Core.Process.Pid);
            statusDetail = $"Auto-logout triggered ({vital}).";
        }

        private bool IsSafetyLogoutRearmed()
        {
            if (Settings.SafetyLogoutCooldownSeconds > 0 &&
                safetyLogoutCooldown.IsRunning &&
                safetyLogoutCooldown.Elapsed.TotalSeconds < Settings.SafetyLogoutCooldownSeconds)
            {
                return false;
            }

            if (safetyLogoutWaitingRecovery && !AreSafetyLogoutVitalsRecovered())
            {
                return false;
            }

            safetyLogoutWaitingRecovery = false;
            return true;
        }

        private bool AreSafetyLogoutVitalsRecovered()
        {
            if (!lastVitals.Valid)
            {
                return false;
            }

            if (Settings.HpDisconnectEnabled && lastVitals.HpPercent <= Settings.HpDisconnectPercent)
            {
                return false;
            }

            if (Settings.EsDisconnectEnabled && lastVitals.HasEnergyShield
                && lastVitals.EsPercent <= Settings.EsDisconnectPercent)
            {
                return false;
            }

            if (Settings.MpDisconnectEnabled && lastVitals.MpPercent <= Settings.MpDisconnectPercent)
            {
                return false;
            }

            return true;
        }

        private bool CanSafetyLogout(out string reason)
        {
            if (Core.States.GameCurrentState != GameStateTypes.InGameState)
            {
                reason = $"Game state is {Core.States.GameCurrentState}, not InGameState.";
                safetyLogoutTriggered = false;
                return false;
            }

            var area = Core.States.InGameStateObject.CurrentWorldInstance.AreaDetails;
            if (area.IsTown)
            {
                reason = "Player is in town.";
                return false;
            }

            if (!Core.States.InGameStateObject.CurrentAreaInstance.Player.TryGetComponent<Life>(out var life))
            {
                reason = "Cannot read player Life component.";
                return false;
            }

            if (life.Health.Current <= 0)
            {
                reason = "Player is dead.";
                safetyLogoutTriggered = false;
                return false;
            }

            if (Core.States.InGameStateObject.CurrentAreaInstance.Player.TryGetComponent<Buffs>(out var buffs)
                && buffs.StatusEffects.ContainsKey("grace_period"))
            {
                reason = "Player has grace period.";
                return false;
            }

            reason = string.Empty;
            return true;
        }

        private void RefreshVitals()
        {
            if (Core.States.GameCurrentState != GameStateTypes.InGameState)
            {
                lastVitals = default;
                safetyLogoutTriggered = false;
                return;
            }

            if (Core.States.InGameStateObject.CurrentAreaInstance.Player.TryGetComponent<Life>(out var life))
                lastVitals = VitalsSnapshot.FromLife(life);
            else
                lastVitals = default;
        }

        private void UpdateServiceStatus()
        {
            if (!Settings.EnableAutoPot)
            {
                serviceStatus = "Stopped";
                statusDetail = "Enable AutoPot to start monitoring.";
                return;
            }

            if (CanExecute(out var reason))
            {
                serviceStatus = "Running";
                statusDetail = "Monitoring vitals and pressing configured keys.";
            }
            else
            {
                serviceStatus = "Stopped";
                statusDetail = reason;
            }
        }

        private bool CanExecute(out string reason)
        {
            if (Core.States.GameCurrentState != GameStateTypes.InGameState)
            {
                reason = $"Game state is {Core.States.GameCurrentState}, not InGameState.";
                return false;
            }

            if (!Core.Process.Foreground)
            {
                reason = "Game window is not in the foreground.";
                return false;
            }

            var area = Core.States.InGameStateObject.CurrentWorldInstance.AreaDetails;
            if (area.IsTown)
            {
                reason = "Player is in town.";
                return false;
            }

            if (!Settings.RunInHideout && area.IsHideout)
            {
                reason = "Player is in hideout (enable 'Run in hideout' to allow).";
                return false;
            }

            if (!Core.States.InGameStateObject.CurrentAreaInstance.Player.TryGetComponent<Life>(out var life))
            {
                reason = "Cannot read player Life component.";
                return false;
            }

            if (life.Health.Current <= 0)
            {
                reason = "Player is dead.";
                return false;
            }

            if (Core.States.InGameStateObject.CurrentAreaInstance.Player.TryGetComponent<Buffs>(out var buffs)
                && buffs.StatusEffects.ContainsKey("grace_period"))
            {
                reason = "Player has grace period.";
                return false;
            }

            reason = string.Empty;
            return true;
        }

        private static string LocalizeServiceStatus(string status) => status switch
        {
            "Running" => "Running",
            "Stopped" => "Stopped",
            _ => status,
        };

        private static string LocalizeReason(string reason)
        {
            if (string.IsNullOrEmpty(reason))
                return reason;

            return reason switch
            {
                "Enable AutoPot to start monitoring." => "Enable AutoPot to start monitoring.",
                "Monitoring vitals and pressing configured keys." => "Monitoring vitals and pressing configured keys.",
                "Game window is not in the foreground." => "Game window is not in the foreground.",
                "Player is in town." => "Player is in town.",
                "Cannot read player Life component." => "Cannot read player Life component.",
                "Player is dead." => "Player is dead.",
                "Player has grace period." => "Player has grace period.",
                "Player is in hideout (enable 'Run in hideout' to allow)." =>
                    "Player is in hideout (enable 'Run in hideout' to allow).",
                _ => reason,
            };
        }
    }
}
