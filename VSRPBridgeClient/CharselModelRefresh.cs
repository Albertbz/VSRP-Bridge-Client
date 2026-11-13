using System;
using System.Linq;
using System.Reflection;
using Vintagestory.API.Client;
using Vintagestory.API.Datastructures;
using Vintagestory.GameContent;
using HarmonyLib;

namespace VSRPBridgeClient;

/// <summary>
/// PlayerModelLib's character dialog reads the models a player may pick from their "extraCustomModels"
/// attribute only while it is being composed. When VSRPBridge's model list reaches the client after the
/// dialog opened, the dialog kept listing just the models open to everyone (the default model), so the
/// player couldn't pick the model they were given. This redraws an open dialog whenever that list changes.
/// </summary>
public class CharselModelRefresh
{
    private const string ExtraModelsAttribute = "extraCustomModels";

    private readonly ICoreClientAPI capi;
    private readonly Action onModelsChanged;
    // The attributes the listener is registered on, so it's only ever registered once per entity
    private SyncedTreeAttribute? watched;

    public CharselModelRefresh(ICoreClientAPI capi)
    {
        this.capi = capi;
        onModelsChanged = OnModelsChanged;
        capi.Event.PlayerJoin += OnPlayerJoin;
    }

    private void OnPlayerJoin(IClientPlayer byPlayer)
    {
        if (byPlayer.PlayerUID != capi.World.Player?.PlayerUID) return;

        var attrs = byPlayer.Entity?.WatchedAttributes;
        if (attrs == null || attrs == watched) return;

        watched?.UnregisterListener(onModelsChanged);
        watched = attrs;
        attrs.RegisterModifiedListener(ExtraModelsAttribute, onModelsChanged);
    }

    private void OnModelsChanged()
    {
        // Attribute updates arrive in the middle of packet handling, so redraw once that's done
        capi.Event.EnqueueMainThreadTask(RedrawOpenDialogs, "vsrpbridgeclient-charselmodels");
    }

    private void RedrawOpenDialogs()
    {
        foreach (var dialog in capi.Gui.OpenedGuis.OfType<GuiDialogCreateCharacter>().ToList())
        {
            try
            {
                // PlayerModelLib's dialog declares its own ComposeGuis, which hides the vanilla one,
                // so look it up on the dialog's actual type
                MethodInfo? compose = AccessTools.Method(dialog.GetType(), "ComposeGuis");
                if (compose == null) continue;

                compose.Invoke(dialog, null);
                capi.Logger.Notification("[VSRPBridgeClient] Player models changed while the character dialog was open, redrew it.");
            }
            catch (Exception ex)
            {
                capi.Logger.Warning("[VSRPBridgeClient] Could not redraw the character dialog after the player models changed: "
                    + (ex.InnerException ?? ex).Message);
            }
        }
    }

    public void Dispose()
    {
        capi.Event.PlayerJoin -= OnPlayerJoin;
        watched?.UnregisterListener(onModelsChanged);
        watched = null;
    }
}
