namespace CraftingHelper;

using GameHelper.Plugin;
using System.Numerics;
using StashUtility.Data;

public sealed class CraftingHelperSettings : IPSettings
{
    public bool EnableRolling = false;
    public bool PreviewOnly = false;
    public bool ShowStatusOverlay = true;
    public bool ShowDiagnostics = false;
    public bool WriteSessionLog = true;
    public int MaxTier = 3;
    public bool MatchTargetTypes = true;
    public List<string> TargetModTypes = new();
    public bool MatchModCount = false;
    public int MinQualifyingMods = 2;
    public int MinClickIntervalMs = 200;
    public int MaxClickIntervalMs = 300;
    public int ResultTimeoutMs = 1500;
    public int MaxClicks = 100;
    public string InventoryPath = "5,36";
    public int ScanStartOffset = 0x20;
    public int ScanEndOffset = 0x600;
    public Vector2 OverlayPosition = new(24, 90);

    internal void Normalize()
    {
        MaxTier = Math.Clamp(MaxTier, 1, 100);
        MinQualifyingMods = Math.Clamp(MinQualifyingMods, 1, 12);
        MinClickIntervalMs = Math.Clamp(MinClickIntervalMs, 200, 2000);
        MaxClickIntervalMs = Math.Clamp(MaxClickIntervalMs, MinClickIntervalMs, 3000);
        ResultTimeoutMs = Math.Clamp(ResultTimeoutMs, 500, 10000);
        MaxClicks = Math.Clamp(MaxClicks, 1, 10000);
        ScanStartOffset = Math.Clamp(ScanStartOffset, 0x20, 0xFF8) & ~7;
        ScanEndOffset = Math.Clamp(ScanEndOffset, ScanStartOffset + 8, 0x1000) & ~7;
        InventoryPath = string.IsNullOrWhiteSpace(InventoryPath) ? "5,36" : InventoryPath;
        TargetModTypes = (TargetModTypes ?? new())
            .Where(type => !string.IsNullOrWhiteSpace(type))
            .Select(EquipmentAffixTiers.NormalizeTargetType)
            .Distinct(StringComparer.Ordinal).ToList();
        if (!float.IsFinite(OverlayPosition.X) || !float.IsFinite(OverlayPosition.Y))
            OverlayPosition = new(24, 90);
    }
}
