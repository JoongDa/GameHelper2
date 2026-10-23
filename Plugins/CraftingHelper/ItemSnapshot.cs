namespace CraftingHelper;

using System;
using System.Collections.Generic;
using System.Numerics;
using GameHelper.RemoteEnums;

/// <summary>A copied mod value; no mutable Core component list escapes the adapter.</summary>
public sealed record ItemModSnapshot(string Id, float Value0, float Value1);

/// <summary>A local observation, not an acknowledgement of a crafting operation.</summary>
public sealed record ItemSnapshot
{
    public IntPtr SlotAddress { get; init; }
    public IntPtr ItemAddress { get; init; }
    public Vector2 Position { get; init; }
    public Vector2 Size { get; init; }
    public string BaseId { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string Path { get; init; } = string.Empty;
    public Rarity Rarity { get; init; }
    public IReadOnlyList<ItemModSnapshot> ExplicitMods { get; init; } = Array.Empty<ItemModSnapshot>();
    public string Signature { get; init; } = string.Empty;
    public int StackCount { get; init; }
    public bool IsEquipment { get; init; }
    public bool IsCurrency { get; init; }
    public long SampleSequence { get; init; }
    public string ContextKey { get; init; } = string.Empty;

    public bool Contains(Vector2 point) =>
        point.X >= this.Position.X && point.Y >= this.Position.Y &&
        point.X < this.Position.X + this.Size.X && point.Y < this.Position.Y + this.Size.Y;

    public bool SameIdentity(ItemSnapshot? other) => other != null &&
        this.ItemAddress != IntPtr.Zero && this.ItemAddress == other.ItemAddress &&
        this.SlotAddress == other.SlotAddress &&
        string.Equals(this.ContextKey, other.ContextKey, StringComparison.Ordinal) &&
        string.Equals(this.BaseId, other.BaseId, StringComparison.Ordinal) &&
        string.Equals(this.Path, other.Path, StringComparison.Ordinal);
}
