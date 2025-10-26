using Vintagestory.API.Client;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using System;
using System.Globalization;

#nullable enable

namespace Map3D;

internal class GuiDialogMap3d : GuiDialogGeneric
{
    // I'm not 100% sure where this comes from, but this converts the positions into
    // the coordinates shown to the player (minimap).
    const int coordinateOffset = 512000;
    const float deg2rad = 0.01745329f; // 2pi / 360
    const int offsetSteps = 32; // voxel (based on textures)

    private BlockEntityMap be;

    // Block attributes
    int maxDistance;

    GuiElementDropDown mode;
    GuiElementDropDown lod;

    GuiElementNumberInput centerX;
    GuiElementNumberInput centerZ;
    GuiElementNumberInput sizeX;
    GuiElementNumberInput sizeZ;

    GuiElementDynamicText area;

    GuiElementSlider? offsetX;
    GuiElementSlider? offsetY;
    GuiElementSlider? offsetZ;

    GuiElementSlider? rotX;
    GuiElementSlider? rotY;
    GuiElementSlider? rotZ;

    GuiElementSlider size;

    // Based on GuiDialogSignPost
    // https://github.com/anegostudios/vssurvivalmod/blob/master/Gui/GuiDialogSignPost.cs
    internal GuiDialogMap3d(string dialogTitle, ICoreClientAPI capi, BlockEntityMap be)
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

        // Block attributes + configuration
        float catalystEfficiency = be.Block.Attributes?["catalystEfficiency"]?.AsFloat(0.1f) ?? 0.1f;
        this.maxDistance = be.Block.Attributes?["maxDistance"]?.AsInt() ?? 0;
        int maxSize = be.Block.Attributes?["maxSize"]?.AsInt(1) ?? 1;
        bool rotation = be.Block.Attributes?["rotation"]?.AsBool() ?? false;
        bool restrictedRotation = be.Block.Attributes?["restrictedRotation"]?[be.Block.Variant["type"]]?.AsBool() ?? false;
        int maxOffset = be.Block.Attributes?["maxOffset"]?.AsInt() ?? 0;
        float xcenter = be.Block.Attributes?["center"]?["x"]?.AsFloat() ?? 0;
        float ycenter = be.Block.Attributes?["center"]?["y"]?.AsFloat() ?? 0;
        float zcenter = be.Block.Attributes?["center"]?["z"]?.AsFloat() ?? 0;

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

        addLabel("Center");
        centerX = addInt(w2, be.center.X - coordinateOffset, (_) => UpdateAreaText());
        centerZ = addInt(w2, be.center.Z - coordinateOffset, (_) => UpdateAreaText());

        addLabel("Size");
        sizeX = addInt(w2, be.srcSize.X, (_) => UpdateAreaText());
        sizeZ = addInt(w2, be.srcSize.Z, (_) => UpdateAreaText());

        newline();
        area = new(capi, "", CairoFont.WhiteSmallText(), bounds(w1, 2 * hLabel));
        c.AddInteractiveElement(area);
        newline();
        c.AddSmallButton(Lang.Get("Reset"), OnResetCorners, bounds(w2, hInput), EnumButtonStyle.Normal);
        c.AddSmallButton(Lang.Get("Update Map data"), OnUpdateMap, bounds(w2, hInput), EnumButtonStyle.Normal);
        y += hInput; // Add some extra space between region and display settings.

        if (maxOffset > 0)
        {
            addLabel("Offset X");
            offsetX = addSliderFloat(w1, -4, 4, offsetSteps, " voxel", (float)be.dimension.CurrentPos.X - be.Pos.X, (value) =>
            {
                UpdateServerSide();
            });
            addLabel("Offset Y");
            offsetY = addSliderFloat(w1, -4, 4, offsetSteps, " voxel", (float)be.dimension.CurrentPos.Y - be.Pos.Y, (value) =>
            {

                UpdateServerSide();
            });
            addLabel("Offset Z");
            offsetZ = addSliderFloat(w1, -4, 4, offsetSteps, " voxel", (float)be.dimension.CurrentPos.Z - be.Pos.Z, (value) =>
            {
                UpdateServerSide();
            });
        }

