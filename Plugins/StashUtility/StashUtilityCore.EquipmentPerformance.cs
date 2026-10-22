// <copyright file="StashUtilityCore.EquipmentPerformance.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace StashUtility
{
    using GameHelper;
    using GameHelper.Plugin;
    using GameHelper.RemoteEnums;
    using GameHelper.RemoteObjects.States.InGameStateObjects;
    using GameHelper.RemoteObjects.UiElement;
    using GameOffsets.Natives;
    using GameOffsets.Objects.States.InGameState;
    using GameOffsets.Objects.UiElement;
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Linq;
    using System.Numerics;
    using System.Reflection;
    using System.Runtime.InteropServices;

    public sealed partial class StashUtilityCore
    {
        private const int EquipmentRefreshIntervalMs = 100;
        private const int EquipmentMaximumSnapshotAgeMs = 200;
        private const double EquipmentRefreshBudgetMs = 2;
        private static readonly FieldInfo EquipmentComponentAddressesField = typeof(Entity)
            .GetField("componentAddresses", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly string[] EquipmentCategories =
            { "Weapons", "Helmets", "Body Armour", "Gloves", "Boots", "Rings", "Amulets", "Belts", "Shields", "Quivers" };

        private readonly Dictionary<string, EquipmentMatcher.CompiledGroup[]> equipmentCompiledGroups = new(StringComparer.Ordinal);
        private readonly Dictionary<IntPtr, EquipmentSnapshot> equipmentSnapshots = new();
        private readonly Dictionary<IntPtr, SlotOffsetCache> itemSlotOffsets = new();
        private readonly Dictionary<IntPtr, (IntPtr Tab, int Index, long Frame)> equipmentActiveTabs = new();
        private readonly Dictionary<IntPtr, (IntPtr NamePointer, string Name)> equipmentModNames = new();
        private readonly List<IntPtr> equipmentStaleKeys = new();
        private readonly List<EquipmentSnapshot> equipmentRefreshQueue = new();
        private readonly List<int[]> equipmentRawValues = new(12);
        private Comparison<EquipmentSnapshot> equipmentRefreshComparer;
        private PropertyInfo processHandleProperty;
        private object equipmentProcessHandle;
        private IntPtr equipmentAreaAddress;
        private string equipmentAreaHash;
        private int equipmentRulesVersion;
        private long equipmentFrame;
        private long equipmentNow;
        private long equipmentPreviousFrameAt;
        private int equipmentFrameIntervalMs = 16;
        private bool equipmentWasEnabled;

        private sealed class EquipmentSnapshot
        {
            internal IntPtr Slot;
            internal IntPtr ItemAddress;
            internal ItemStruct Identity;
            internal Item Item;
            internal IntPtr[] Components;
            internal string Category;
            internal long SeenFrame;
            internal long DrawnFrame;
            internal long QueuedFrame;
            internal long RefreshAt;
            internal long ReadAt;
            internal int RulesVersion;
            internal int MatchedCount;
            internal bool HasSnapshot;
            internal bool HasMods;
            internal Vector2 Position;
            internal Vector2 Size;
            internal List<(string name, (float v0, float v1) vals)> Mods = new(12);
            internal List<(string name, (float v0, float v1) vals)> PendingMods = new(12);
        }

        private sealed class SlotOffsetCache
        {
            internal int Offset = -1;
            internal int ScanStart;
            internal int ScanEnd;
            internal long ProbeAt;
            internal long SeenFrame;
            internal long PointerFrame;
            internal bool Validated;
            internal IntPtr ItemAddress;
            internal ItemStruct Identity;
            internal string Path;
            internal object Parents;
            internal UiElementBase Element;
            internal UiElementBaseOffset Layout;
            internal bool HasLayout;
        }

        public override void DrawUI()
        {
            BeginEquipmentPerformanceFrame();
            FlushPendingSettings();
            equipmentFrame++;
            equipmentNow = Environment.TickCount64;
            equipmentFrameIntervalMs = equipmentPreviousFrameAt == 0 ? 16 :
                (int)Math.Clamp(equipmentNow - equipmentPreviousFrameAt, 1, 100);
            equipmentPreviousFrameAt = equipmentNow;
            equipmentRefreshQueue.Clear();
            bool completed = false;
            try
            {
                if (!RefreshEquipmentContext()) return;
                if (equipmentWasEnabled != Settings.EnableEquipmentManager)
                {
                    equipmentSnapshots.Clear();
                    equipmentWasEnabled = Settings.EnableEquipmentManager;
                }
                DrawOverlayFrame();
                RefreshPendingEquipment();
                completed = true;
            }
            finally
            {
                if (completed) PruneEquipmentCaches();
                else ClearEquipmentRuntimeCaches();
                EndEquipmentPerformanceFrame();
            }
        }

        private bool RefreshEquipmentContext()
        {
            processHandleProperty ??= Core.Process.GetType().GetProperty("Handle",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            var currentHandle = processHandleProperty?.GetValue(Core.Process);
            if (currentHandle == null || currentHandle is SafeHandle { IsClosed: true } ||
                currentHandle is SafeHandle { IsInvalid: true }) return false;

            if (!ReferenceEquals(equipmentProcessHandle, currentHandle))
            {
                ClearEquipmentRuntimeCaches();
                ClearEquipmentMemoryDelegates();
                handleObj = null;
                if (!InitReflection())
                {
                    equipmentProcessHandle = null;
                    handleObj = null;
                    return false;
                }
                equipmentProcessHandle = currentHandle;
            }

            var area = Core.States.InGameStateObject.CurrentAreaInstance;
            var address = area?.Address ?? IntPtr.Zero;
            var hash = area?.AreaHash;
            if (equipmentAreaAddress != address || equipmentAreaHash != hash)
            {
                ClearEquipmentRuntimeCaches();
                equipmentAreaAddress = address;
                equipmentAreaHash = hash;
            }

            return Core.States.GameCurrentState is not (GameStateTypes.AreaLoadingState or GameStateTypes.LoadingState
                or GameStateTypes.GameNotLoaded or GameStateTypes.LoginState or GameStateTypes.SelectCharacterState);
        }

        private void ClearEquipmentRuntimeCaches()
        {
            equipmentSnapshots.Clear();
            itemSlotOffsets.Clear();
            equipmentActiveTabs.Clear();
            equipmentModNames.Clear();
            equipmentRefreshQueue.Clear();
            equipmentRawValues.Clear();
        }

        private void PruneEquipmentCaches()
        {
            equipmentStaleKeys.Clear();
            foreach (var pair in equipmentSnapshots)
                if (pair.Value.SeenFrame != equipmentFrame) equipmentStaleKeys.Add(pair.Key);
            foreach (var key in equipmentStaleKeys) equipmentSnapshots.Remove(key);

            equipmentStaleKeys.Clear();
            foreach (var pair in itemSlotOffsets)
                if (pair.Value.SeenFrame != equipmentFrame) equipmentStaleKeys.Add(pair.Key);
            foreach (var key in equipmentStaleKeys) itemSlotOffsets.Remove(key);

            equipmentStaleKeys.Clear();
            foreach (var pair in equipmentActiveTabs)
                if (pair.Value.Frame != equipmentFrame) equipmentStaleKeys.Add(pair.Key);
            foreach (var key in equipmentStaleKeys) equipmentActiveTabs.Remove(key);
            equipmentRefreshQueue.Clear();
            equipmentRawValues.Clear();
        }

        private void TrackEquipmentStashTab(IntPtr container, IntPtr tab, int index)
        {
            // First observing the second player's panel must not invalidate the first panel.
            if (equipmentActiveTabs.TryGetValue(container, out var previous) &&
                (previous.Tab != tab || previous.Index != index))
            {
                equipmentSnapshots.Clear();
                itemSlotOffsets.Clear();
                equipmentRefreshQueue.Clear();
            }
            equipmentActiveTabs[container] = (tab, index, equipmentFrame);
        }

        private void SaveEquipmentSettings(string category = null)
        {
            equipmentRulesVersion++;
            if (category == null) equipmentCompiledGroups.Clear();
            else equipmentCompiledGroups.Remove(category);
            RequestSettingsSave();
        }

        private List<Models.EquipmentModOption> GetEquipmentOptions(string category)
        {
            if (!equipmentOptionsByCategory.TryGetValue(category, out var options))
            {
                options = Data.ModDatabase.AllEquipmentModOptions.Where(option => option.Mods[0].Category == category).ToList();
                equipmentOptionsByCategory[category] = options;
            }
            return options;
        }

        private EquipmentMatcher.CompiledGroup[] GetEquipmentGroups(string category)
        {
            if (!equipmentCompiledGroups.TryGetValue(category, out var compiled))
            {
                compiled = Settings.EquipmentCategoryGroups != null &&
                    Settings.EquipmentCategoryGroups.TryGetValue(category, out var groups) && groups != null
                    ? groups.Where(group => group?.ModPatternIds?.Count > 0)
                        .Select(group => EquipmentMatcher.Compile(group, GetEquipmentOptions(category))).ToArray()
                    : Array.Empty<EquipmentMatcher.CompiledGroup>();
                equipmentCompiledGroups[category] = compiled;
                if (equipmentDiagnosticsEnabled) equipmentPerformance.RuleCompiles++;
            }
            return compiled;
        }

        private SlotOffsetCache GetSlotCache(IntPtr slot)
        {
            if (!itemSlotOffsets.TryGetValue(slot, out var cached))
            {
                cached = new SlotOffsetCache { ScanStart = Settings.ScanStartOffset, ScanEnd = Settings.ScanEndOffset };
                itemSlotOffsets[slot] = cached;
            }
            cached.SeenFrame = equipmentFrame;
            if (cached.ScanStart != Settings.ScanStartOffset || cached.ScanEnd != Settings.ScanEndOffset)
            {
                cached.Offset = -1;
                cached.ProbeAt = 0;
                cached.PointerFrame = 0;
                cached.ItemAddress = IntPtr.Zero;
                cached.Validated = false;
                cached.ScanStart = Settings.ScanStartOffset;
                cached.ScanEnd = Settings.ScanEndOffset;
                equipmentSnapshots.Remove(slot);
            }
            return cached;
        }

        private void ProcessVisibleItemSlot(IntPtr slot, UiElementBaseOffset layout)
        {
            if (layout.Self != IntPtr.Zero && layout.Self != slot) return;
            var cached = GetSlotCache(slot);
            if (cached.HasLayout && (cached.Layout.ParentPtr != layout.ParentPtr ||
                cached.Layout.Vtable != layout.Vtable || !SameVector(cached.Layout.ChildrensPtr, layout.ChildrensPtr) ||
                !ReferenceEquals(cached.Parents, currentUiParentsObj)))
            {
                cached.Offset = -1;
                cached.ProbeAt = 0;
                cached.PointerFrame = 0;
                cached.ItemAddress = IntPtr.Zero;
                cached.Validated = false;
                equipmentSnapshots.Remove(slot);
                cached.Element = null;
            }
            // Observe empty nodes too, so same-address UI rebuilds cannot preserve a stale offset.
            bool geometryChanged = !cached.HasLayout || !SameSlotGeometry(cached.Layout, layout);
            cached.Layout = layout;
            cached.HasLayout = true;
            cached.Parents = currentUiParentsObj;
            if (geometryChanged) cached.Element = null;

            // Empty slots need neither a UI wrapper nor an Item object.
            if (GetItemAddressFromElement(slot) == IntPtr.Zero) return;
            if (cached.Element == null)
            {
                cached.Element = PluginUiElementReflection.CreateUiElement(slot, currentUiParentsObj) as UiElementBase;
                if (equipmentDiagnosticsEnabled) equipmentPerformance.UiElements++;
            }
            if (cached.Element == null) return;
            // Parent caches refresh each frame, so current screen coordinates stay independent of Mods.
            var position = cached.Element.Position;
            var size = cached.Element.Size;
            if (size.X <= 0 || position == Vector2.Zero) return;
            ProcessItemSlot(slot, position, size);
        }

        private static bool SameSlotGeometry(UiElementBaseOffset left, UiElementBaseOffset right) =>
            left.ParentPtr == right.ParentPtr && left.Flags == right.Flags && left.ScaleIndex == right.ScaleIndex &&
            left.LocalScaleMultiplier == right.LocalScaleMultiplier &&
            left.RelativePosition.X == right.RelativePosition.X && left.RelativePosition.Y == right.RelativePosition.Y &&
            left.UnscaledSize.X == right.UnscaledSize.X && left.UnscaledSize.Y == right.UnscaledSize.Y &&
            left.PositionModifier.X == right.PositionModifier.X && left.PositionModifier.Y == right.PositionModifier.Y;

        private static bool SameVector(StdVector left, StdVector right) =>
            left.First == right.First && left.Last == right.Last && left.End == right.End;

        private static bool SameItemIdentity(ItemStruct left, ItemStruct right) =>
            left.VTablePtr == right.VTablePtr && left.EntityDetailsPtr == right.EntityDetailsPtr &&
            SameVector(left.ComponentListPtr, right.ComponentListPtr);

        private void ProcessItemSlot(IntPtr slot, Vector2 position, Vector2 size)
        {
            var itemAddress = GetItemAddressFromElement(slot);
            if (itemAddress == IntPtr.Zero)
            {
                equipmentSnapshots.Remove(slot);
                return;
            }
            var cached = itemSlotOffsets[slot];
            string category = EquipmentMatcher.GetCategory(cached.Path);
            if (Settings.EnableEquipmentManager && !Settings.EnableDebugProbe && category != null)
            {
                if (!equipmentSnapshots.TryGetValue(slot, out var snapshot) ||
                    snapshot.ItemAddress != itemAddress || snapshot.Category != category ||
                    !SameItemIdentity(snapshot.Identity, cached.Identity))
                {
                    snapshot = new EquipmentSnapshot
                    {
                        Slot = slot, ItemAddress = itemAddress, Identity = cached.Identity, Category = category,
                        RefreshAt = equipmentNow,
                    };
                    equipmentSnapshots[slot] = snapshot;
                }
                snapshot.SeenFrame = equipmentFrame;
                snapshot.Position = position;
                snapshot.Size = size;
                if (snapshot.HasSnapshot && snapshot.RulesVersion != equipmentRulesVersion)
                {
                    if (snapshot.HasMods) MatchEquipmentSnapshot(snapshot);
                    else
                    {
                        snapshot.HasSnapshot = false;
                        snapshot.RefreshAt = equipmentNow;
                    }
                }
                if (equipmentNow >= snapshot.RefreshAt)
                {
                    if (snapshot.QueuedFrame != equipmentFrame)
                    {
                        snapshot.QueuedFrame = equipmentFrame;
                        equipmentRefreshQueue.Add(snapshot);
                    }
                }
                else DrawEquipmentSnapshot(snapshot);
                return;
            }

            // Preserve the original per-frame item evaluation for waystones, tablets, jewels and debug inspection.
            var item = ReadFreshItem(itemAddress);
            if (item == null || !item.IsValid) return;
            equipmentSnapshots.Remove(slot);
            EvaluateAndHighlightItem(item, position, size, slot);
        }

        private void DrawEquipmentSnapshot(EquipmentSnapshot snapshot)
        {
            if (!snapshot.HasSnapshot || snapshot.DrawnFrame == equipmentFrame ||
                equipmentNow - snapshot.ReadAt >= EquipmentMaximumSnapshotAgeMs) return;
            DrawEquipmentHighlight(snapshot.MatchedCount, snapshot.Position, snapshot.Size);
            snapshot.DrawnFrame = equipmentFrame;
        }

        private void RefreshPendingEquipment()
        {
            if (equipmentRefreshQueue.Count == 0) return;
            // Old deadlines eventually outrank new arrivals, preventing starvation under sustained changes.
            equipmentRefreshComparer ??= CompareEquipmentRefreshPriority;
            equipmentRefreshQueue.Sort(equipmentRefreshComparer);
            int budget = Math.Clamp((equipmentSnapshots.Count * equipmentFrameIntervalMs + 99) / 100, 8, 64);
            long started = Stopwatch.GetTimestamp();
            int refreshed = 0;
            foreach (var snapshot in equipmentRefreshQueue)
            {
                if (snapshot.SeenFrame != equipmentFrame ||
                    !equipmentSnapshots.TryGetValue(snapshot.Slot, out var current) || !ReferenceEquals(snapshot, current)) continue;
                if (refreshed < budget && (refreshed == 0 ||
                    Stopwatch.GetElapsedTime(started).TotalMilliseconds < EquipmentRefreshBudgetMs))
                {
                    RefreshEquipmentSnapshot(snapshot);
                    refreshed++;
                }
                else if (equipmentDiagnosticsEnabled) equipmentPerformance.Deferred++;
                DrawEquipmentSnapshot(snapshot);
            }
        }

        private int CompareEquipmentRefreshPriority(EquipmentSnapshot left, EquipmentSnapshot right)
        {
            int Priority(EquipmentSnapshot snapshot) => equipmentNow - snapshot.RefreshAt >= EquipmentRefreshIntervalMs
                ? 0 : !snapshot.HasSnapshot ? 1 : 2;
            int priority = Priority(left).CompareTo(Priority(right));
            return priority != 0 ? priority : left.RefreshAt.CompareTo(right.RefreshAt);
        }

        private void RefreshEquipmentSnapshot(EquipmentSnapshot snapshot)
        {
            if (equipmentDiagnosticsEnabled) equipmentPerformance.Refreshes++;
            if (!TryReadMemory<ItemStruct>(snapshot.ItemAddress, out var identity) ||
                !SameItemIdentity(snapshot.Identity, identity) ||
                !TryReadItemPath(snapshot.ItemAddress, out var path) || path != itemSlotOffsets[snapshot.Slot].Path)
            {
                FailEquipmentSnapshot(snapshot);
                itemSlotOffsets[snapshot.Slot].Validated = false;
                return;
            }
            if (GetEquipmentGroups(snapshot.Category).Length == 0)
            {
                snapshot.HasSnapshot = true;
                snapshot.HasMods = false;
                snapshot.MatchedCount = 0;
                snapshot.RulesVersion = equipmentRulesVersion;
                snapshot.ReadAt = equipmentNow;
                snapshot.RefreshAt = equipmentNow + EquipmentRefreshIntervalMs;
                return;
            }
            if (!TryReadVector(identity.ComponentListPtr, 50, out IntPtr[] components))
            {
                FailEquipmentSnapshot(snapshot);
                itemSlotOffsets[snapshot.Slot].Validated = false;
                return;
            }
            if (snapshot.Item == null || snapshot.Components == null ||
                !snapshot.Components.AsSpan().SequenceEqual(components))
            {
                snapshot.Item = ReadFreshItem(snapshot.ItemAddress);
                snapshot.Components = components;
            }
            if (snapshot.Item == null || !snapshot.Item.IsValid || snapshot.Item.Path != path ||
                !TryReadEquipmentMods(snapshot.Item, snapshot.PendingMods) ||
                !TryReadMemory<IntPtr>(snapshot.Slot + itemSlotOffsets[snapshot.Slot].Offset, out var itemAfter) ||
                itemAfter != snapshot.ItemAddress || !TryReadMemory<ItemStruct>(snapshot.ItemAddress, out var identityAfter) ||
                !SameItemIdentity(identity, identityAfter) ||
                !TryReadVector(identityAfter.ComponentListPtr, 50, out IntPtr[] componentsAfter) ||
                !components.AsSpan().SequenceEqual(componentsAfter))
            {
                FailEquipmentSnapshot(snapshot);
                return;
            }
            bool unchanged = snapshot.HasSnapshot && snapshot.RulesVersion == equipmentRulesVersion &&
                snapshot.Mods.SequenceEqual(snapshot.PendingMods);
            (snapshot.Mods, snapshot.PendingMods) = (snapshot.PendingMods, snapshot.Mods);
            snapshot.HasSnapshot = true;
            snapshot.HasMods = true;
            snapshot.ReadAt = equipmentNow;
            snapshot.RefreshAt = equipmentNow + EquipmentRefreshIntervalMs;
            if (!unchanged) MatchEquipmentSnapshot(snapshot);
        }

        private void FailEquipmentSnapshot(EquipmentSnapshot snapshot)
        {
            // A failed/torn read is pending validation, never a confirmed zero-match result.
            snapshot.HasSnapshot = false;
            snapshot.MatchedCount = 0;
            snapshot.Item = null;
            snapshot.RefreshAt = equipmentNow + 16;
            if (equipmentDiagnosticsEnabled) equipmentPerformance.ReadFailures++;
        }

        private void MatchEquipmentSnapshot(EquipmentSnapshot snapshot)
        {
            snapshot.MatchedCount = 0;
            foreach (var group in GetEquipmentGroups(snapshot.Category))
            {
                int count = EquipmentMatcher.CountMatches(group, snapshot.Mods);
                if (count >= group.MinMatchCount) snapshot.MatchedCount = Math.Max(snapshot.MatchedCount, count);
            }
            snapshot.RulesVersion = equipmentRulesVersion;
        }

        private int RefreshEquipmentMatch(Item item, string category, IntPtr slot)
        {
            // Debug inspection uses fresh Items and does not participate in scheduled snapshots.
            var snapshot = new EquipmentSnapshot { ItemAddress = item.Address, Category = category };
            if (!TryReadEquipmentMods(item, snapshot.Mods)) return 0;
            MatchEquipmentSnapshot(snapshot);
            return snapshot.MatchedCount;
        }

        private bool TryGetCachedItemAddress(IntPtr slot, out IntPtr address)
        {
            address = IntPtr.Zero;
            var cached = GetSlotCache(slot);
            if (cached.PointerFrame == equipmentFrame)
            {
                address = cached.ItemAddress;
                return true;
            }
            cached.PointerFrame = equipmentFrame;
            if (cached.Offset >= 0)
            {
                if (TryReadMemory<IntPtr>(slot + cached.Offset, out var candidate))
                {
                    if (candidate == IntPtr.Zero)
                    {
                        // Retain a proven offset through empty slots and poll it cheaply every frame.
                        cached.ItemAddress = IntPtr.Zero;
                        equipmentSnapshots.Remove(slot);
                        return equipmentNow < cached.ProbeAt;
                    }
                    if (cached.Validated && candidate == cached.ItemAddress &&
                        EquipmentMatcher.GetCategory(cached.Path) != null && !Settings.EnableDebugProbe &&
                        TryReadMemory<ItemStruct>(candidate, out var identity) && SameItemIdentity(identity, cached.Identity))
                    {
                        address = candidate;
                        return true;
                    }
                    if (TryValidateSlotCandidate(cached, candidate))
                    {
                        address = candidate;
                        return true;
                    }
                }
                cached.ItemAddress = IntPtr.Zero;
                cached.Validated = false;
                cached.Offset = -1;
                cached.ProbeAt = 0;
                equipmentSnapshots.Remove(slot);
            }
            cached.ItemAddress = IntPtr.Zero;
            return equipmentNow < cached.ProbeAt;
        }

        private bool TryValidateSlotCandidate(SlotOffsetCache cached, IntPtr candidate)
        {
            if (!IsReadableAddress(candidate) || !TryReadMemory<ItemStruct>(candidate, out var identity) ||
                !IsReadableAddress(identity.EntityDetailsPtr) || !TryReadItemPath(candidate, out var path)) return false;
            cached.ItemAddress = candidate;
            cached.Identity = identity;
            cached.Path = path;
            cached.Validated = true;
            return true;
        }

        private bool TryReadItemPath(IntPtr address, out string path)
        {
            path = null;
            if (equipmentDiagnosticsEnabled) equipmentPerformance.MetadataValidations++;
            if (!IsReadableAddress(address) || !TryReadMemory<IntPtr>(address + 0x08, out var details) ||
                !IsReadableAddress(details) || !TryReadMemory<StdWString>(details + 0x08, out var text)) return false;
            path = ReadStdWString(text);
            return path.StartsWith("Metadata/Items", StringComparison.Ordinal);
        }

        private bool TryReadEquipmentModName(IntPtr row, out string name)
        {
            name = null;
            if (!TryReadMemory<IntPtr>(row, out var pointer) || !IsReadableAddress(pointer)) return false;
            if (equipmentModNames.TryGetValue(row, out var cached) && cached.NamePointer == pointer)
            {
                name = cached.Name;
                return true;
            }
            var bytes = ReadStdVector<byte>(new StdVector { First = pointer, Last = pointer + 256 });
            if (bytes.Length != 256) return false;
            int length = 0;
            while (length < 256 && (bytes[length] != 0 || bytes[length + 1] != 0)) length += 2;
            if (length == 0 || length == 256) return false;
            name = System.Text.Encoding.Unicode.GetString(bytes, 0, length);
            equipmentModNames[row] = (pointer, name);
            return true;
        }
    }
}
