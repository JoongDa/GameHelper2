// <copyright file="StashUtilityCore.Equipment.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace StashUtility
{
    using GameHelper;
    using GameHelper.Plugin;
    using GameHelper.RemoteObjects.Components;
    using GameHelper.RemoteObjects.States.InGameStateObjects;
    using GameHelper.Utils;
    using GameOffsets.Natives;
    using GameOffsets.Objects.Components;
    using GameOffsets.Objects.States.InGameState;
    using ImGuiNET;
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Linq;
    using System.Numerics;
    using System.Reflection;
    using System.Runtime.InteropServices;
    using System.Text;

    public sealed partial class StashUtilityCore
    {
        private const int EquipmentMaximumExplicitMods = 32;
        private const int EquipmentMaximumRawValues = 16;
        private const int EquipmentMaximumModIdLength = 256;
        private static readonly FieldInfo EquipmentComponentAddressesField = typeof(Entity).GetField(
            "componentAddresses", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly PropertyInfo EquipmentProcessHandleProperty = typeof(GameProcess).GetProperty(
            "Handle", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

        private readonly Dictionary<IntPtr, EquipmentItemSnapshot> equipmentFrameSnapshots = new();
        private readonly HashSet<IntPtr> equipmentFrameDrawnItems = new();
        private readonly Dictionary<IntPtr, (IntPtr Pointer, string Id)> equipmentModIds = new();
        private object equipmentProcessHandle;
        private IntPtr equipmentAreaAddress;
        private string equipmentAreaHash;
        private EquipmentItemSnapshot lastHoveredEquipmentSnapshot;
        private bool freezeHoveredEquipment;

        private sealed class EquipmentItemSnapshot
        {
            public string Category;
            public string BaseName;
            public string InternalName;
            public string Path;
            public IntPtr ItemAddress;
            public List<EquipmentRawMod> Mods = new();
        }

        private sealed class EquipmentRawMod
        {
            public string ModId;
            public int[] RawValues = Array.Empty<int>();
        }

        private void BeginEquipmentFrame()
        {
            // Only stable data-row names survive a frame; item values and matches never do.
            this.equipmentFrameSnapshots.Clear();
            this.equipmentFrameDrawnItems.Clear();
            if (this.Settings.Equipment?.Enabled != true && !this.Settings.EnableDebugProbe) return;
            try
            {
                var currentHandle = EquipmentProcessHandleProperty?.GetValue(Core.Process);
                if (!ReferenceEquals(this.equipmentProcessHandle, currentHandle))
                {
                    this.equipmentProcessHandle = currentHandle;
                    this.equipmentModIds.Clear();
                    this.ClearEquipmentDebugSnapshot();
                    if (currentHandle == null)
                    {
                        this.handleObj = null;
                    }
                    else
                    {
                        // The shared scanner must also stop using the previous process handle.
                        this.InitReflection();
                    }
                }

                var area = Core.States.InGameStateObject.CurrentAreaInstance;
                var areaAddress = area?.Address ?? IntPtr.Zero;
                var areaHash = area?.AreaHash;
                if (this.equipmentAreaAddress != areaAddress || !string.Equals(this.equipmentAreaHash, areaHash, StringComparison.Ordinal))
                {
                    this.equipmentAreaAddress = areaAddress;
                    this.equipmentAreaHash = areaHash;
                    this.equipmentModIds.Clear();
                    this.ClearEquipmentDebugSnapshot();
                }
            }
            catch
            {
                this.equipmentProcessHandle = null;
                this.equipmentModIds.Clear();
                this.ClearEquipmentDebugSnapshot();
            }
        }

        private void ResetEquipmentState()
        {
            this.equipmentFrameSnapshots.Clear();
            this.equipmentFrameDrawnItems.Clear();
            this.equipmentModIds.Clear();
            this.equipmentProcessHandle = null;
            this.equipmentAreaAddress = IntPtr.Zero;
            this.equipmentAreaHash = null;
            this.freezeHoveredEquipment = false;
            this.ClearEquipmentDebugSnapshot();
            this.ClearEquipmentUiState();
        }

        private void ClearEquipmentDebugSnapshot()
        {
            this.lastHoveredEquipmentSnapshot = null;
        }

        private static bool TryGetEquipmentCategory(string path, out string category)
        {
            category = null;
            if (string.IsNullOrEmpty(path)) return false;

            // Match metadata families explicitly; currency, charms, maps and jewels are excluded.
            if (path.StartsWith("Metadata/Items/Weapons/", StringComparison.Ordinal)) category = "Weapons";
            else if (path.StartsWith("Metadata/Items/Armours/Helmets/", StringComparison.Ordinal)) category = "Helmets";
            else if (path.StartsWith("Metadata/Items/Armours/BodyArmours/", StringComparison.Ordinal)) category = "BodyArmours";
            else if (path.StartsWith("Metadata/Items/Armours/Gloves/", StringComparison.Ordinal)) category = "Gloves";
            else if (path.StartsWith("Metadata/Items/Armours/Boots/", StringComparison.Ordinal)) category = "Boots";
            else if (path.StartsWith("Metadata/Items/Armours/Shields/", StringComparison.Ordinal)) category = "Shields";
            else if (path.StartsWith("Metadata/Items/Rings/", StringComparison.Ordinal)) category = "Rings";
            else if (path.StartsWith("Metadata/Items/Amulets/", StringComparison.Ordinal)) category = "Amulets";
            else if (path.StartsWith("Metadata/Items/Belts/", StringComparison.Ordinal)) category = "Belts";
            else if (path.StartsWith("Metadata/Items/Quivers/", StringComparison.Ordinal) ||
                     path.StartsWith("Metadata/Items/Armours/Quivers/", StringComparison.Ordinal)) category = "Quivers";
            else if (path.StartsWith("Metadata/Items/Armours/Focii/", StringComparison.Ordinal) ||
                     path.StartsWith("Metadata/Items/Armours/Foci/", StringComparison.Ordinal) ||
                     path.StartsWith("Metadata/Items/Armours/Focus/", StringComparison.Ordinal) ||
                     path.StartsWith("Metadata/Items/Foci/", StringComparison.Ordinal) ||
                     path.StartsWith("Metadata/Items/Focus/", StringComparison.Ordinal)) category = "Focus";
            return category != null;
        }

        private void ProcessEquipmentItem(Item item, Vector2 position, Vector2 size)
        {
            if (item == null || !item.IsValid || this.Settings.Equipment == null ||
                (!this.Settings.Equipment.Enabled && !this.Settings.EnableDebugProbe) ||
                !TryGetEquipmentCategory(item.Path, out var category)) return;

            var mouse = ImGui.GetMousePos();
            var captureHovered = this.Settings.EnableDebugProbe && !this.freezeHoveredEquipment &&
                mouse.X >= position.X && mouse.Y >= position.Y &&
                mouse.X <= position.X + size.X && mouse.Y <= position.Y + size.Y;
            var hasGroups = this.Settings.Equipment.Enabled &&
                this.Settings.Equipment.CategoryGroups != null &&
                this.Settings.Equipment.CategoryGroups.TryGetValue(category, out var groups) &&
                groups != null && groups.Count > 0;
            if (!captureHovered && !hasGroups) return;

            if (!this.equipmentFrameSnapshots.TryGetValue(item.Address, out var snapshot))
            {
                this.TryReadEquipmentSnapshot(item, category, out snapshot);
                this.equipmentFrameSnapshots[item.Address] = snapshot;
            }

            if (captureHovered)
            {
                // A failed refresh removes the old sample instead of presenting stale success.
                this.lastHoveredEquipmentSnapshot = snapshot;
            }

            if (snapshot == null || !hasGroups ||
                !this.EvaluateEquipmentGroups(snapshot, out _, out _)) return;

            var thickness = this.Settings.Equipment.BorderThickness;
            var borderMargin = this.Settings.Equipment.BorderMargin;
            if (!float.IsFinite(thickness) || !float.IsFinite(borderMargin) ||
                !float.IsFinite(position.X) || !float.IsFinite(position.Y) ||
                !float.IsFinite(size.X) || !float.IsFinite(size.Y) || size.X <= 0 || size.Y <= 0) return;
            thickness = Math.Clamp(thickness, 0.5f, 20f);
            var margin = Math.Clamp(borderMargin, 0f, 20f) * (size.X / 52f);
            var inset = Math.Min(margin + thickness / 2f, Math.Min(size.X, size.Y) / 2f - 0.5f);
            if (inset < 0f || !this.equipmentFrameDrawnItems.Add(snapshot.ItemAddress)) return;

            this.AddStyledRect(ImGui.GetBackgroundDrawList(), position + new Vector2(inset),
                position + size - new Vector2(inset), ImGuiHelper.Color(this.Settings.Equipment.HighlightColor),
                thickness, Math.Clamp(this.Settings.Equipment.FrameStyle, 0, 2));
        }

        private bool EvaluateEquipmentGroups(EquipmentItemSnapshot snapshot, out string matchedGroupName, out int matchedCount)
        {
            matchedGroupName = null;
            matchedCount = 0;
            if (snapshot == null || this.Settings.Equipment?.CategoryGroups == null ||
                !this.Settings.Equipment.CategoryGroups.TryGetValue(snapshot.Category, out var groups) || groups == null) return false;

            foreach (var group in groups)
            {
                if (group?.ModPatternIds == null || group.MinMatchCount < 1) continue;
                var selectedIds = new HashSet<string>(group.ModPatternIds.Where(id => !string.IsNullOrWhiteSpace(id)), StringComparer.Ordinal);
                if (selectedIds.Count == 0 || group.MinMatchCount > selectedIds.Count) continue;
                var hits = new HashSet<string>(StringComparer.Ordinal);
                foreach (var mod in snapshot.Mods)
                {
                    if (!selectedIds.Contains(mod.ModId) || hits.Contains(mod.ModId)) continue;
                    if (group.ModRequiredMinRolls != null && group.ModRequiredMinRolls.TryGetValue(mod.ModId, out var minimum0) &&
                        (!float.IsFinite(minimum0) || mod.RawValues.Length < 1 || mod.RawValues[0] < (double)minimum0)) continue;
                    if (group.ModRequiredMinRollsValue1 != null && group.ModRequiredMinRollsValue1.TryGetValue(mod.ModId, out var minimum1) &&
                        (!float.IsFinite(minimum1) || mod.RawValues.Length < 2 || mod.RawValues[1] < (double)minimum1)) continue;
                    hits.Add(mod.ModId);
                }

                if (hits.Count >= group.MinMatchCount)
                {
                    matchedGroupName = group.Name;
                    matchedCount = hits.Count;
                    return true;
                }
            }

            return false;
        }

        private bool TryReadEquipmentSnapshot(Item item, string category, out EquipmentItemSnapshot snapshot)
        {
            snapshot = null;
            if (this.equipmentProcessHandle == null || !ReferenceEquals(this.handleObj, this.equipmentProcessHandle)) return false;
            try
            {
                if (!PluginUiElementReflection.TryValidateItemAddress(item.Address, out var path, out _) ||
                    !string.Equals(path, item.Path, StringComparison.Ordinal) ||
                    !TryGetEquipmentCategory(path, out var currentCategory) || !string.Equals(currentCategory, category, StringComparison.Ordinal) ||
                    !this.TryReadMemory<ItemStruct>(item.Address, out var identityBefore) ||
                    !item.TryGetComponent<Base>(out var baseComponent) || !baseComponent.IsParentValid(item.Address) ||
                    EquipmentComponentAddressesField?.GetValue(item) is not ConcurrentDictionary<string, IntPtr> components ||
                    !components.TryGetValue("Mods", out var modsAddress) || !IsEquipmentAddress(modsAddress)) return false;

                // A bounded double read catches in-place crafting even when all pointers stay equal.
                for (var attempt = 0; attempt < 2; attempt++)
                {
                    if (!this.TryReadEquipmentExplicitMods(modsAddress, item.Address, out var first) ||
                        !this.TryReadEquipmentExplicitMods(modsAddress, item.Address, out var second)) continue;
                    if (!EquipmentModsEqual(first, second)) continue;
                    if (!this.TryReadMemory<ItemStruct>(item.Address, out var identityAfter) ||
                        identityBefore.VTablePtr != identityAfter.VTablePtr ||
                        identityBefore.EntityDetailsPtr != identityAfter.EntityDetailsPtr ||
                        !EquipmentVectorsEqual(identityBefore.ComponentListPtr, identityAfter.ComponentListPtr) ||
                        !this.TryReadMemory<ModsOffsets>(modsAddress, out var ownerAfter) ||
                        ownerAfter.Header.EntityPtr != item.Address) return false;

                    snapshot = new EquipmentItemSnapshot
                    {
                        Category = category,
                        BaseName = baseComponent.BaseItemName,
                        InternalName = baseComponent.InternalName,
                        Path = path,
                        ItemAddress = item.Address,
                        Mods = second
                    };
                    return true;
                }
            }
            catch
            {
                // Live UI and item containers can disappear between any two reads.
            }

            return false;
        }

        private bool TryReadEquipmentExplicitMods(IntPtr modsAddress, IntPtr owner, out List<EquipmentRawMod> mods)
        {
            mods = null;
            if (!this.TryReadMemory<ModsOffsets>(modsAddress, out var before) || before.Header.EntityPtr != owner ||
                !TryGetEquipmentVectorCount(before.Details0.Mods.ExplicitMods, Marshal.SizeOf<ModArrayStruct>(),
                    EquipmentMaximumExplicitMods, out var count)) return false;

            var result = new List<EquipmentRawMod>(count);
            for (var index = 0; index < count; index++)
            {
                var address = before.Details0.Mods.ExplicitMods.First + index * Marshal.SizeOf<ModArrayStruct>();
                if (!this.TryReadMemory<ModArrayStruct>(address, out var mod) ||
                    !this.TryReadEquipmentModId(mod.ModsPtr, out var id) ||
                    !this.TryReadEquipmentRawValues(mod, out var values)) return false;
                result.Add(new EquipmentRawMod { ModId = id, RawValues = values });
            }

            if (!this.TryReadMemory<ModsOffsets>(modsAddress, out var after) || after.Header.EntityPtr != owner ||
                before.Details0.Rarity != after.Details0.Rarity ||
                !EquipmentVectorsEqual(before.Details0.Mods.ExplicitMods, after.Details0.Mods.ExplicitMods)) return false;
            mods = result;
            return true;
        }

        private bool TryReadEquipmentRawValues(ModArrayStruct mod, out int[] values)
        {
            values = null;
            if (!TryGetEquipmentVectorCount(mod.Values, sizeof(int), EquipmentMaximumRawValues, out var count)) return false;
            values = new int[count];
            if (count == 1)
            {
                // The current game layout stores a single value inline, after the vector header.
                values[0] = mod.Value0;
            }
            else
            {
                for (var index = 0; index < count; index++)
                {
                    if (!this.TryReadMemory<int>(mod.Values.First + index * sizeof(int), out values[index]))
                    {
                        values = null;
                        return false;
                    }
                }
            }

            return true;
        }

        private bool TryReadEquipmentModId(IntPtr rowAddress, out string id)
        {
            id = null;
            if (!IsEquipmentAddress(rowAddress) || !this.TryReadMemory<IntPtr>(rowAddress, out var stringPointer) ||
                !IsEquipmentAddress(stringPointer)) return false;
            if (this.equipmentModIds.TryGetValue(rowAddress, out var cached) && cached.Pointer == stringPointer)
            {
                id = cached.Id;
                return true;
            }

            var text = new StringBuilder();
            for (var index = 0; index < EquipmentMaximumModIdLength; index++)
            {
                if (!this.TryReadMemory<ushort>(stringPointer + index * sizeof(ushort), out var value)) return false;
                if (value == 0)
                {
                    if (text.Length == 0 || !this.TryReadMemory<IntPtr>(rowAddress, out var pointerAfter) || pointerAfter != stringPointer) return false;
                    id = text.ToString();
                    if (this.equipmentModIds.Count >= 8192) this.equipmentModIds.Clear();
                    this.equipmentModIds[rowAddress] = (stringPointer, id);
                    return true;
                }

                if (char.IsControl((char)value)) return false;
                text.Append((char)value);
            }

            return false;
        }

        private static bool TryGetEquipmentVectorCount(StdVector vector, int stride, int maximumCount, out int count)
        {
            count = 0;
            var first = vector.First.ToInt64();
            var last = vector.Last.ToInt64();
            var end = vector.End.ToInt64();
            if (first == 0 && last == 0 && end == 0) return true;
            if (!IsEquipmentAddress(vector.First) || last < first || end < last || end > 0x7FFFFFFFFFFF) return false;
            var bytes = last - first;
            if (bytes % stride != 0 || bytes / stride > maximumCount) return false;
            count = (int)(bytes / stride);
            return true;
        }

        private static bool IsEquipmentAddress(IntPtr pointer)
        {
            var address = pointer.ToInt64();
            return address >= 0x10000 && address <= 0x7FFFFFFFFFFF;
        }

        private static bool EquipmentVectorsEqual(StdVector left, StdVector right) =>
            left.First == right.First && left.Last == right.Last && left.End == right.End;

        private static bool EquipmentModsEqual(List<EquipmentRawMod> left, List<EquipmentRawMod> right)
        {
            if (left.Count != right.Count) return false;
            for (var index = 0; index < left.Count; index++)
            {
                if (!string.Equals(left[index].ModId, right[index].ModId, StringComparison.Ordinal) ||
                    !left[index].RawValues.SequenceEqual(right[index].RawValues)) return false;
            }

            return true;
        }
    }
}
