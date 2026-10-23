namespace CraftingHelper;

using GameHelper;
using GameHelper.Plugin;
using GameHelper.RemoteEnums;
using ImGuiNET;
using Newtonsoft.Json;
using StashUtility.Data;
using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;

/// <summary>Single-item, hold-to-run crafting. All additional reads and input belong to this plugin.</summary>
public sealed class CraftingHelperCore : PCore<CraftingHelperSettings>
{
    private readonly EquipmentAffixTiers tiers = new();
    private readonly HoveredItemAdapter items = new();
    private readonly CraftingSession session = new();
    private ItemSnapshot? currencySource;
    private ItemSnapshot? sourceNow;
    private ItemSnapshot? target;
    private ItemSnapshot? previousTarget;
    private string targetSearch = string.Empty;
    private string context = string.Empty;
    private string lastLoggedState = string.Empty;
    private string notice = string.Empty;
    private bool previousRight;
    private bool previousLeft;
    private bool wasEnabled;
    private int qualifyingMods;
    private bool targetMatched;
    private bool targetUnknown;

    private string SettingsPath => Path.Combine(DllDirectory, "config", "settings.json");
    private string DataPath => Path.Combine(DllDirectory, "Data", "equipment_affix_tiers.json");
    private string LogPath => Path.Combine(DllDirectory, "logs", "sessions.jsonl");

    public override void OnEnable(bool isGameOpened)
    {
        try
        {
            if (File.Exists(SettingsPath))
                Settings = JsonConvert.DeserializeObject<CraftingHelperSettings>(File.ReadAllText(SettingsPath)) ?? new();
        }
        catch (Exception ex) { notice = ex.Message; }
        Settings.Normalize();
        items.Configure(Settings.InventoryPath, Settings.ScanStartOffset, Settings.ScanEndOffset);
        tiers.Load(DataPath);
        ConfigureSession();
        session.Reset();
        wasEnabled = Settings.EnableRolling;
        previousRight = InputAdapter.RightButtonHeld;
        previousLeft = InputAdapter.LeftButtonHeld;
    }

    public override void OnDisable()
    {
        Stop("plugin_disabled");
        ClearSource();
        items.Reset();
        target = previousTarget = null;
        Log("disabled");
    }

    public override void SaveSettings()
    {
        Settings.Normalize();
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        File.WriteAllText(SettingsPath, JsonConvert.SerializeObject(Settings, Formatting.Indented));
    }

    public override void DrawUI()
    {
        try { Update(); }
        catch (Exception ex)
        {
            Stop("read_failed");
            ClearSource();
            notice = ex.Message;
            Log("exception", ex.Message);
        }
        if (Settings.ShowStatusOverlay && InputAdapter.IsGameForeground)
            DrawStatus();
    }

