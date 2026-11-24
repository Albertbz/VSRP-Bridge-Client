using Vintagestory.API.Client;
using Vintagestory.API.Server;
using Vintagestory.API.Config;
using Vintagestory.API.Common;
using Vintagestory.GameContent;
using HarmonyLib;
using ProtoBuf;
using System;

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
    private CharselModelRefresh? charselModelRefresh;
    private bool charselPending;
    private GuiDialogBaby? babyDialog;

    /// <summary>Server side: a player confirmed the baby dialog.</summary>
    public event Action<IServerPlayer, BabyChoicePacket>? BabyChoiceReceived;
    /// <summary>Server side: a player ran .baby to reopen the dialog of a pending birth.</summary>
    public event Action<IServerPlayer>? BabyDialogRequested;
    /// <summary>Server side: the baby dialog asks whether a name is free. Answer with SendBabyNameResult.</summary>
    public event Action<IServerPlayer, CheckBabyNamePacket>? BabyNameCheckRequested;

    // Called on server and client
    // Useful for registering block/entity classes on both sides
    public override void Start(ICoreAPI api)
    {
        api.Network.RegisterChannel(ChannelName)
            .RegisterMessageType<OpenCharselPacket>()
            .RegisterMessageType<OpenBabyDialogPacket>()
            .RegisterMessageType<BabyChoicePacket>()
            .RegisterMessageType<RequestBabyDialogPacket>()
            .RegisterMessageType<CheckBabyNamePacket>()
            .RegisterMessageType<BabyNameResultPacket>();
    }

    public override void StartServerSide(ICoreServerAPI api)
    {
        sapi = api;
        api.Network.GetChannel(ChannelName)
            .SetMessageHandler<BabyChoicePacket>((player, p) => BabyChoiceReceived?.Invoke(player, p))
            .SetMessageHandler<RequestBabyDialogPacket>((player, _) => BabyDialogRequested?.Invoke(player))
            .SetMessageHandler<CheckBabyNamePacket>((player, p) => BabyNameCheckRequested?.Invoke(player, p));

        harmony = new Harmony(Mod.Info.ModID);
        TabletopBabyGuard.Patch(harmony, api);
    }

    public override void StartClientSide(ICoreClientAPI api)
    {
        capi = api;
        api.Network.GetChannel(ChannelName)
            .SetMessageHandler<OpenCharselPacket>(OnOpenCharsel)
            .SetMessageHandler<OpenBabyDialogPacket>(OnOpenBabyDialog)
            .SetMessageHandler<BabyNameResultPacket>(p =>
            {
                if (babyDialog?.OffspringId == p.OffspringId) babyDialog.OnNameResult(p);
            });

        api.ChatCommands.Create("baby")
            .WithDescription(Lang.Get("vsrpbridgeclient:baby-command"))
            .HandleWith(_ =>
            {
                api.Network.GetChannel(ChannelName).SendPacket(new RequestBabyDialogPacket());
                // No message at all: client commands print an empty one as a blank chat line. The server
                // answers by opening the dialog, or says there is no newborn waiting
                return new TextCommandResult { Status = EnumCommandStatus.Success };
            });

        harmony = new Harmony(Mod.Info.ModID);
        harmony.Patch(
            AccessTools.Method(typeof(CharacterSystem), "onCharSelCmd"),
            prefix: new HarmonyMethod(typeof(CharselCachedDialogPatch), nameof(CharselCachedDialogPatch.Prefix)));

        TabletopBabyGuard.Patch(harmony, api);
        woundTraits = new WoundTraits(api, harmony);
        charselModelRefresh = new CharselModelRefresh(api);
    }

    public override void Dispose()
    {
        charselModelRefresh?.Dispose();
        woundTraits?.Dispose();
        harmony?.UnpatchAll(Mod.Info.ModID);
        base.Dispose();
    }


    public void OpenCharsel(IServerPlayer player)
    {
        sapi?.Network.GetChannel(ChannelName).SendPacket(new OpenCharselPacket(), player);
    }

    /// <summary>Opens the baby dialog for the player, see GuiDialogBaby.</summary>
    public void OpenBabyDialog(IServerPlayer player, OpenBabyDialogPacket packet)
    {
        sapi?.Network.GetChannel(ChannelName).SendPacket(packet, player);
    }

    /// <summary>Sends the bot's answer about a name to the player's baby dialog.</summary>
    public void SendBabyNameResult(IServerPlayer player, BabyNameResultPacket packet)
    {
        sapi?.Network.GetChannel(ChannelName).SendPacket(packet, player);
    }

    private void OnOpenBabyDialog(OpenBabyDialogPacket p)
    {
        if (capi == null) return;
        // The same birth sent again (.baby, or on join) only needs bringing back, a new one replaces it
        if (babyDialog?.IsOpened() == true)
        {
            if (babyDialog.OffspringId == p.OffspringId) return;
            babyDialog.TryClose();
        }

        var channel = capi.Network.GetChannel(ChannelName);
        babyDialog = new GuiDialogBaby(capi, p,
            choice => channel.SendPacket(choice),
            name => channel.SendPacket(new CheckBabyNamePacket { OffspringId = p.OffspringId, Name = name }));
        babyDialog.TryOpen();
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
