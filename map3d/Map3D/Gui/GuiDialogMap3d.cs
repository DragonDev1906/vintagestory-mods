using Vintagestory.API.Client;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using System;
using System.Globalization;

namespace Map3D;

internal class GuiDialogMap3d : GuiDialogGeneric
{
    // I'm not 100% sure where this comes from, but this converts the positions into
    // the coordinates shown to the player (minimap).
    const int coordinateOffset = 512000;
    const float deg2rad = 0.01745329f; // 2pi / 360
    const int offsetSteps = 32; // voxel (based on textures)

    private BlockEntityMapDisplay be;

    GuiElementDropDown mode;
    GuiElementDropDown lod;

    GuiElementNumberInput corner1X;
    GuiElementNumberInput corner1Z;
    GuiElementNumberInput corner2X;
    GuiElementNumberInput corner2Z;

    GuiElementDynamicText area;

    GuiElementSlider offsetX;
    GuiElementSlider offsetY;
    GuiElementSlider offsetZ;

    GuiElementSlider rotX;
    GuiElementSlider rotY;
    GuiElementSlider rotZ;

    GuiElementSlider scale;

    // Based on GuiDialogSignPost
    // https://github.com/anegostudios/vssurvivalmod/blob/master/Gui/GuiDialogSignPost.cs
    internal GuiDialogMap3d(string dialogTitle, ICoreClientAPI capi, BlockEntityMapDisplay be)
        : base(dialogTitle, capi)
    {
        this.be = be;

        // These are mutated during composer creation below.
        // I don't really like the syntax used there, but couldn't come up with
        // a better one given the API we get.
        // ElementBounds currentLabel = ElementBounds.Fixed(0, 0, 150, 20);
        // ElementBounds currentInput = ElementBounds.Fixed(0, 15, 150, 25);

        const float gapX = 10;
        const float gapY = 5;
        const float w1 = 320;
        const float w2 = 155;
        // const float w3 = 100;
        const float hLabel = 20;
        const float hInput = 25;

        // Current position. New elements are added relative to this position.
        float x = 0;
        float y = 25;
        float currentHeight = 0;

        // Helper functions to compute bounds for elements.
        var newline = () =>
        {
            x = 0;
            y += currentHeight + gapY;
            currentHeight = 0;
        };
        var bounds = (float width, float height) =>
        {
            if (height > currentHeight)
            {
                currentHeight = height;
            }
            var el = ElementBounds.Fixed(x, y, width, height);
            x += width + gapX;
            return el;
        };
        var label = () =>
        {
            if (x > 0)
            {
                newline();
            }
            var el = ElementBounds.Fixed(x, y, w1, hLabel);
            currentHeight = hLabel;
            newline();
            return el;
        };

        // 10px padding
        ElementBounds bgBounds = ElementBounds.Fill.WithFixedPadding(GuiStyle.ElementToDialogPadding);
        bgBounds.BothSizing = ElementSizing.FitToChildren;

        ElementBounds dialogBounds = ElementStdBounds.AutosizedMainDialog.WithAlignment(EnumDialogArea.LeftTop)
            .WithFixedAlignmentOffset(60 + GuiStyle.DialogToScreenPadding, GuiStyle.DialogToScreenPadding);

        CairoFont fontLabel = CairoFont.WhiteDetailText();
        CairoFont fontInput = CairoFont.WhiteSmallText();

        // These methods can be chained, but I find that a bit restrictive and not really more readable.
        GuiComposer c = capi.Gui.CreateCompo("blockentitymap3ddialog", dialogBounds);

        // Helper methods to add elements.
        var addLabel = (string text) =>
        {
            c.AddStaticText(Lang.Get(text), fontLabel, label());
        };
        var addInt = (float width, int value, Action<int> onChanged) =>
        {
            GuiElementNumberInput el = new(capi, bounds(width, hInput), (value) =>
            {
                int.TryParse(value, NumberStyles.Any, GlobalConstants.DefaultCultureInfo, out var result);
                onChanged(result);
            }, CairoFont.WhiteSmallText());
            c.AddInteractiveElement(el);
            el.SetValue(value);
            return el;
        };
        var addFloat = (float width, float value, Action<float> onChanged) =>
        {
            GuiElementNumberInput el = new(capi, bounds(width, hInput), (value) =>
            {
                float.TryParse(value, NumberStyles.Any, GlobalConstants.DefaultCultureInfo, out var result);
                onChanged(result);
            }, CairoFont.WhiteSmallText());
            c.AddInteractiveElement(el);
            el.SetValue(value);
            return el;
        };
        var addSlider = (float width, int min, int max, int step, string unit, int value, Action<int> onChanged) =>
        {
            GuiElementSlider el = new(capi, (value) =>
            {
                onChanged(value);
                return true;
            }, bounds(width, hInput));
            c.AddInteractiveElement(el);
            el.SetValues(value, min, max, step, unit);
            return el;
        };
        var addSliderFloat = (float width, float min, float max, int factor, string unit, float value, Action<float> onChanged) =>
        {
            // A slightly more sane interface for floating point sliders.
            // Also solves the issue of step sizes like 0.1 (though it's still not good).
            return addSlider(
                width,
                (int)(min * factor),
                (int)(max * factor),
                1,
                unit,
                (int)(value * factor),
                (value) => onChanged((float)value / factor)
            );
        };

        c.AddShadedDialogBG(bgBounds);
        c.AddDialogTitleBar(dialogTitle, OnTitlebarClose);
        c.BeginChildElements(bgBounds);

        addLabel("Mode");
        mode = new(capi,
            new string[] { "full", "surface" },
            new string[] { "All blocks", "Only surface" },
            0, (_, _) => { }, bounds(w1, hInput), fontInput, false
        );
        c.AddInteractiveElement(mode);

        addLabel("LoD (Blocks per Block)");
        lod = new(capi,
            new string[] { "1", "2", "4", "8", "16", "32" },
            new string[] { "1:1", "1:2", "1:4", "1:8", "1:16", "1:32" },
            0, (_, _) => { }, bounds(w1, hInput), fontInput, false
        );
        c.AddInteractiveElement(lod);

        addLabel("Corner 1");
        corner1X = addInt(w2, be.corner1.X - coordinateOffset, (_) => UpdateAreaText());
        corner1Z = addInt(w2, be.corner1.Z - coordinateOffset, (_) => UpdateAreaText());

        addLabel("Corner 2");
        corner2X = addInt(w2, be.corner2.X - coordinateOffset, (_) => UpdateAreaText());
        corner2Z = addInt(w2, be.corner2.Z - coordinateOffset, (_) => UpdateAreaText());

        newline();
        area = new(capi, "", CairoFont.WhiteSmallText(), bounds(w1, 2 * hLabel));
        c.AddInteractiveElement(area);
        newline();
        c.AddSmallButton(Lang.Get("Reset"), OnResetCorners, bounds(w2, hInput), EnumButtonStyle.Normal);
        c.AddSmallButton(Lang.Get("Update Map data"), OnUpdateMap, bounds(w2, hInput), EnumButtonStyle.Normal);
        y += hInput; // Add some extra space between region and display settings.

        // In the options below we could just send the update to the server and wait for it to update us.
        // That would however not really be user-friendly, so we try to hide the network latency by
        // updating our view immediately.
        addLabel("Offset X");
        offsetX = addSliderFloat(w1, -4, 4, offsetSteps, " voxel", (float)be.dimension.CurrentPos.X - be.Pos.X, (value) =>
        {
            be.dimension.CurrentPos.X = be.Pos.X + value;
            UpdateServerSide();
        });
        addLabel("Offset Y");
        offsetY = addSliderFloat(w1, -4, 4, offsetSteps, " voxel", (float)be.dimension.CurrentPos.Y - be.Pos.Y, (value) =>
        {

            be.dimension.CurrentPos.Y = be.Pos.Y + value;
            UpdateServerSide();
        });
        addLabel("Offset Z");
        offsetZ = addSliderFloat(w1, -4, 4, offsetSteps, " voxel", (float)be.dimension.CurrentPos.Z - be.Pos.Z, (value) =>
        {
            be.dimension.CurrentPos.Z = be.Pos.Z + value;
            UpdateServerSide();
        });

        addLabel("Yaw");
        rotX = addSliderFloat(w1, -180, 180, 1, "°", be.dimension.CurrentPos.Yaw / deg2rad, (value) =>
        {
            be.dimension.CurrentPos.Yaw = value * deg2rad;
            UpdateServerSide();
        });
        addLabel("Pitch");
        rotY = addSliderFloat(w1, -90, 90, 1, "°", be.dimension.CurrentPos.Pitch / deg2rad, (value) =>
        {
            be.dimension.CurrentPos.Pitch = value * deg2rad;
            UpdateServerSide();
        });
        addLabel("Roll");
        rotZ = addSliderFloat(w1, -180, 180, 1, "°", be.dimension.CurrentPos.Roll / deg2rad, (value) =>
        {
            be.dimension.CurrentPos.Roll = value * deg2rad;
            UpdateServerSide();
        });

        addLabel("Scale");
        scale = addSliderFloat(w1, 0.01f, 1, 100, "%", be.scale, (value) =>
        {
            be.dimension.scale = value;
            UpdateServerSide();
        });

        c.EndChildElements();
        SingleComposer = c.Compose();

        // This makes setting the values above slightly redundant.
        LoadFromBE();
    }

