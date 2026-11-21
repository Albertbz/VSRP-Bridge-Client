using Cairo;
using Vintagestory.API.Client;

namespace VSRPBridgeClient;

/// <summary>
/// A dropdown that stays inside a scrolled area. Two parts of a plain dropdown break when it scrolls:
/// <list type="bullet">
/// <item>It draws after every other element so its open list is on top, which is after the area's clip has
/// ended. This one clips itself while closed. An open list isn't clipped, so it can reach past the area like
/// any other dropdown list.</item>
/// <item>Its dark background is painted once onto the dialog's static surface, so it neither moved nor was
/// clipped while the text and arrow did. This one draws its background as a texture where it is now.</item>
/// </list>
/// </summary>
public class GuiElementClippedDropDown : GuiElementDropDown
{
    private readonly ElementBounds clip;
    private LoadedTexture background;

    public GuiElementClippedDropDown(ICoreClientAPI capi, string[] values, string[] names, int selectedIndex,
        SelectionChangedDelegate onSelectionChanged, ElementBounds bounds, ElementBounds clip)
        : base(capi, values, names, selectedIndex, onSelectionChanged, bounds, CairoFont.WhiteSmallText(), multiSelect: false)
    {
        this.clip = clip;
        background = new LoadedTexture(capi);
    }

    public override void ComposeElements(Context ctx, ImageSurface surface)
    {
        // The base paints the background onto the context it is given and composes everything else. Giving it
        // a throwaway one keeps the background off the dialog's static surface
        using (var dummySurface = new ImageSurface(Format.Argb32, 1, 1))
        using (var dummyCtx = new Context(dummySurface))
        {
            base.ComposeElements(dummyCtx, dummySurface);
        }

        // The same background as the base draws, from 0,0 on its own surface
        int width = (int)Bounds.InnerWidth, height = (int)Bounds.InnerHeight;
        using var bgSurface = new ImageSurface(Format.Argb32, width, height);
        using var bgCtx = genContext(bgSurface);
        bgCtx.SetSourceRGBA(0.0, 0.0, 0.0, 0.2);
        RoundRectangle(bgCtx, 0, 0, width, height, 3.0);
        bgCtx.Fill();
        EmbossRoundRectangleElement(bgCtx, 0, 0, width, height, inverse: true, 1, 1);
        generateTexture(bgSurface, ref background);
    }

    public override void RenderInteractiveElements(float deltaTime)
    {
        bool clipped = !listMenu.IsOpened;
        if (clipped) api.Render.PushScissor(clip);
        api.Render.Render2DTexturePremultipliedAlpha(background.TextureId, (int)Bounds.renderX, (int)Bounds.renderY, background.Width, background.Height);
        base.RenderInteractiveElements(deltaTime);
        if (clipped) api.Render.PopScissor();
    }

    public void CloseList()
    {
        // Close() is internal to the game, and losing focus is what closes the list there
        if (listMenu.IsOpened) listMenu.OnFocusLost();
    }

    public override void Dispose()
    {
        base.Dispose();
        background.Dispose();
    }
}
