using Vintagestory.API.Client;
using Vintagestory.API.Server;
using Vintagestory.API.Config;
using Vintagestory.API.Common;
using Vintagestory.GameContent;
using HarmonyLib;
using ProtoBuf;

namespace VSRPBridgeClient;

[ProtoContract(ImplicitFields = ImplicitFields.AllPublic)]
public class OpenCharselPacket
{
}

public class VSRPBridgeClientModSystem : ModSystem
{
    private const string ChannelName = "vsrpbridgeclient:channel";

    // How long to wait for the server's allowcharselonce grant to sync before opening anyway
    private const int CharselWaitMs = 5000;
    private const int CharselPollMs = 100;

    private ICoreServerAPI? sapi;
    private ICoreClientAPI? capi;
    private Harmony? harmony;
    private WoundTraits? woundTraits;
    private bool charselPending;

    // Called on server and client
    // Useful for registering block/entity classes on both sides
    public override void Start(ICoreAPI api)
    {
        api.Network.RegisterChannel(ChannelName)
            .RegisterMessageType<OpenCharselPacket>();
    }

    public override void StartServerSide(ICoreServerAPI api)
    {
        sapi = api;
    }

    public override void StartClientSide(ICoreClientAPI api)
    {
        capi = api;
        api.Network.GetChannel(ChannelName)
            .SetMessageHandler<OpenCharselPacket>(OnOpenCharsel);

        harmony = new Harmony(Mod.Info.ModID);
        harmony.Patch(
            AccessTools.Method(typeof(CharacterSystem), "onCharSelCmd"),
            prefix: new HarmonyMethod(typeof(CharselCachedDialogPatch), nameof(CharselCachedDialogPatch.Prefix)));

        woundTraits = new WoundTraits(api, harmony);
    }

    public override void Dispose()
    {
        woundTraits?.Dispose();
        harmony?.UnpatchAll(Mod.Info.ModID);
        base.Dispose();
    }


    public void OpenCharsel(IServerPlayer player)
    {
        sapi?.Network.GetChannel(ChannelName).SendPacket(new OpenCharselPacket(), player);
    }

    private void OnOpenCharsel(OpenCharselPacket p)
    {
        if (capi == null || charselPending) return;
        charselPending = true;
        TryOpenCharsel(CharselWaitMs);
    }

    // The packet usually arrives before the allowcharselonce watched attribute has synced,
    // and /charsel refuses to open without it, so wait for the attribute first.
    private void TryOpenCharsel(int msLeft)
    {
        bool allowed = capi!.World.Player?.Entity?.WatchedAttributes.GetBool("allowcharselonce") == true;
        if (!allowed && msLeft > 0)
        {
            capi.Event.RegisterCallback(_ => TryOpenCharsel(msLeft - CharselPollMs), CharselPollMs);
            return;
        }

        charselPending = false;

        // Go through the /charsel command instead of creating GuiDialogCreateCharacter ourselves:
        // mods like PlayerModelLib patch the command to open their own dialog
        IClientPlayer? player = capi.World.Player;
        if (player == null) return;

        capi.ChatCommands.Execute("charsel", new TextCommandCallingArgs
        {
            Caller = new Caller
            {
                Player = player,
                FromChatGroupId = GlobalConstants.GeneralChatGroup,
                CallerPrivileges = new[] { "*" }
            },
            RawArgs = new CmdArgs("")
        }, result =>
        {
            if (!string.IsNullOrEmpty(result.StatusMessage)) player.ShowChatNotification(Lang.Get(result.StatusMessage));
        });
    }
}

/// <summary>
/// .charsel only checks allowcharselonce while CharacterSystem has no stored dialog. Vanilla clears it in
/// ClientSelectionDone, but PlayerModelLib's dialog does not, so .charsel kept reopening it without permission.
/// Dropping a closed stored dialog before the command runs makes it check permission every time.
/// </summary>
public static class CharselCachedDialogPatch
{
    private static readonly AccessTools.FieldRef<CharacterSystem, GuiDialogCreateCharacter?> createCharDlg =
        AccessTools.FieldRefAccess<CharacterSystem, GuiDialogCreateCharacter?>("createCharDlg");

    public static void Prefix(CharacterSystem __instance)
    {
        GuiDialogCreateCharacter? dlg = createCharDlg(__instance);
        if (dlg != null && !dlg.IsOpened())
        {
            createCharDlg(__instance) = null;
        }
    }
}
