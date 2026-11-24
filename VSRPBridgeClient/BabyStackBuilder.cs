using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;

namespace VSRPBridgeClient;

/// <summary>
/// Builds the chiseled block of a baby. The server uses it for the block the player gets, and the dialog for
/// its previews, so a preview always looks the same as the block given.
/// </summary>
public static class BabyStackBuilder
{
    public const string ChiseledBlockCode = "game:chiseledblock";
    public const int MaxNameLength = 40;
    /// <summary>The bot's id of the offspring a baby block or tabletop piece is of, so it can be renamed later.</summary>
    public const string OffspringIdAttribute = "vsrpOffspringId";

    // "rotation" holds rotationY + 360 shifted left by 10, so this is no rotation
    private const int NoRotation = 360 << 10;
    // A full block of voxels, the amount vanilla gives each material of a new chiseled block
    private const int FullMaterialQuantity = 4096;

    /// <summary>
    /// The block code each material index of the shape gets: the chosen block of its slot, or the material the
    /// shape was captured with when the slot has no choice.
    /// </summary>
    public static string[] ResolveMaterials(BabyShape shape, IReadOnlyDictionary<string, string> choices)
    {
        var codes = new string[shape.FallbackMaterials.Length];
        for (int i = 0; i < codes.Length; i++)
        {
            string? slot = i < shape.SlotByMaterialIndex.Length ? shape.SlotByMaterialIndex[i] : null;
            codes[i] = slot != null && choices.TryGetValue(slot, out var chosen) && !string.IsNullOrEmpty(chosen)
                ? chosen
                : shape.FallbackMaterials[i];
        }
        return codes;
    }

    /// <summary>Trims the name to one line of at most MaxNameLength characters.</summary>
    public static string CleanName(string? name)
    {
        string clean = (name ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();
        return clean.Length > MaxNameLength ? clean.Substring(0, MaxNameLength).TrimEnd() : clean;
    }

    /// <summary>
    /// The lang key of the dialog's line for a gender as the bot sends it ("Man" or "Woman", any case).
    /// Null for no or any other gender.
    /// </summary>
    public static string? GenderKey(string? gender) => gender?.Trim().ToLowerInvariant() switch
    {
        "man" => "vsrpbridgeclient:baby-gender-boy",
        "woman" => "vsrpbridgeclient:baby-gender-girl",
        _ => null
    };

    /// <summary>
    /// The chiseled block for the shape with the chosen materials, named name (the default chiseled block name
    /// when empty), stamped with offspringId when given. Null when the chiseled block or one of the materials
    /// doesn't exist.
    /// </summary>
    public static ItemStack? Build(IWorldAccessor world, BabyShape shape, IReadOnlyDictionary<string, string> choices, string? name, string? offspringId = null)
    {
        Block? chisel = world.GetBlock(new AssetLocation(ChiseledBlockCode));
        if (chisel == null || shape.FallbackMaterials.Length == 0) return null;

        string[] codes = ResolveMaterials(shape, choices);
        var ids = new int[codes.Length];
        for (int i = 0; i < codes.Length; i++)
        {
            Block? block = world.GetBlock(new AssetLocation(codes[i]));
            if (block == null || block.Id == 0) return null;
            ids[i] = block.Id;
        }

        var quantities = new int[ids.Length];
        for (int i = 0; i < quantities.Length; i++) quantities[i] = FullMaterialQuantity;

        var stack = new ItemStack(chisel, 1);
        // Block ids rather than codes: some vanilla code only reads ids, and a stack's ids are remapped if
        // they change between game versions, like those of any other chiseled block
        stack.Attributes["materials"] = new IntArrayAttribute(ids);
        stack.Attributes["cuboids"] = new IntArrayAttribute((int[])shape.Cuboids.Clone());
        stack.Attributes["availMaterialQuantities"] = new IntArrayAttribute(quantities);
        stack.Attributes.SetInt("rotation", NoRotation);
        stack.Attributes.SetInt("decorRot", 0);
        // Always set, since a missing name falls back to the material's name but a null one would throw.
        // Side solidity is left out, so the server works it out when the block is placed
        stack.Attributes.SetString("blockName", CleanName(name));
        if (!string.IsNullOrEmpty(offspringId)) stack.Attributes.SetString(OffspringIdAttribute, offspringId);
        return stack;
    }
}
