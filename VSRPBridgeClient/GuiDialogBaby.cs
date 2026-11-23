using System;
using System.Collections.Generic;
using System.Linq;
using Cairo;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;

namespace VSRPBridgeClient;

/// <summary>
/// Lets the player pick the shape, materials and name of a newborn's chiseled block. Every shape is shown as a
/// thumbnail built with the chosen materials, and the selected one as a large turning preview, so changes show
/// right away. Confirm sends the choices to the server, which builds and gives the block. Closing it in any
/// other way leaves the birth pending on the server.
/// </summary>
public class GuiDialogBaby : GuiDialogGeneric
{
    private const double ThumbSize = 64;
    private const double ThumbGap = 8;
    private const int ThumbsPerRow = 6;
    // More rows than this scroll, so the dialog keeps its height however many shapes there are
    private const int VisibleThumbRows = 2;
    private const double ScrollbarWidth = 20;
    private const double SlotColumnWidth = IconSize + 6 + DropDownWidth;
    private const double SlotRowGap = 10;
    // Label, then the icon and dropdown under it
    private const double SlotRowHeight = 22 + IconSize + SlotRowGap;
    private const double PreviewSize = 180;
    private const double ColumnX = PreviewSize + 16;
    private const double DropDownWidth = 200;
    private const double IconSize = 30;
    // Above the dialog background, below opened dropdown lists (310) and hover texts (500)
    private const double ItemZ = 200;

    private readonly OpenBabyDialogPacket packet;
    private readonly Action<BabyChoicePacket> onConfirm;
    private readonly Action<string> requestNameCheck;
    // Slot code -> chosen block code, shared between shapes so switching shape keeps the choices
    private readonly Dictionary<string, string> choices = new();
    // Shape code + materials -> preview stack. Reusing the same stack keeps the chiseled block mesh cache from
    // building a new mesh every frame
    private readonly Dictionary<string, DummySlot?> previews = new();
    private readonly Dictionary<string, DummySlot> icons = new();
    // Each thumbnail's bounds and its y inside the scrolled area before scrolling
    private readonly List<(BabyShape Shape, ElementBounds Bounds, double BaseY)> thumbs = new();
    private ElementBounds? thumbClip;
    // Kept across composing again for a new shape, so picking one doesn't jump back to the top
    private float thumbScroll;
    private readonly List<(string Slot, ElementBounds Bounds)> slotIcons = new();
    // Every element of the slot rows with its y inside the scrolled area before scrolling
    private readonly List<(ElementBounds Bounds, double BaseY)> slotElements = new();
    private readonly List<string> dropDownKeys = new();
    private ElementBounds? slotClip;
    private ElementBounds? thumbScrollBounds;
    private ElementBounds? slotScrollBounds;
    private float slotScroll;
    private ElementBounds? previewBounds;
    private int selectedShape;
    private string name;
    // The name field's text as typed, which may end in a space while typing, kept when composing again
    private string nameText = "";

    // The line under the name field, as tall as its text needs. A change of height composes the dialog again,
    // see ResizeStatus
    private const double MinStatusHeight = 20;
    private double statusHeight = MinStatusHeight;
    private double statusWidth;
    private bool resizeQueued;

    // Asking the bot whether the name is free. Confirm is only enabled once it said yes for the current name
    private const int NameCheckDelayMs = 500;
    private const int NameCheckRetryMs = 5000;
    // The name the last answer was for, and the answer
    private string? checkedName;
    private BabyNameStatus checkedStatus;
    private string checkedMessage = "";
    // The name a check is waiting to be sent or answered for
    private string? requestedName;
    private long nameCheckCallback;
    // Only offered when the server has Tabletop Games, and ticked by default
    private bool asTabletopPiece = true;

    public override string? ToggleKeyCombinationCode => null;
    public override bool PrefersUngrabbedMouse => true;
    public override bool DisableMouseGrab => true;