    private void Update()
    {
        var now = Environment.TickCount64;
        bool shift = InputAdapter.ShiftHeld;
        bool right = InputAdapter.RightButtonHeld;
        bool left = InputAdapter.LeftButtonHeld;
        bool rightPressed = right && !previousRight;
        bool leftPressed = left && !previousLeft;
        previousRight = right;
        previousLeft = left;

        var currentContext = $"{Core.Process.Pid}:{Core.States.InGameStateObject.CurrentAreaInstance.Address.ToInt64():X}";
        if (context != currentContext)
        {
            context = currentContext;
            ClearSource();
            items.Reset();
            previousTarget = null;
            Stop("context_changed");
        }

        if (!CanObserve())
        {
            ClearSource();
            target = previousTarget = null;
            TickInvalid(now, shift, "game_unavailable");
            LogState();
            return;
        }

        // Check manual input before emitting our own synchronous down/up pair.
        if (rightPressed || leftPressed || InputAdapter.EscapeHeld || InputAdapter.ControlHeld || InputAdapter.AltHeld)
        {
            ClearSource();
            Stop("manual_input");
        }

        target = null;
        if (rightPressed || shift || Settings.ShowDiagnostics)
        {
            if (InputAdapter.TryGetClientCursorPosition(out var mouse) &&
                items.TryReadHovered(mouse, out var hovered, rightPressed))
            {
                if (rightPressed && hovered!.IsCurrency && hovered.StackCount > 0 && !shift &&
                    !left && !InputAdapter.ControlHeld && !InputAdapter.AltHeld && !InputAdapter.EscapeHeld)
                {
                    currencySource = sourceNow = hovered;
                    notice = string.Empty;
                    Log("source_recorded");
                }
                if (hovered!.IsEquipment) target = hovered;
            }
        }

        if (wasEnabled != Settings.EnableRolling)
        {
            Stop("settings_changed");
            wasEnabled = Settings.EnableRolling;
        }

        if (!Settings.EnableRolling)
        {
            TickInvalid(now, shift, "disabled");
            LogState();
            return;
        }

        if (!shift)
        {
            ConfigureSession();
            session.Tick(new(now, false, false, string.Empty, string.Empty, false, false, 0, 0), Settings.PreviewOnly);
            previousTarget = null;
            LogState();
            return;
        }

        if (right || left || InputAdapter.ControlHeld || InputAdapter.AltHeld || ImGui.GetIO().WantCaptureMouse)
        {
            TickInvalid(now, shift, "manual_input");
            LogState();
            return;
        }
        if (!RulesConfigured())
        {
            TickInvalid(now, shift, "rules_missing");
            LogState();
            return;
        }
        if (target == null || target.Rarity is not (Rarity.Magic or Rarity.Rare))
        {
            TickInvalid(now, shift, "target_missing");
            previousTarget = null;
            LogState();
            return;
        }
        if (currencySource == null)
        {
            TickInvalid(now, shift, "source_missing");
            LogState();
            return;
        }
        if (!items.TryReadSource(currencySource, out var latestSource) || latestSource == null)
        {
            ClearSource();
            TickInvalid(now, shift, "source_changed");
            LogState();
            return;
        }
        sourceNow = latestSource;

        if (previousTarget != null && previousTarget.SlotAddress == target.SlotAddress &&
            previousTarget.ItemAddress != target.ItemAddress && !session.IsWaiting &&
            session.State is CraftingState.Arming or CraftingState.Ready)
        {
            Stop("target_changed");
        }
        previousTarget = target;
        Evaluate(target);
        var binding = $"{target.ContextKey}|{target.SlotAddress.ToInt64():X}|{target.BaseId}|{target.Path}|{currencySource.SlotAddress.ToInt64():X}|{currencySource.ItemAddress.ToInt64():X}";
        var observation = new CraftingSnapshot(Environment.TickCount64, shift, true, binding, target.Signature, targetMatched,
            targetUnknown, sourceNow.StackCount, target.SampleSequence);

        if (session.Tick(observation, Settings.PreviewOnly))
        {
            // Final checks are made immediately before the click; never enqueue input.
            if (!InputAdapter.TryGetClientCursorPosition(out var clickPosition) ||
                !target.Contains(clickPosition) || !CanObserve() || !InputAdapter.TryLeftClick())
                Stop("input_rejected");
            else
            {
                session.NotifyClickIssued(observation with { TimeMs = Environment.TickCount64 });
                Log("click");
            }
        }
        LogState();
    }

    private bool CanObserve() =>
        Core.States.GameCurrentState == GameStateTypes.InGameState &&
        !Core.GHSettings.EnableControllerMode && InputAdapter.IsGameForeground &&
        !Core.States.InGameStateObject.GameUi.ChatParent.IsChatActive && items.IsInventoryVisible;

    private void TickInvalid(long now, bool shift, string reason)
    {
        session.Tick(new(now, shift, false, string.Empty, string.Empty, false, false, 0, 0, reason), Settings.PreviewOnly);
    }

    private bool RulesConfigured() => tiers.Ready &&
        ((Settings.MatchModCount && Settings.MinQualifyingMods > 0) ||
         (Settings.MatchTargetTypes && tiers.TypesReady && Settings.TargetModTypes.Count > 0 &&
          Settings.TargetModTypes.All(type => tiers.TargetTypeIds.Contains(type))));

    private void Evaluate(ItemSnapshot item)
    {
        var match = tiers.Evaluate(item.BaseId, item.Path, item.ExplicitMods.Select(mod => mod.Id),
            Settings.MaxTier, Settings.MatchTargetTypes ? Settings.TargetModTypes : Array.Empty<string>());
        qualifyingMods = match.MatchingCount;
        targetMatched = (Settings.MatchModCount && match.MatchingCount >= Settings.MinQualifyingMods) ||
            (Settings.MatchTargetTypes && !string.IsNullOrEmpty(match.TargetType));
        targetUnknown = !targetMatched && (item.ExplicitMods.Count == 0 || item.ExplicitMods.Any(mod =>
            !tiers.TryGet(item.BaseId, item.Path, mod.Id, out _, out _)));
    }

    private void ConfigureSession()
    {
        Settings.Normalize();
        session.Configure(Settings.MinClickIntervalMs, Settings.MaxClickIntervalMs, 3000,
            Settings.ResultTimeoutMs, Settings.MaxClicks);
    }

    private void ClearSource()
    {
        currencySource = sourceNow = null;
    }

