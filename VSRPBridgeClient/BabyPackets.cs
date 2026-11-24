using System.Collections.Generic;
using ProtoBuf;

namespace VSRPBridgeClient;

/// <summary>
/// A baby shape: the cuboids of a chiseled block captured in-game, and which slot (skin, hair, ...) each of
/// its materials belongs to. The cuboids keep their material index, so only the materials change per baby.
/// </summary>
[ProtoContract(ImplicitFields = ImplicitFields.AllPublic)]
public class BabyShape
{
    public string Code = "";
    public string Label = "";
    // The chiseled block's cuboids, see BlockEntityMicroBlock.ToUint
    public int[] Cuboids = System.Array.Empty<int>();
    // Slot code per material index of the cuboids
    public string[] SlotByMaterialIndex = System.Array.Empty<string>();
    // The block codes the shape was captured with, used for a slot that has no choice
    public string[] FallbackMaterials = System.Array.Empty<string>();
}

[ProtoContract(ImplicitFields = ImplicitFields.AllPublic)]
public class BabyMaterialOption
{
    public string BlockCode = "";
    // Shown in the dropdown, the block's own name when empty
    public string Label = "";
}

[ProtoContract(ImplicitFields = ImplicitFields.AllPublic)]
public class BabySlotOptions
{
    public string Code = "";
    public string Label = "";
    public List<BabyMaterialOption> Options = new();
}

/// <summary>Server to client: open the baby dialog for a pending birth.</summary>
[ProtoContract(ImplicitFields = ImplicitFields.AllPublic)]
public class OpenBabyDialogPacket
{
    // The bot's id of the offspring character
    public string OffspringId = "";
    public string DefaultName = "";
    // "Man" or "Woman" as the bot sent it, or empty when it sent none
    public string Gender = "";
    // Whether the server has Tabletop Games, so the dialog offers to make the baby a tabletop piece
    public bool TabletopAvailable;
    public List<BabyShape> Shapes = new();
    public List<BabySlotOptions> Slots = new();
    // Changing the shape of a baby the player already has, from /babyshape: the name (DefaultName) is shown but
    // can't be changed, and isn't checked with the bot
    public bool Reshape;
}

/// <summary>Client to server: the player confirmed the baby dialog.</summary>
[ProtoContract(ImplicitFields = ImplicitFields.AllPublic)]
public class BabyChoicePacket
{
    public string OffspringId = "";
    public string ShapeCode = "";
    public string Name = "";
    // Unused: the server makes every baby a tabletop piece when it has Tabletop Games
    public bool AsTabletopPiece;
    // Slot code -> block code
    public Dictionary<string, string> SlotChoices = new();
    // A design from the player's own pantograph portfolio, with its own materials, instead of the server's
    // shape ShapeCode. Null for a server shape
    public BabyShape? CustomShape;
}

/// <summary>Client to server: ask whether a name is free, see BabyNameResultPacket.</summary>
[ProtoContract(ImplicitFields = ImplicitFields.AllPublic)]
public class CheckBabyNamePacket
{
    public string OffspringId = "";
    public string Name = "";
}

public enum BabyNameStatus
{
    Available,
    Taken,
    // The bot couldn't be asked, or gave no usable answer
    Failed
}

/// <summary>Server to client: the bot's answer for a name the dialog asked about.</summary>
[ProtoContract(ImplicitFields = ImplicitFields.AllPublic)]
public class BabyNameResultPacket
{
    public string OffspringId = "";
    public string Name = "";
    public BabyNameStatus Status;
    // The bot's reason when the name is taken, shown instead of the default text when given
    public string Message = "";
}

/// <summary>Client to server: the player ran .baby, so the server re-sends the dialog of a pending birth.</summary>
[ProtoContract(ImplicitFields = ImplicitFields.AllPublic)]
public class RequestBabyDialogPacket
{
}