    public GuiDialogBaby(ICoreClientAPI capi, OpenBabyDialogPacket packet, Action<BabyChoicePacket> onConfirm, Action<string> requestNameCheck)
        : base(Lang.Get("vsrpbridgeclient:baby-title"), capi)
    {
        this.packet = packet;
        this.onConfirm = onConfirm;
        this.requestNameCheck = requestNameCheck;
        name = nameText = BabyStackBuilder.CleanName(packet.DefaultName);

        foreach (var slot in packet.Slots)
        {
            var first = slot.Options.FirstOrDefault(o => BlockOf(o.BlockCode) != null);
            if (first != null) choices[slot.Code] = first.BlockCode;
        }
    }

    public string OffspringId => packet.OffspringId;

    public override bool TryOpen()
    {
        if (packet.Shapes.Count == 0) return false;
        ComposeDialog();
        return base.TryOpen();
    }

    private void ComposeDialog()
    {
        BabyShape shape = packet.Shapes[selectedShape];
        thumbs.Clear();
        slotIcons.Clear();
        slotElements.Clear();
        dropDownKeys.Clear();
        thumbScrollBounds = slotScrollBounds = null;

        // The slots of this shape that have options, in the order the config lists them
        var shapeSlots = new HashSet<string>(shape.SlotByMaterialIndex);
        var slotRows = packet.Slots
            .Where(s => shapeSlots.Contains(s.Code))
            .Select(s => (Slot: s, Options: s.Options.Where(o => BlockOf(o.BlockCode) != null).ToList()))
            .Where(r => r.Options.Count > 0)
            .ToList();
        double slotsHeight = slotRows.Count * SlotRowHeight - SlotRowGap;
        bool slotsScroll = slotsHeight > PreviewSize;

        int rows = (packet.Shapes.Count + ThumbsPerRow - 1) / ThumbsPerRow;
        double rowHeight = ThumbSize + ThumbGap;
        double gridWidth = ThumbsPerRow * rowHeight - ThumbGap;
        bool scrolls = rows > VisibleThumbRows;
        double totalHeight = rows * rowHeight - ThumbGap;
        double visibleHeight = Math.Min(rows, VisibleThumbRows) * rowHeight - ThumbGap;

        ElementBounds bg = ElementBounds.Fill.WithFixedPadding(GuiStyle.ElementToDialogPadding);
        bg.BothSizing = ElementSizing.FitToChildren;
        ElementBounds dialog = ElementStdBounds.AutosizedMainDialog.WithAlignment(EnumDialogArea.CenterMiddle);

        SingleComposer?.Dispose();
        SingleComposer = capi.Gui.CreateCompo("vsrpbridgeclient-baby", dialog)
            .AddShadedDialogBG(bg)
            .AddDialogTitleBar(DialogTitle, () => TryClose())
            .BeginChildElements(bg);

        double y = 30;
        double scrollSpace = ScrollbarWidth + 6;
        double contentWidth = Math.Max(gridWidth + (scrolls ? scrollSpace : 0), ColumnX + SlotColumnWidth + (slotsScroll ? scrollSpace : 0));
        string? genderKey = BabyStackBuilder.GenderKey(packet.Gender);
        if (genderKey != null)
        {
            SingleComposer.AddStaticText(Lang.Get(genderKey), CairoFont.WhiteSmallishText().WithWeight(FontWeight.Bold), ElementBounds.Fixed(0, y, contentWidth, 26));
            y += 30;
        }
        // Sized to the text, which wraps differently with the GUI scale, the language and edits to the lang file.
        // Measuring works in scaled pixels, so the width is scaled and the height scaled back
        string explanation = Lang.Get("vsrpbridgeclient:baby-explanation");
        CairoFont explanationFont = CairoFont.WhiteSmallText();
        double explanationHeight = Math.Ceiling(capi.Gui.Text.GetMultilineTextHeight(explanationFont, explanation, GuiElement.scaled(contentWidth)) / RuntimeEnv.GUIScale);
        SingleComposer.AddStaticText(explanation, explanationFont, ElementBounds.Fixed(0, y, contentWidth, explanationHeight));
        y += explanationHeight + 10;
        SingleComposer
            .AddStaticText(Lang.Get("vsrpbridgeclient:baby-name"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(0, y + 5, 90, 25))
            .AddTextInput(ElementBounds.Fixed(90, y, 280, 30), OnNameChanged, CairoFont.TextInput(), "name");
        y += 34;
        statusWidth = contentWidth - 90;
        SingleComposer.AddDynamicText("", CairoFont.WhiteSmallText(), ElementBounds.Fixed(90, y, statusWidth, statusHeight), "nameStatus");
        y += statusHeight + 8;

        SingleComposer.AddStaticText(Lang.Get("vsrpbridgeclient:baby-shape"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(0, y, 300, 22));
        y += 26;
        // The thumbnails sit in a clipped area. Scrolling moves each one, see OnThumbScroll
        thumbClip = ElementBounds.Fixed(0, y, gridWidth, visibleHeight);
        if (scrolls)
        {
            thumbScrollBounds = ElementBounds.Fixed(gridWidth + 6, y, ScrollbarWidth, visibleHeight);
            SingleComposer.AddVerticalScrollbar(OnThumbScroll, thumbScrollBounds, "thumbScroll");
        }
        SingleComposer.BeginClip(thumbClip);
        for (int i = 0; i < packet.Shapes.Count; i++)
        {
            double baseY = (i / ThumbsPerRow) * rowHeight;
            var thumb = ElementBounds.Fixed((i % ThumbsPerRow) * rowHeight, baseY, ThumbSize, ThumbSize);
            bool selected = i == selectedShape;
            SingleComposer
                // Dynamic, so it is drawn where the thumbnail is after scrolling. The dialog is composed again
                // when the shape changes, which moves the highlight
                .AddDynamicCustomDraw(thumb, (ctx, _, b) => DrawFrame(ctx, b, selected))
                .AddHoverText(packet.Shapes[i].Label, CairoFont.WhiteSmallText(), 250, thumb);
            thumbs.Add((packet.Shapes[i], thumb, baseY));
        }
        SingleComposer.EndClip();
        y += visibleHeight + 14;

        previewBounds = ElementBounds.Fixed(0, y, PreviewSize, PreviewSize);
        SingleComposer.AddInset(previewBounds, 2);

        // One row per slot, in a clipped column no taller than the preview. Every element in it is drawn
        // dynamically, so it moves with the scrollbar, see OnSlotScroll
        slotClip = ElementBounds.Fixed(ColumnX, y, SlotColumnWidth, Math.Min(slotsHeight, PreviewSize));
        if (slotsScroll)
        {
            slotScrollBounds = ElementBounds.Fixed(ColumnX + SlotColumnWidth + 6, y, ScrollbarWidth, PreviewSize);
            SingleComposer.AddVerticalScrollbar(OnSlotScroll, slotScrollBounds, "slotScroll");
        }
        SingleComposer.BeginClip(slotClip);
        double rowY = 0;
        for (int i = 0; i < slotRows.Count; i++)
        {
            var (slot, options) = slotRows[i];
            string[] values = options.Select(o => o.BlockCode).ToArray();
            string[] names = options.Select(OptionLabel).ToArray();
            int selected = Math.Max(0, Array.IndexOf(values, choices.GetValueOrDefault(slot.Code)));
            string slotCode = slot.Code;
            string key = "slot-" + i;

            var label = ElementBounds.Fixed(0, rowY, SlotColumnWidth, 20);
            var icon = ElementBounds.Fixed(0, rowY + 22, IconSize, IconSize);
            var dropDown = ElementBounds.Fixed(IconSize + 6, rowY + 22, DropDownWidth, IconSize);
            SingleComposer
                .AddDynamicText(slot.Label, CairoFont.WhiteSmallText(), label)
                .AddDynamicCustomDraw(icon, (ctx, _, b) => DrawIconBackground(ctx, b))
                .AddInteractiveElement(new GuiElementClippedDropDown(capi, values, names, selected,
                    (code, _) => choices[slotCode] = code, dropDown, slotClip), key);
            slotElements.Add((label, rowY));
            slotElements.Add((icon, rowY + 22));
            slotElements.Add((dropDown, rowY + 22));
            slotIcons.Add((slotCode, icon));
            dropDownKeys.Add(key);
            rowY += SlotRowHeight;
        }
        SingleComposer.EndClip();
        y += PreviewSize + 12;

        if (packet.TabletopAvailable)
        {
            SingleComposer
                .AddSwitch(on => asTabletopPiece = on, ElementBounds.Fixed(0, y, 30, 30), "tabletop")
                .AddStaticText(Lang.Get("vsrpbridgeclient:baby-tabletop"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(40, y + 6, contentWidth - 40, 22));
            y += 42;
        }

        SingleComposer
            .AddSmallButton(Lang.Get("Cancel"), () => TryClose(), ElementBounds.Fixed(0, (int)y).WithFixedPadding(10, 4))
            .AddSmallButton(Lang.Get("vsrpbridgeclient:baby-confirm"), OnConfirm,
                ElementBounds.Fixed(0, (int)y).WithFixedPadding(10, 4).WithAlignment(EnumDialogArea.RightFixed), EnumButtonStyle.Normal, "confirm")
            .EndChildElements()
            .Compose();

        // Setting the text calls OnNameChanged, which only asks the bot again when the name changed
        SingleComposer.GetTextInput("name").SetValue(nameText);
        UpdateNameStatus();
        if (packet.TabletopAvailable) SingleComposer.GetSwitch("tabletop").SetValue(asTabletopPiece);

        // Read before SetHeights: it reports the new scrollbar's position of 0 to the scroll handlers, which
        // would overwrite the remembered positions
        float keepThumbScroll = thumbScroll, keepSlotScroll = slotScroll;
        if (scrolls)
        {
            var scrollbar = SingleComposer.GetScrollbar("thumbScroll");
            scrollbar.SetHeights((float)visibleHeight, (float)totalHeight);
            scrollbar.CurrentYPosition = Math.Min(keepThumbScroll, (float)(totalHeight - visibleHeight));
            scrollbar.TriggerChanged();
        }
        else thumbScroll = 0;

        if (slotsScroll)
        {
            var scrollbar = SingleComposer.GetScrollbar("slotScroll");
            scrollbar.SetHeights((float)PreviewSize, (float)slotsHeight);
            scrollbar.CurrentYPosition = Math.Min(keepSlotScroll, (float)(slotsHeight - PreviewSize));
            scrollbar.TriggerChanged();
        }
        else slotScroll = 0;
    }

    private void OnSlotScroll(float value)
    {
        slotScroll = value;
        // An open list would stay where it opened while its dropdown moves away
        if (SingleComposer != null)
            foreach (string key in dropDownKeys)
                (SingleComposer.GetDropDown(key) as GuiElementClippedDropDown)?.CloseList();
        foreach (var (bounds, baseY) in slotElements)
        {
            bounds.fixedY = baseY - value;
            bounds.CalcWorldBounds();
        }
    }

    // The dark square behind a slot's block icon, drawn on its own surface so from 0,0
    private static void DrawIconBackground(Context ctx, ElementBounds b)
    {
        GuiElement.RoundRectangle(ctx, 1, 1, b.OuterWidth - 2, b.OuterHeight - 2, 2);
        ctx.SetSourceRGBA(0, 0, 0, 0.35);
        ctx.FillPreserve();
        ctx.SetSourceRGBA(0.6, 0.55, 0.45, 0.6);
        ctx.LineWidth = 1;
        ctx.Stroke();
    }

    private void OnThumbScroll(float value)
    {
        thumbScroll = value;
        foreach (var (_, bounds, baseY) in thumbs)
        {
            bounds.fixedY = baseY - value;
            bounds.CalcWorldBounds();
        }
    }

    // Draws on the thumbnail's own surface, so from 0,0
    private static void DrawFrame(Context ctx, ElementBounds b, bool selected)
    {
        double inset = 1.5;
        GuiElement.RoundRectangle(ctx, inset, inset, b.OuterWidth - 2 * inset, b.OuterHeight - 2 * inset, 3);
        ctx.SetSourceRGBA(0, 0, 0, 0.35);
        ctx.FillPreserve();
        // The selected one's gold outline is drawn in front of its block instead, see RenderSelectedOutline
        ctx.SetSourceRGBA(0.6, 0.55, 0.45, 0.6);
        ctx.LineWidth = 1;
        ctx.Stroke();
    }

    private void OnNameChanged(string text)
    {
        nameText = text;
        name = BabyStackBuilder.CleanName(text);
        // Composing again sets the same name, which needs no new check
        if (name.Length > 0 && name != checkedName && name != requestedName) ScheduleNameCheck(NameCheckDelayMs);
        if (name.Length == 0) CancelNameCheck();
        UpdateNameStatus();
    }

    // Waits for the player to stop typing, so the bot is asked once per name rather than per letter
    private void ScheduleNameCheck(int delayMs)
    {
        CancelNameCheck();
        string forName = name;
        requestedName = forName;
        nameCheckCallback = capi.Event.RegisterCallback(_ =>
        {
            nameCheckCallback = 0;
            if (IsOpened() && name == forName) requestNameCheck(forName);
        }, delayMs);
    }

    private void CancelNameCheck()
    {
        if (nameCheckCallback != 0) capi.Event.UnregisterCallback(nameCheckCallback);
        nameCheckCallback = 0;
        requestedName = null;
    }

    /// <summary>The bot's answer for a name, from the server. Answers for an older name are ignored.</summary>
    public void OnNameResult(BabyNameResultPacket result)
    {
        if (result.Name != name) return;
        requestedName = null;
        checkedName = result.Name;
        checkedStatus = result.Status;
        checkedMessage = result.Message ?? "";
        // A failed check is tried again by itself, so a short hiccup doesn't leave the player stuck
        if (result.Status == BabyNameStatus.Failed) ScheduleNameCheck(NameCheckRetryMs);
        UpdateNameStatus();
    }

    private bool NameIsFree => name.Length > 0 && checkedName == name && checkedStatus == BabyNameStatus.Available;

    private string StatusText()
    {
        if (name.Length == 0) return "";
        if (checkedName != name) return Lang.Get("vsrpbridgeclient:baby-name-checking");
        return checkedStatus switch
        {
            BabyNameStatus.Available => Lang.Get("vsrpbridgeclient:baby-name-available"),
            BabyNameStatus.Taken => checkedMessage.Length > 0 ? checkedMessage : Lang.Get("vsrpbridgeclient:baby-name-taken"),
            _ => Lang.Get("vsrpbridgeclient:baby-name-failed")
        };
    }

    // In unscaled units. Measuring works in scaled pixels, so the width is scaled and the height scaled back
    private double StatusHeightFor(string text) => text.Length == 0 ? MinStatusHeight
        : Math.Max(MinStatusHeight, Math.Ceiling(capi.Gui.Text.GetMultilineTextHeight(CairoFont.WhiteSmallText(), text, GuiElement.scaled(statusWidth)) / RuntimeEnv.GUIScale));

    private void UpdateNameStatus()
    {
        if (SingleComposer == null) return;
        string text = StatusText();
        SingleComposer.GetDynamicText("nameStatus")?.SetNewText(text);
        var confirm = SingleComposer.GetButton("confirm");
        if (confirm != null) confirm.Enabled = NameIsFree;

        // A different height needs the dialog composed again. That is left until the current input event is
        // done, as this often runs while the name field is handling a key, which would otherwise carry on in
        // the old field and leave the caret in the wrong place
        if (StatusHeightFor(text) != statusHeight && !resizeQueued)
        {
            resizeQueued = true;
            capi.Event.EnqueueMainThreadTask(ResizeStatus, "vsrpbridgeclient-babyname");
        }
    }

    /// <summary>Composes the dialog again with the status line as tall as its text, keeping the name field's focus and caret.</summary>
    private void ResizeStatus()
    {
        resizeQueued = false;
        if (!IsOpened() || SingleComposer == null) return;
        double needed = StatusHeightFor(StatusText());
        if (needed == statusHeight) return;

        var input = SingleComposer.GetTextInput("name");
        bool focused = input.HasFocus;
        int caret = input.CaretPosWithoutLineBreaks;
        statusHeight = needed;
        ComposeDialog();
        if (focused)
        {
            input = SingleComposer.GetTextInput("name");
            SingleComposer.FocusElement(input.TabIndex);
            input.CaretPosWithoutLineBreaks = caret;
        }
    }

    public override void OnGuiClosed()
    {
        CancelNameCheck();
        base.OnGuiClosed();
    }

    private bool OnConfirm()
    {
        if (!NameIsFree) return true;
        BabyShape shape = packet.Shapes[selectedShape];
        onConfirm(new BabyChoicePacket
        {
            OffspringId = packet.OffspringId,
            ShapeCode = shape.Code,
            Name = name,
            AsTabletopPiece = packet.TabletopAvailable && asTabletopPiece,
            SlotChoices = shape.SlotByMaterialIndex.Distinct()
                .Where(choices.ContainsKey)
                .ToDictionary(s => s, s => choices[s])
        });
        TryClose();
        return true;
    }

    public override void OnMouseDown(MouseEvent args)
    {
        for (int i = 0; i < thumbs.Count; i++)
        {
            // Thumbnails scrolled out of view are still there, just clipped
            if (thumbClip?.PointInside(args.X, args.Y) != true || !thumbs[i].Bounds.PointInside(args.X, args.Y)) continue;
            if (i != selectedShape)
            {
                selectedShape = i;
                ComposeDialog();
            }
            args.Handled = true;
            return;
        }
        base.OnMouseDown(args);
        args.Handled = true;
    }

    // Like the waypoint dialog: keep clicks and typing in the dialog instead of passing them to the game
    public override bool CaptureAllInputs() => IsOpened();

    public override void OnMouseUp(MouseEvent args)
    {
        base.OnMouseUp(args);
        args.Handled = true;
    }

    public override void OnMouseMove(MouseEvent args)
    {
        base.OnMouseMove(args);
        args.Handled = true;
    }

    // Routed by hand: the composer would otherwise pass a wheel event that nothing under the mouse takes to
    // every element, scrolling both lists at once
    public override void OnMouseWheel(MouseWheelEventArgs args)
    {
        args.SetHandled();
        if (SingleComposer == null) return;
        int x = capi.Input.MouseX, y = capi.Input.MouseY;

        // An open dropdown list, or a focused dropdown under the mouse, which steps through its options
        foreach (string key in dropDownKeys)
        {
            var dropDown = SingleComposer.GetDropDown(key);
            if (!dropDown.IsPositionInside(x, y)) continue;
            var inner = new MouseWheelEventArgs { delta = args.delta, deltaPrecise = args.deltaPrecise };
            dropDown.OnMouseWheel(capi, inner);
            if (inner.IsHandled) return;
        }

        if (thumbScrollBounds != null && (thumbClip!.PointInside(x, y) || thumbScrollBounds.PointInside(x, y)))
            SingleComposer.GetScrollbar("thumbScroll").OnMouseWheel(capi, args);
        else if (slotScrollBounds != null && (slotClip!.PointInside(x, y) || slotScrollBounds.PointInside(x, y)))
            SingleComposer.GetScrollbar("slotScroll").OnMouseWheel(capi, args);
    }

    public override void OnRenderGUI(float deltaTime)
    {
        base.OnRenderGUI(deltaTime);
        if (SingleComposer == null) return;

        if (thumbClip != null)
        {
            capi.Render.PushScissor(thumbClip);
            foreach (var (shape, bounds, _) in thumbs)
            {
                // Skip those scrolled out of view
                if (bounds.renderY + bounds.OuterHeight < thumbClip.renderY || bounds.renderY > thumbClip.renderY + thumbClip.OuterHeight) continue;
                RenderCentered(PreviewOf(shape), bounds, ThumbSize * 0.8, false);
            }
            if (selectedShape < thumbs.Count) RenderSelectedOutline(thumbs[selectedShape].Bounds);
            capi.Render.PopScissor();
        }

        if (previewBounds != null)
            RenderCentered(PreviewOf(packet.Shapes[selectedShape]), previewBounds, PreviewSize * 0.75, true);

        if (slotClip != null)
        {
            capi.Render.PushScissor(slotClip);
            foreach (var (slot, bounds) in slotIcons)
                if (choices.TryGetValue(slot, out var code)) RenderCentered(IconOf(code), bounds, IconSize * 0.7, false);
            capi.Render.PopScissor();
        }
    }

    // Gold, opaque, as ARGB
    private const int SelectedOutlineColor = unchecked((int)0xFFF2CC66);
    // In front of the thumbnail blocks (drawn around ItemZ) and behind hover texts (500)
    private const float OutlineZ = 400;

    /// <summary>
    /// The selected thumbnail's outline, drawn after the blocks and in front of them: a block filling its
    /// thumbnail covered an outline drawn into the frame behind it.
    /// </summary>
    private void RenderSelectedOutline(ElementBounds bounds)
    {
        int thickness = Math.Max(2, (int)Math.Round(GuiElement.scaled(2.5)));
        float x = (float)bounds.renderX, y = (float)bounds.renderY;
        float w = (float)bounds.OuterWidth, h = (float)bounds.OuterHeight;
        for (int i = 0; i < thickness; i++)
            capi.Render.RenderRectangle(x + i, y + i, OutlineZ, w - 2 * i, h - 2 * i, SelectedOutlineColor);
    }

    private void RenderCentered(ItemSlot? slot, ElementBounds bounds, double size, bool rotate)
    {
        if (slot?.Itemstack == null) return;
        capi.Render.PushScissor(bounds, true);
        capi.Render.RenderItemstackToGui(slot,
            bounds.renderX + bounds.OuterWidth / 2, bounds.renderY + bounds.OuterHeight / 2, ItemZ,
            (float)GuiElement.scaled(size), -1, shading: true, rotate: rotate, showStackSize: false);
        capi.Render.PopScissor();
    }

    private DummySlot? PreviewOf(BabyShape shape)
    {
        string key = shape.Code + "|" + string.Join(",", BabyStackBuilder.ResolveMaterials(shape, choices));
        if (!previews.TryGetValue(key, out var slot))
        {
            ItemStack? stack = BabyStackBuilder.Build(capi.World, shape, choices, name);
            previews[key] = slot = stack != null ? new DummySlot(stack) : null;
        }
        return slot;
    }

    private DummySlot? IconOf(string blockCode)
    {
        if (icons.TryGetValue(blockCode, out var slot)) return slot;
        Block? block = BlockOf(blockCode);
        if (block == null) return null;
        return icons[blockCode] = new DummySlot(new ItemStack(block));
    }

    private Block? BlockOf(string code)
    {
        Block? block = capi.World.GetBlock(new AssetLocation(code));
        return block == null || block.Id == 0 ? null : block;
    }

    private string OptionLabel(BabyMaterialOption option)
    {
        if (!string.IsNullOrWhiteSpace(option.Label)) return option.Label;
        Block? block = BlockOf(option.BlockCode);
        return block != null ? block.GetHeldItemName(new ItemStack(block)) : option.BlockCode;
    }
}