    private void UpdateAreaText()
    {
        if (corner1X == null || corner2X == null || corner1Z == null || corner2Z == null)
            return;

        int x1 = (int)corner1X.GetValue() - (be.Pos.X - coordinateOffset);
        int x2 = (int)corner2X.GetValue() - (be.Pos.X - coordinateOffset);
        int z1 = (int)corner1Z.GetValue() - (be.Pos.Z - coordinateOffset);
        int z2 = (int)corner2Z.GetValue() - (be.Pos.Z - coordinateOffset);
        // TODO: Support different languages
        area.SetNewText("Relative: (" + x1 + ", " + z1 + ") to (" + x2 + ", " + z2 + ")");
    }

    private void OnTitlebarClose()
    {
        TryClose();
    }

    // Called from the BE when the server sends new settings.
    // This allows us to view changes made by other players live,
    // hopefully without falling into infinite recursion.
    public void LoadFromBE()
    {
        capi.Logger.Notification("Update from BE");
        loadDataSettingsFromBE();
        loadDisplaySettingsFromBE();
    }
    private void loadDataSettingsFromBE()
    {
        mode.SetSelectedIndex((int)be.mode);
        lod.SetSelectedIndex((int)be.lod);
        corner1X.SetValue(be.corner1.X - coordinateOffset);
        corner1Z.SetValue(be.corner1.Z - coordinateOffset);
        corner2X.SetValue(be.corner2.X - coordinateOffset);
        corner2Z.SetValue(be.corner2.Z - coordinateOffset);
        UpdateAreaText();
    }
    private void loadDisplaySettingsFromBE()
    {
        offsetX.SetValue(be.offset.X);
        offsetY.SetValue(be.offset.Y);
        offsetZ.SetValue(be.offset.Z);
        rotX.SetValue((int)(be.rotation.X / deg2rad));
        rotY.SetValue((int)(be.rotation.Y / deg2rad));
        rotZ.SetValue((int)(be.rotation.Z / deg2rad));
        scale.SetValue((int)(be.scale * 100));
    }

