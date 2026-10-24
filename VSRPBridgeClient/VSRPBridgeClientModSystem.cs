using Vintagestory.API.Client;
using Vintagestory.API.Server;
using Vintagestory.API.Config;
using Vintagestory.API.Common;
using Vintagestory.GameContent;
using ProtoBuf;

namespace VSRPBridgeClient;

[ProtoContract(ImplicitFields = ImplicitFields.AllPublic)]
public class OpenCharselPacket
{
}

public class VSRPBridgeClientModSystem : ModSystem
{
    private const string ChannelName = "vsrpbridgeclient:channel";

    private ICoreServerAPI sapi;
    private ICoreClientAPI capi;
    private GuiDialogCreateCharacter charselDialog;

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
    }


    public void OpenCharsel(IServerPlayer player)
    {
        sapi.Network.GetChannel(ChannelName).SendPacket(new OpenCharselPacket(), player);
    }

    private void OnOpenCharsel(OpenCharselPacket p)
    {
        if (charselDialog != null && charselDialog.IsOpened()) return;

        var charSys = capi.ModLoader.GetModSystem<CharacterSystem>();
        charselDialog = new GuiDialogCreateCharacter(capi, charSys);
        charselDialog.PrepAndOpen();
    }
}