    private string T(string key, string fallback) => PluginText.T($"craftinghelper.{key}", fallback);
    private string Label(string key, string fallback) => PluginText.Label($"craftinghelper.{key}", fallback, key);
    private string StatusText => !Settings.EnableRolling ? T("status.disabled", "Disabled") :
        T($"status.{(session.StatusCode == "invalid" ? session.Status : session.StatusCode)}", session.Status);
    private void Stop(string code) => session.Stop(T($"status.{code}", code), code);
    private string ModName(string type) => PluginText.T($"stashutility.equipment.mod_type.{type.ToLowerInvariant()}",
        Regex.Replace(type, "(?<=[a-z0-9])(?=[A-Z])", " "));

    public override void DrawSettings()
    {
        bool changed = false;
        changed |= ImGui.Checkbox(Label("enable", "Enable semi-automatic crafting"), ref Settings.EnableRolling);
        changed |= ImGui.Checkbox(Label("preview", "Preview only (no clicks)"), ref Settings.PreviewOnly);
        ImGui.TextWrapped(T("instructions", "Right-click a currency stack in your backpack, hover equipment, then hold Shift for 3 seconds. Release Shift to stop."));
        ImGui.TextWrapped(T("independent_input", "Other plugins keep their own input behavior. Disable conflicting automatic input while crafting."));
        ImGui.Separator();
        DrawSummary();
        if (ImGui.Button(Label("clear_source", "Clear recorded currency")))
        {
            ClearSource();
            Stop("manual_input");
        }
        ImGui.SameLine();
        if (ImGui.Button(Label("reload", "Reload affix data")))
        {
            tiers.Load(DataPath);
            Stop("data_changed");
        }
        ImGui.Text($"{T("data_version", "Affix data")}: {(tiers.Ready ? tiers.SourceVersion : T("data_missing", "Unavailable"))}");
        if (!string.IsNullOrEmpty(tiers.Error)) ImGui.TextWrapped(tiers.Error);

        ImGui.Separator();
        changed |= ImGui.SliderInt(Label("max_tier", "T tier or better (T1 best)"), ref Settings.MaxTier, 1, 10);
        changed |= ImGui.Checkbox(Label("match_targets", "Stop on any selected affix"), ref Settings.MatchTargetTypes);
        if (Settings.MatchTargetTypes)
        {
            if (!tiers.TypesReady) ImGui.TextWrapped(T("types_missing", "Target affix data is unavailable."));
            if (ImGui.BeginCombo(Label("add_target", "Add target affix"), T("select_target", "Search and select...")))
            {
                ImGui.InputText(Label("search", "Search"), ref targetSearch, 128);
                foreach (string type in tiers.TargetTypeIds)
                {
                    if (Settings.TargetModTypes.Contains(type)) continue;
                    string name = ModName(type);
                    if (!string.IsNullOrEmpty(targetSearch) &&
                        !name.Contains(targetSearch, StringComparison.OrdinalIgnoreCase) &&
                        !type.Contains(targetSearch, StringComparison.OrdinalIgnoreCase)) continue;
                    if (ImGui.Selectable($"{name}##{type}"))
                    {
                        Settings.TargetModTypes.Add(type);
                        changed = true;
                    }
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip(type);
                }
                ImGui.EndCombo();
            }
            foreach (string type in Settings.TargetModTypes.ToArray())
            {
                if (ImGui.SmallButton($"X##remove_{type}"))
                {
                    Settings.TargetModTypes.Remove(type);
                    changed = true;
                }
                ImGui.SameLine();
                ImGui.Text(ModName(type));
                if (!tiers.TargetTypeIds.Contains(type))
                {
                    ImGui.SameLine();
                    ImGui.TextDisabled(T("unavailable_target", "(not in current data)"));
                }
            }
        }
        changed |= ImGui.Checkbox(Label("match_count", "Stop on qualifying affix count"), ref Settings.MatchModCount);
        if (Settings.MatchModCount)
            changed |= ImGui.SliderInt(Label("minimum_count", "Minimum qualifying affixes"), ref Settings.MinQualifyingMods, 1, 6);
        ImGui.TextWrapped(T("rule_hint", "Enabled stop conditions use OR. Only affixes meeting the tier threshold qualify. Unknown results stop the session."));
        if (!RulesConfigured()) ImGui.TextWrapped(T("rules_required", "Select at least one available stop condition before running."));

        if (ImGui.CollapsingHeader(T("timing", "Timing and limits")))
        {
            changed |= ImGui.SliderInt(Label("min_interval", "Minimum click interval (ms)"), ref Settings.MinClickIntervalMs, 200, 1000);
            changed |= ImGui.SliderInt(Label("max_interval", "Maximum click interval (ms)"), ref Settings.MaxClickIntervalMs, Settings.MinClickIntervalMs, 2000);
            changed |= ImGui.SliderInt(Label("timeout", "Result timeout (ms)"), ref Settings.ResultTimeoutMs, 500, 5000);
            changed |= ImGui.SliderInt(Label("max_clicks", "Maximum clicks per session"), ref Settings.MaxClicks, 1, 1000);
        }
        changed |= ImGui.Checkbox(Label("overlay", "Show status overlay"), ref Settings.ShowStatusOverlay);
        if (ImGui.CollapsingHeader(T("advanced", "Diagnostics and advanced options")))
        {
            changed |= ImGui.Checkbox(Label("diagnostics", "Show hovered item diagnostics"), ref Settings.ShowDiagnostics);
            changed |= ImGui.Checkbox(Label("logging", "Write session log"), ref Settings.WriteSessionLog);
            changed |= ImGui.InputText(Label("inventory_path", "Main inventory UI path"), ref Settings.InventoryPath, 128);
            changed |= ImGui.InputInt(Label("scan_start", "Item pointer scan start (decimal)"), ref Settings.ScanStartOffset, 8, 32);
            changed |= ImGui.InputInt(Label("scan_end", "Item pointer scan end (decimal)"), ref Settings.ScanEndOffset, 8, 32);
            changed |= ImGui.DragFloat2(Label("overlay_position", "Overlay position"), ref Settings.OverlayPosition);
            ImGui.TextWrapped($"{T("adapter_status", "Reader")}: {items.LastError}");
            if (target != null)
            {
                ImGui.TextWrapped($"{target.BaseId}\n{target.Path}");
                ImGui.Text($"Slot 0x{target.SlotAddress.ToInt64():X} | Item 0x{target.ItemAddress.ToInt64():X}");
                foreach (var mod in target.ExplicitMods)
                {
                    string tier = tiers.TryGet(target.BaseId, target.Path, mod.Id, out _, out var value)
                        ? $"T{value}" : "?";
                    ImGui.TextWrapped($"{tier} {mod.Id}: {mod.Value0.ToString(CultureInfo.InvariantCulture)}, {mod.Value1.ToString(CultureInfo.InvariantCulture)}");
                }
            }
        }
        if (changed)
        {
            Settings.Normalize();
            items.Configure(Settings.InventoryPath, Settings.ScanStartOffset, Settings.ScanEndOffset);
            Stop("settings_changed");
            previousTarget = null;
        }
    }