        if (rotation)
        {
            addLabel("Yaw");
            rotX = addSliderFloat(w1, -180, 180, 1, "°", be.dimension.CurrentPos.Yaw / deg2rad, (value) =>
            {
                UpdateServerSide();
            });
        }
        if (rotation && !restrictedRotation)
        {
            addLabel("Pitch");
            rotY = addSliderFloat(w1, -90, 90, 1, "°", be.dimension.CurrentPos.Pitch / deg2rad, (value) =>
            {
                UpdateServerSide();
            });
            addLabel("Roll");
            rotZ = addSliderFloat(w1, -180, 180, 1, "°", be.dimension.CurrentPos.Roll / deg2rad, (value) =>
            {
                UpdateServerSide();
            });
        }

        addLabel("Size");
        size = addSlider(w1, 1, 32 * maxSize, 1, " voxel", be.size, (value) =>
        {
            UpdateServerSide();
        });

        c.EndChildElements();
        SingleComposer = c.Compose();

        // This makes setting the values above slightly redundant.
        LoadFromBE();
    }

    private void UpdateAreaText()
    {
        if (centerX == null || centerZ == null || sizeX == null || sizeZ == null)
            return;

        // Convert to relative coordinates
        int x = (int)centerX.GetValue() - (be.Pos.X - coordinateOffset);
        int z = (int)centerZ.GetValue() - (be.Pos.Z - coordinateOffset);
        long distanceSq = (long)x * x + (long)z * z;

        // Build the text to display
        // TODO: Support different languages
        string text = "Relative: (" + x + ", " + z + ")\nDistance: " + MathF.Sqrt(distanceSq);
        if (distanceSq > this.maxDistance * this.maxDistance)
            text += "\nINVALID: Max distance = " + this.maxDistance;


        // Set the text
        area.SetNewText(text);
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
        centerX.SetValue(be.center.X - coordinateOffset);
        centerZ.SetValue(be.center.Z - coordinateOffset);
        sizeX.SetValue(be.srcSize.X);
        sizeZ.SetValue(be.srcSize.Z);
        UpdateAreaText();
    }
    private void loadDisplaySettingsFromBE()
    {
        offsetX?.SetValue(be.offset.X);
        offsetY?.SetValue(be.offset.Y);
        offsetZ?.SetValue(be.offset.Z);
        rotX?.SetValue((int)(be.rotation.X / deg2rad));
        rotY?.SetValue((int)(be.rotation.Y / deg2rad));
        rotZ?.SetValue((int)(be.rotation.Z / deg2rad));
        size.SetValue(be.size);
    }

    private bool OnResetCorners()
    {
        loadDataSettingsFromBE();
        return true;
    }

    private bool OnUpdateMap()
    {
        int sx = (int)sizeX.GetValue();
        int sz = (int)sizeZ.GetValue();
        var center = new BlockPos(
            (int)centerX.GetValue() + coordinateOffset,
            0,
            (int)centerZ.GetValue() + coordinateOffset
        );
        var size = new Vec3i(sx, 256, sz);
        var mode = (MapMode)this.mode.SelectedIndices[0];
        var lod = (Lod)this.lod.SelectedIndices[0];

        UpdateDimensionPacket p = new(mode, center, size, lod);
        capi.Network.SendBlockEntityPacket(be.Pos, (int)MapPacket.UpdateDimension, p);
        return true;
    }

    private void UpdateServerSide()
    {
        Vec3i? offset = null;
        Vec3f? rotation = null;

        if (offsetX != null && offsetY != null && offsetZ != null)
            offset = new Vec3i(offsetX.GetValue(), offsetY.GetValue(), offsetZ.GetValue());
        if (rotX != null || rotY != null || rotZ != null)
            rotation = new Vec3f(
                rotX?.GetValue() ?? 0 * deg2rad,
                rotY?.GetValue() ?? 0 * deg2rad,
                rotZ?.GetValue() ?? 0 * deg2rad
            );
        int size = this.size.GetValue();

        ConfigurePacket p = new(offset, rotation, size);
        be.ApplyConfigPacket(p);
        capi.Network.SendBlockEntityPacket(be.Pos, (int)MapPacket.Configure, p);
    }
}

