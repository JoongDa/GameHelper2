namespace CraftingHelper;

using System;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Reflection;
using GameHelper;
using GameHelper.Plugin;
using GameHelper.RemoteEnums;
using GameHelper.RemoteObjects.Components;
using GameHelper.RemoteObjects.States.InGameStateObjects;
using GameHelper.RemoteObjects.UiElement;

/// <summary>
/// Uses Core's UI tree, bounds and item components. The two missing public APIs (slot item
/// pointer and fresh Item construction) are isolated here until Core exposes them.
/// Only the hovered item and a manually selected source are materialized, at most every 50 ms.
/// </summary>
public sealed class HoveredItemAdapter
{
    private const int SampleIntervalMilliseconds = 50;
    private const int MaximumSlots = 120;
    private static readonly PropertyInfo? HandleProperty = typeof(GameHelper.GameProcess).GetProperty(
        "Handle", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
    private static readonly ConstructorInfo? ItemConstructor = typeof(Item).GetConstructor(
        BindingFlags.Instance | BindingFlags.NonPublic, null, new[] { typeof(IntPtr) }, null);

    private object? handle;
    private MethodInfo? tryReadPointerMethod;
    private int? preferredPointerOffset;
    private long sequence;
    private long lastHoverRead;
    private long lastSourceRead;
    private ItemSnapshot? hovered;
    private ItemSnapshot? sourceObservation;
    private string sourceIdentity = string.Empty;
    private int[] inventoryPath = { 5, 36 };
    private int scanStart = 0x20;
    private int scanEnd = 0x600;
    private bool configurationValid = true;
    private UiElementBase? cachedGrid;
    private long lastGridRead;
    private string cachedContext = string.Empty;
    private string lastGridContextPrefix = string.Empty;

    public string LastError { get; private set; } = string.Empty;

    public bool IsInventoryVisible => this.TryGetInventoryGrid(out _, out _);

    public void Configure(string inventoryPath, int scanStart = 0x20, int scanEnd = 0x600)
    {
        var segments = (inventoryPath ?? string.Empty).Split(',', StringSplitOptions.TrimEntries);
        var parsed = new int[segments.Length];
        if (segments.Length == 0 || segments.Length > 16 ||
            !segments.Select((segment, index) => int.TryParse(segment, out parsed[index]) &&
                parsed[index] >= 0 && parsed[index] <= 200).All(valid => valid) ||
            scanStart < 0x20 || scanEnd > 0x1000 || scanStart >= scanEnd ||
            scanStart % IntPtr.Size != 0 || scanEnd % IntPtr.Size != 0)
        {
            this.configurationValid = false;
            this.LastError = "The inventory path or slot pointer search range is invalid.";
            return;
        }

        if (!this.configurationValid || !this.inventoryPath.SequenceEqual(parsed) ||
            this.scanStart != scanStart || this.scanEnd != scanEnd)
        {
            this.Reset();
            this.inventoryPath = parsed;
            this.scanStart = scanStart;
            this.scanEnd = scanEnd;
        }

        this.configurationValid = true;
    }

    public bool TryReadHovered(Vector2 mousePosition, out ItemSnapshot? snapshot, bool forceRefresh = false)
    {
        snapshot = null;
        this.LastError = "No readable backpack item is under the cursor.";
        try
        {
            if (!this.TryGetInventoryGrid(out var grid, out var contextKey, forceRefresh))
            {
                this.hovered = null;
                return false;
            }

            if (!forceRefresh && !SampleDue(this.lastHoverRead))
            {
                if (this.hovered != null && this.hovered.ContextKey == contextKey && this.hovered.Contains(mousePosition))
                {
                    snapshot = this.hovered;
                    this.LastError = string.Empty;
                    return true;
                }

                return false;
            }

            this.lastHoverRead = Stopwatch.GetTimestamp();
            this.hovered = null;
            if (!float.IsFinite(mousePosition.X) || !float.IsFinite(mousePosition.Y))
            {
                return false;
            }

            // Inspect UI bounds only; item-pointer fallback is restricted to this mouse slot.
            for (var index = 0; index < Math.Min(grid.TotalChildrens, MaximumSlots); index++)
            {
                var slot = grid[index];
                if (slot == null || !TryGetRectangle(slot, out var position, out var size) ||
                    !Contains(position, size, mousePosition))
                {
                    continue;
                }

                if (!this.TrySnapshot(slot, position, size, contextKey, out snapshot, out _))
                {
                    return false;
                }

                this.hovered = snapshot;
                this.LastError = string.Empty;
                return true;
            }
        }
        catch (Exception ex)
        {
            this.LastError = ex.GetBaseException().Message;
            this.hovered = null;
        }

        return false;
    }

    public bool TryReadSource(ItemSnapshot source, out ItemSnapshot? snapshot)
    {
        snapshot = null;
        this.LastError = "The currency source could not be read.";
        try
        {
            if (!source.IsCurrency || source.ItemAddress == IntPtr.Zero ||
                !this.TryGetInventoryGrid(out var grid, out var contextKey) ||
                !string.Equals(source.ContextKey, contextKey, StringComparison.Ordinal))
            {
                this.LastError = "The currency source or inventory context changed.";
                return false;
            }

            var identity = $"{source.ContextKey}:{source.SlotAddress:X}:{source.ItemAddress:X}";
            if (identity == this.sourceIdentity && this.sourceObservation != null && !SampleDue(this.lastSourceRead))
            {
                snapshot = this.sourceObservation;
                this.LastError = string.Empty;
                return true;
            }

            this.sourceIdentity = identity;
            this.lastSourceRead = Stopwatch.GetTimestamp();
            this.sourceObservation = null;
            UiElementBase? sourceSlot = null;
            for (var index = 0; index < Math.Min(grid.TotalChildrens, MaximumSlots); index++)
            {
                var slot = grid[index];
                if (slot?.Address == source.SlotAddress)
                {
                    sourceSlot = slot;
                    break;
                }
            }

            // Ordinary inventory item UI nodes disappear when their last stack item is used.
            if (sourceSlot == null)
            {
                snapshot = this.EmptySource(source);
                this.sourceObservation = snapshot;
                this.LastError = string.Empty;
                return true;
            }

            if (!TryGetRectangle(sourceSlot, out var position, out var size))
            {
                this.LastError = "The currency source is no longer visible.";
                return false;
            }

            if (!this.TrySnapshot(sourceSlot, position, size, contextKey, out snapshot, out var isEmpty))
            {
                if (!isEmpty)
                {
                    return false;
                }

                snapshot = this.EmptySource(source);
            }
            else if (!source.SameIdentity(snapshot))
            {
                this.LastError = "The currency source item changed.";
                snapshot = null;
                return false;
            }

            this.sourceObservation = snapshot;
            this.LastError = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            this.LastError = ex.GetBaseException().Message;
            return false;
        }
    }

    public void Reset()
    {
        this.hovered = null;
        this.sourceObservation = null;
        this.sourceIdentity = string.Empty;
        this.lastHoverRead = 0;
        this.lastSourceRead = 0;
        this.handle = null;
        this.tryReadPointerMethod = null;
        this.preferredPointerOffset = null;
        this.cachedGrid = null;
        this.lastGridRead = 0;
        this.cachedContext = string.Empty;
        this.lastGridContextPrefix = string.Empty;
        this.LastError = string.Empty;
    }

    private static bool SampleDue(long previous) => previous == 0 ||
        Stopwatch.GetElapsedTime(previous).TotalMilliseconds >= SampleIntervalMilliseconds;

    private static bool Contains(Vector2 position, Vector2 size, Vector2 point) =>
        point.X >= position.X && point.Y >= position.Y &&
        point.X < position.X + size.X && point.Y < position.Y + size.Y;

    private bool TryGetInventoryGrid(out UiElementBase grid, out string contextKey, bool forceRefresh = false)
    {
        grid = null!;
        contextKey = string.Empty;
        if (!this.configurationValid)
        {
            this.LastError = "The inventory path or slot pointer search range is invalid.";
            return false;
        }

        if (Core.Process.Pid == 0 || !Core.Process.Foreground || Core.GHSettings.EnableControllerMode ||
            Core.States.GameCurrentState != GameStateTypes.InGameState)
        {
            this.LastError = "The single-player game must be in the foreground.";
            return false;
        }

        var state = Core.States.InGameStateObject;
        var ui = state.GameUi;
        if (ui.Address == IntPtr.Zero || !ui.RightPanel.IsVisible ||
            ui.WorldMapPanel.IsVisible || ui.AtlasSkillsPanel.IsVisible || ui.TempleConsole.IsVisible ||
            ui.CurrencyExchangePanel.IsVisible || ui.GemcuttingPanel.IsVisible ||
            ui.SupportGemcuttingPanel.IsVisible || ui.SekhemasTrialMapPanel.IsVisible || ui.IsPassiveSkillTreeOpen)
        {
            this.LastError = "The backpack is closed or covered by another panel.";
            return false;
        }

        var contextPrefix = $"{Core.Process.Pid}:{state.CurrentAreaInstance.Address:X}:{state.CurrentAreaInstance.AreaHash}:{ui.Address:X}:";
        if (!forceRefresh && !SampleDue(this.lastGridRead) && this.lastGridContextPrefix == contextPrefix)
        {
            if (this.cachedGrid != null)
            {
                grid = this.cachedGrid;
                contextKey = this.cachedContext;
                return true;
            }

            this.LastError = "The configured backpack grid is unavailable.";
            return false;
        }

        this.lastGridRead = Stopwatch.GetTimestamp();
        this.lastGridContextPrefix = contextPrefix;
        this.cachedGrid = null;
        this.cachedContext = string.Empty;
        // Single-player keyboard inventory path already used by StashUtility (default 5,36).
        // Core refreshes RightPanel and invalidates its child cache each frame. Walking from
        // that public root obtains fresh child models without mutating shared Core nodes.
        UiElementBase? candidate = ui.RightPanel;
        foreach (var childIndex in this.inventoryPath)
        {
            candidate = candidate[childIndex];
            if (candidate == null || candidate.Address == IntPtr.Zero)
            {
                this.LastError = "The configured backpack path is unavailable.";
                return false;
            }

            if (!candidate.IsVisible)
            {
                this.LastError = "The backpack grid is hidden.";
                return false;
            }
        }

        if (candidate.TotalChildrens > MaximumSlots)
        {
            this.LastError = "The configured path is not an ordinary backpack grid.";
            return false;
        }

        grid = candidate;
        contextKey = contextPrefix + $"{candidate.Address:X}";
        this.cachedGrid = grid;
        this.cachedContext = contextKey;
        return true;
    }

    private static bool TryGetRectangle(UiElementBase slot, out Vector2 position, out Vector2 size)
    {
        position = slot.Position;
        size = slot.Size;
        if (!slot.IsVisible)
        {
            return false;
        }

        if (!ValidRectangle(position, size) &&
            !PluginUiElementReflection.TryGetAbsoluteRect(slot.Address, out position, out size))
        {
            return false;
        }

        return ValidRectangle(position, size);
    }

    private static bool ValidRectangle(Vector2 position, Vector2 size)
    {
        var window = Core.Process.WindowArea;
        return float.IsFinite(position.X) && float.IsFinite(position.Y) &&
            float.IsFinite(size.X) && float.IsFinite(size.Y) &&
            position.X >= 0 && position.Y >= 0 && size.X > 0 && size.Y > 0 &&
            size.X <= window.Width && size.Y <= window.Height &&
            position.X + size.X <= window.Width + 2 && position.Y + size.Y <= window.Height + 2;
    }

    private bool TrySnapshot(UiElementBase slot, Vector2 position, Vector2 size, string contextKey,
        out ItemSnapshot? snapshot, out bool isEmpty)
    {
        snapshot = null;
        if (!this.TryGetItemPointer(slot.Address, out var itemAddress, out isEmpty))
        {
            return false;
        }

        if (ItemConstructor?.Invoke(new object[] { itemAddress }) is not Item item || !item.IsValid ||
            !item.TryGetComponent<GameHelper.RemoteObjects.Components.Base>(out var itemBase) ||
            string.IsNullOrEmpty(itemBase.InternalName))
        {
            this.LastError = "The item base data is unavailable.";
            return false;
        }

        var isEquipment = IsEquipmentPath(item.Path);
        var isCurrency = item.Path.StartsWith("Metadata/Items/Currency/", StringComparison.OrdinalIgnoreCase);
        var mods = Array.Empty<ItemModSnapshot>();
        var rarity = Rarity.Normal;
        if (item.TryGetComponent<Mods>(out var component))
        {
            rarity = component.Rarity;
            mods = component.ExplicitMods.Select(mod => new ItemModSnapshot(
                mod.name, mod.values.value0, mod.values.value1)).ToArray();
        }
        else if (isEquipment)
        {
            this.LastError = "The item's explicit mods are unavailable.";
            return false;
        }

        var count = 1;
        if (item.TryGetComponent<Stack>(out var stack))
        {
            count = Math.Max(0, stack.Count);
        }
        else if (isCurrency)
        {
            this.LastError = "The currency stack count is unavailable.";
            return false;
        }

        var signature = $"{(int)rarity}:" + string.Join(";", mods.OrderBy(mod => mod.Id, StringComparer.Ordinal)
            .ThenBy(mod => BitConverter.SingleToInt32Bits(mod.Value0))
            .ThenBy(mod => BitConverter.SingleToInt32Bits(mod.Value1))
            .Select(mod => $"{mod.Id}:{BitConverter.SingleToInt32Bits(mod.Value0):X8}:{BitConverter.SingleToInt32Bits(mod.Value1):X8}"));
        snapshot = new ItemSnapshot
        {
            SlotAddress = slot.Address,
            ItemAddress = item.Address,
            Position = position,
            Size = size,
            BaseId = itemBase.InternalName,
            DisplayName = itemBase.BaseItemName,
            Path = item.Path,
            Rarity = rarity,
            ExplicitMods = Array.AsReadOnly(mods),
            Signature = signature,
            StackCount = count,
            IsEquipment = isEquipment,
            IsCurrency = isCurrency,
            SampleSequence = ++this.sequence,
            ContextKey = contextKey,
        };
        return true;
    }

    private ItemSnapshot EmptySource(ItemSnapshot source) => source with
    {
        ItemAddress = IntPtr.Zero,
        StackCount = 0,
        SampleSequence = ++this.sequence,
    };

    private static bool IsEquipmentPath(string path) =>
        path.StartsWith("Metadata/Items/Weapons/", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith("Metadata/Items/Armours/", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith("Metadata/Items/Rings/", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith("Metadata/Items/Amulets/", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith("Metadata/Items/Belts/", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith("Metadata/Items/Quivers/", StringComparison.OrdinalIgnoreCase);

    private bool TryGetItemPointer(IntPtr slotAddress, out IntPtr itemAddress, out bool isEmpty)
    {
        itemAddress = IntPtr.Zero;
        isEmpty = false;
        if (!this.EnsureReader())
        {
            this.LastError = "The game memory reader is unavailable.";
            return false;
        }

        if (this.preferredPointerOffset is int knownOffset &&
            this.TryReadPointer(slotAddress + knownOffset, out var knownAddress))
        {
            if (knownAddress == IntPtr.Zero)
            {
                isEmpty = true;
                return false;
            }

            if (PluginUiElementReflection.TryValidateItemAddress(knownAddress, out _, out _))
            {
                itemAddress = knownAddress;
                return true;
            }
        }

        // Discover the ordinary slot's pointer once, then reuse that offset. No item or mod
        // offsets are duplicated: validation, Item, Base, Mods and Stack remain Core-owned.
        for (var offset = this.scanStart; offset + IntPtr.Size <= this.scanEnd; offset += IntPtr.Size)
        {
            if (offset == this.preferredPointerOffset ||
                !this.TryReadPointer(slotAddress + offset, out var candidate) || candidate == IntPtr.Zero ||
                !PluginUiElementReflection.TryValidateItemAddress(candidate, out _, out _))
            {
                continue;
            }

            this.preferredPointerOffset = offset;
            itemAddress = candidate;
            return true;
        }

        this.LastError = "The hovered slot does not contain a readable item.";
        return false;
    }

    private bool EnsureReader()
    {
        var currentHandle = HandleProperty?.GetValue(Core.Process);
        if (currentHandle == null)
        {
            return false;
        }

        if (!ReferenceEquals(this.handle, currentHandle))
        {
            this.handle = currentHandle;
            this.tryReadPointerMethod = currentHandle.GetType()
                .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(method => method.Name == "TryReadMemory" && method.IsGenericMethodDefinition &&
                    method.GetParameters().Length == 2)?.MakeGenericMethod(typeof(IntPtr));
            this.preferredPointerOffset = null;
        }

        return this.tryReadPointerMethod != null;
    }

    private bool TryReadPointer(IntPtr address, out IntPtr pointer)
    {
        pointer = IntPtr.Zero;
        try
        {
            var arguments = new object[] { address, IntPtr.Zero };
            if (this.tryReadPointerMethod?.Invoke(this.handle, arguments) is not true)
            {
                return false;
            }

            pointer = (IntPtr)arguments[1];
            return true;
        }
        catch
        {
            return false;
        }
    }
}
