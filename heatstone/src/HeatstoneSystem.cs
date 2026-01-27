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

        api.RegisterItemClass(Mod.Info.ModID + ".ItemHeatWearable", typeof(ItemHeatWearable));
    }

    public override void Dispose()
    {
        harmony?.UnpatchAll(Mod.Info.ModID);
    }
}

/// # Problem
/// The vanilla game takes 1/2 year to reduce "condition" by 1 (when moving; sadly hard-coded)
/// The warm is also hard-coded to be linear to 2*condition (clamped to 1).
/// These two together would mean we have to restrict the condition to a maximum of 1/1296
/// (half a year) to get the item to loose all of its heat in an hour (ingame), which would
/// be possible but quite impractical.
///
/// # Solutions
/// - Limit condition (possible but not a good solution, see above)
/// - Harmony patch BehaviorBodyTemperature.updateWearableConditions to add special handling
///   Downside: Impacts all clothing
/// - Harmony patch the place where the fireplace bonus is applied
///   Downside: That place does not iterate over clothing items => Performance impact without using this item
/// - Harmony patch the base class and (conditionally) change its logic for GetWarmth/ChangeCondition
///   Downside: Need to copy logic, haven't done it yet
/// - Harmony patch the base class and modify changeVal (done here)
//
// // Uncomment to disable all condition changes (repairs and loss over time)
// // Note that repair kits will still be consumed with this patch.
// [HarmonyPatch(typeof(ItemWearable), nameof(ItemWearable.ChangeCondition))]
// public class NoConditionChange
// {
//     public static bool Prefix(ItemWearable __instance, ref float changeVal)
//     {
//         // We don't use the condition system.
//         if (__instance is ItemHeatWearable item)
//             return false;
//
//         // Run the original function if it isn't of our class.
//         return true;
//     }
// }

[HarmonyPatch(typeof(ItemWearable), nameof(ItemWearable.GetWarmth))]
public class WarmthFromTemp
{
    public static bool Prefix(ItemWearable __instance, ItemSlot inslot, ref float __result)
    {
        // We don't use the condition system.
        if (__instance is ItemHeatWearable item)
        {

            float maxWarmth = inslot.Itemstack.ItemAttributes?["warmth"].AsFloat(0) ?? 0;
            float mul = inslot.Itemstack.ItemAttributes?["warmthPerTempDeg"].AsFloat(0) ?? 0;
            float temp = item.GetTemperature(item.World, inslot.Itemstack);
            __result = Math.Max(0, Math.Min(maxWarmth, (temp - 20) * mul));
            return false;
        }

        // Run the original function if it isn't of our class.
        return true;
    }
}
