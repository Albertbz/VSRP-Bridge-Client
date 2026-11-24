using System;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;

namespace VSRPBridgeClient;

/// <summary>
/// Keeps a newborn in its Tabletop Games piece, on its own. The piece's Exchange and Remove tool modes take its
/// chiseled block back out, and Up Add and Down Add stack other blocks on it, so they are refused for a piece
/// stamped with an offspring id (BabyStackBuilder.OffspringIdAttribute, on the piece or its block). Its other
/// modes still work, such as scaling, rotating, and taking off blocks stacked before this.
/// Patched on both sides, as tool modes run on the client and then on the server. Does nothing without Tabletop Games.
/// </summary>
public static class TabletopBabyGuard
{
    private const string ToolModesType = "TabletopGames.CollectibleBehaviorChiseledPieceToolModes";
    private const string ContentsAttribute = "containedChiseledStacks";
    private const string MainBlockKey = "0,0,0";
    // EnumMode.Exchange, Remove, UpAdd and DownAdd of CollectibleBehaviorChiseledPieceToolModes
    private const int ExchangeMode = 0;
    private const int RemoveMode = 1;
    private const int UpAddMode = 2;
    private const int DownAddMode = 4;

    public static void Patch(Harmony harmony, ICoreAPI api)
    {
        Type? type = AccessTools.TypeByName(ToolModesType);
        if (type == null) return;
        var method = AccessTools.Method(type, "SetToolMode");
        if (method == null)
        {
            api.Logger.Warning("[VSRPBridgeClient] Tabletop Games has no SetToolMode to patch, so newborns can be taken out of their pieces.");
            return;
        }
        harmony.Patch(method, prefix: new HarmonyMethod(typeof(TabletopBabyGuard), nameof(Prefix)));
    }

    /// <summary>Whether the stack is a tabletop piece holding a newborn.</summary>
    public static bool IsBabyPiece(ItemStack? piece)
    {
        if (piece == null) return false;
        if (!string.IsNullOrEmpty(piece.Attributes.GetString(BabyStackBuilder.OffspringIdAttribute))) return true;
        ItemStack? main = piece.Attributes.GetTreeAttribute(ContentsAttribute)?.GetItemstack(MainBlockKey);
        return !string.IsNullOrEmpty(main?.Attributes.GetString(BabyStackBuilder.OffspringIdAttribute));
    }

    public static bool Prefix(ItemSlot slot, IPlayer byPlayer, int index)
    {
        bool takesOut = index == ExchangeMode || index == RemoveMode;
        bool stacks = index == UpAddMode || index == DownAddMode;
        if (!takesOut && !stacks) return true;
        if (!IsBabyPiece(slot?.Itemstack)) return true;
        if (byPlayer?.Entity?.Api is ICoreClientAPI capi)
        {
            string key = takesOut ? "baby-piece-locked" : "baby-piece-no-stacking";
            capi.TriggerIngameError(typeof(TabletopBabyGuard), "vsrpbridgeclient-" + key, Lang.Get("vsrpbridgeclient:" + key));
        }
        return false;
    }
}
