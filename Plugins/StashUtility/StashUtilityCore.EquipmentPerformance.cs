// <copyright file="StashUtilityCore.EquipmentPerformance.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace StashUtility
{
    using GameHelper;
    using GameHelper.RemoteEnums;
    using GameHelper.RemoteObjects.States.InGameStateObjects;
    using GameOffsets.Natives;
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Numerics;
    using System.Reflection;
    using System.Runtime.InteropServices;

    public sealed partial class StashUtilityCore
    {
        private const int EquipmentRefreshIntervalMs = 100;
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
        private PropertyInfo processHandleProperty;
        private object equipmentProcessHandle;
        private IntPtr equipmentAreaAddress;
        private string equipmentAreaHash;
        private int equipmentRulesVersion;
        private long equipmentFrame;

        private sealed class EquipmentSnapshot
        {
            internal IntPtr ItemAddress;
            internal string Category;
            internal long SeenFrame;
            internal long DrawnFrame;
            internal long RefreshAt;
            internal int RulesVersion;
            internal int MatchedCount;
            internal List<(string name, (float v0, float v1) vals)> Mods;
        }

        private sealed class SlotOffsetCache
        {
            internal int Offset = -1;
            internal long ProbeAt;
            internal long SeenFrame;
        }

        public override void DrawUI()
        {
            equipmentFrame++;
            bool completed = false;
            try
            {
                if (!RefreshEquipmentContext()) return;
                DrawOverlayFrame();
                completed = true;
            }
            finally
            {
                if (completed) PruneEquipmentCaches();
                else ClearEquipmentRuntimeCaches();
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
        }

        private void TrackEquipmentStashTab(IntPtr container, IntPtr tab, int index)
        {
            bool knownContainer = equipmentActiveTabs.TryGetValue(container, out var previous);
            if (knownContainer ? previous.Tab != tab || previous.Index != index : equipmentActiveTabs.Count > 0)
            {
                equipmentSnapshots.Clear();
                itemSlotOffsets.Clear();
            }
            equipmentActiveTabs[container] = (tab, index, equipmentFrame);
        }

        private void SaveEquipmentSettings()
        {
            equipmentRulesVersion++;
            equipmentCompiledGroups.Clear();
            SaveSettings();
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
            }
            return compiled;
        }

        private void ProcessItemSlot(IntPtr slot, Vector2 position, Vector2 size)
        {
            var itemAddress = GetItemAddressFromElement(slot);
            if (itemAddress == IntPtr.Zero)
            {
                equipmentSnapshots.Remove(slot);
                return;
            }

            if (Settings.EnableEquipmentManager && !Settings.EnableDebugProbe &&
                equipmentSnapshots.TryGetValue(slot, out var snapshot) && snapshot.ItemAddress == itemAddress &&
                snapshot.RulesVersion == equipmentRulesVersion && Environment.TickCount64 < snapshot.RefreshAt)
            {
                snapshot.SeenFrame = equipmentFrame;
                if (snapshot.DrawnFrame != equipmentFrame)
                {
                    DrawEquipmentHighlight(snapshot.MatchedCount, position, size);
                    snapshot.DrawnFrame = equipmentFrame;
                }
                return;
            }

            var item = ReadFreshItem(itemAddress);
            if (item == null || !item.IsValid)
            {
                equipmentSnapshots.Remove(slot);
                return;
            }
            if (EquipmentMatcher.GetCategory(item.Path) == null) equipmentSnapshots.Remove(slot);
            EvaluateAndHighlightItem(item, position, size, slot);
        }

        private int RefreshEquipmentMatch(Item item, string category, IntPtr slot)
        {
            var groups = GetEquipmentGroups(category);
            var mods = new List<(string name, (float v0, float v1) vals)>();
            if (groups.Length > 0 && !TryReadEquipmentMods(item, out mods))
            {
                equipmentSnapshots.Remove(slot);
                return 0;
            }

            equipmentSnapshots.TryGetValue(slot, out var snapshot);
            bool unchanged = snapshot != null && snapshot.ItemAddress == item.Address &&
                snapshot.Category == category && snapshot.RulesVersion == equipmentRulesVersion && snapshot.Mods.SequenceEqual(mods);
            int matchedCount = unchanged ? snapshot.MatchedCount : 0;
            if (!unchanged)
            {
                foreach (var group in groups)
                {
                    int count = EquipmentMatcher.CountMatches(group, mods);
                    if (count >= group.MinMatchCount) matchedCount = Math.Max(matchedCount, count);
                }
            }

            snapshot ??= new EquipmentSnapshot();
            snapshot.ItemAddress = item.Address;
            snapshot.Category = category;
            snapshot.SeenFrame = equipmentFrame;
            snapshot.DrawnFrame = equipmentFrame;
            snapshot.RefreshAt = Environment.TickCount64 + EquipmentRefreshIntervalMs;
            snapshot.RulesVersion = equipmentRulesVersion;
            snapshot.MatchedCount = matchedCount;
            snapshot.Mods = mods;
            equipmentSnapshots[slot] = snapshot;
            return matchedCount;
        }

        private bool TryGetCachedItemAddress(IntPtr slot, out IntPtr address)
        {
            address = IntPtr.Zero;
            if (!itemSlotOffsets.TryGetValue(slot, out var cached))
            {
                cached = new SlotOffsetCache();
                itemSlotOffsets[slot] = cached;
            }
            cached.SeenFrame = equipmentFrame;
            if (cached.Offset >= 0 && cached.Offset >= Settings.ScanStartOffset && cached.Offset + 8 <= Settings.ScanEndOffset)
            {
                if (TryReadMemory<IntPtr>(slot + cached.Offset, out address) && IsValidItemEntity(address)) return true;
                equipmentSnapshots.Remove(slot);
                cached.Offset = -1;
                cached.ProbeAt = 0;
            }
            address = IntPtr.Zero;
            return Environment.TickCount64 < cached.ProbeAt;
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
