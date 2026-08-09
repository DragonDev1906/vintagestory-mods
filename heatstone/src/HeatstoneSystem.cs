using System;
using System.Reflection;
using Vintagestory.API.Client;
using Vintagestory.API.Server;
using Vintagestory.API.Config;
using Vintagestory.API.Common;
using Vintagestory.Common;
using Vintagestory.GameContent;
using HarmonyLib;

namespace Heatstone;

public class HeatstoneSystem : ModSystem
{
    Harmony harmony;

    public override void Start(ICoreAPI api)
    {
        base.Start(api);

        harmony = new Harmony(Mod.Info.ModID);
        harmony.PatchAll();

        api.RegisterCollectibleBehaviorClass(Mod.Info.ModID + ".HeatWearable", typeof(CollectibleBehaviorHeatWearable));
    }

    public override void Dispose()
    {
        harmony?.UnpatchAll(Mod.Info.ModID);
    }
}