    private bool OnResetCorners()
    {
        loadDataSettingsFromBE();
        return true;
    }

    private bool OnUpdateMap()
    {
        var corner1 = new BlockPos((int)corner1X.GetValue() + coordinateOffset, 0, (int)corner1Z.GetValue() + coordinateOffset);
        var corner2 = new BlockPos((int)corner2X.GetValue() + coordinateOffset, 256, (int)corner2Z.GetValue() + coordinateOffset);
        var mode = (MapMode)this.mode.SelectedIndices[0];
        var lod = (Lod)this.lod.SelectedIndices[0];

        UpdateDimensionPacket p = new(mode, corner1, corner2, lod);
        capi.Network.SendBlockEntityPacket(be.Pos, (int)MapPacket.UpdateDimension, p);
        return true;
    }

    private void UpdateServerSide()
    {
        var offset = new Vec3i(offsetX.GetValue(), offsetY.GetValue(), offsetZ.GetValue());
        var rotation = new Vec3f(rotX.GetValue() * deg2rad, rotY.GetValue() * deg2rad, rotZ.GetValue() * deg2rad);
        float scale = this.scale.GetValue() / 100f;

        ConfigurePacket p = new(offset, rotation, scale);
        capi.Network.SendBlockEntityPacket(be.Pos, (int)MapPacket.Configure, p);
    }
}