    private void DrawSummary()
    {
        ImGui.TextWrapped($"{T("status", "Status")}: {StatusText}");
        string currency = currencySource == null ? T("no_source", "Right-click a currency stack to record it") :
            $"{currencySource.DisplayName} × {sourceNow?.StackCount ?? currencySource.StackCount}";
        ImGui.TextWrapped($"{T("currency", "Currency")}: {currency}");
        if (target != null)
        {
            ImGui.TextWrapped($"{T("target", "Target")}: {target.DisplayName}");
            ImGui.Text($"{T("qualifying", "Qualifying affixes")}: {qualifyingMods}");
        }
        ImGui.Text($"{T("clicks", "Clicks")}: {session.ClickCount} / {Settings.MaxClicks}");
        if (Settings.PreviewOnly) ImGui.Text(T("preview_active", "PREVIEW — no clicks"));
        if (!string.IsNullOrEmpty(notice)) ImGui.TextWrapped(notice);
    }

    private void DrawStatus()
    {
        ImGui.SetNextWindowPos(Settings.OverlayPosition, ImGuiCond.Always);
        ImGui.SetNextWindowSize(new Vector2(390, 0), ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0.82f);
        if (ImGui.Begin(PluginText.Title("craftinghelper.window", "CraftingHelper", "CraftingHelperStatus"),
            ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoInputs | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoSavedSettings))
        {
            DrawSummary();
            if (session.State == CraftingState.Arming)
                ImGui.ProgressBar(Math.Clamp(session.ArmElapsedMs / 3000f, 0, 1), new Vector2(-1, 0));
        }
        ImGui.End();
    }

    private void LogState()
    {
        string stamp = $"{session.State}:{session.StatusCode}:{session.ClickCount}";
        if (stamp == lastLoggedState) return;
        lastLoggedState = stamp;
        Log("state");
    }

    private void Log(string action, string? error = null)
    {
        if (!Settings.WriteSessionLog) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            // Keep the local diagnostic log bounded without retaining plugin callbacks in Core.
            if (File.Exists(LogPath) && new FileInfo(LogPath).Length > 2 * 1024 * 1024)
                File.Move(LogPath, LogPath + ".previous", true);
            File.AppendAllText(LogPath, JsonConvert.SerializeObject(new
            {
                utc = DateTimeOffset.UtcNow, action, state = session.State.ToString(), reason = session.StatusCode,
                clicks = session.ClickCount, preview = Settings.PreviewOnly, currency = currencySource?.BaseId,
                remaining = sourceNow?.StackCount, target = target?.BaseId, signature = target?.Signature, error
            }) + Environment.NewLine);
        }
        catch { /* Logging must not create additional input or change the crafting result. */ }
    }
}
