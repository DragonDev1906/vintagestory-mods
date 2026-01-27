using System.Text;
using Vintagestory.GameContent;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;

namespace Heatstone;

/// # Added/Relevant attributes
/// - "cooldownSpeed" (float): Previously just on the itemstack, now it can be used
///   to specify passive temperature drain.
/// - "hoursToDrainCondition" (float): Hours it takes to drain the items temperature
///   while wearing it. Does not include the time from cooldownSpeed.
///
/// # OLD
/// ## Added/Most relevant attributes
/// - "hoursToDrainCondition" (float): Number of ingame hours it takes to get condition from 1 to 0
///   (while moving and not taking damage)
/// - "warmth" (float): Same as vanilla, indicates how much warmth this item gives max
///   (only reduced once condition falls below 50%)
///
/// ### Not yet added
/// - "minChargingTemp" (float): Minimum temperature required to get any charging
/// - "maxChargingTemp" (float): Temperature at which it becomes possible to charge to condition 1.
/// - "heatCapacity" (float): Max amount of energy that can be stored (condition=1)
public class ItemHeatWearable : ItemWearable
{
    internal IWorldAccessor World
    {
        get
        {
            return api.World;
        }
    }

    // Applied via Harmony patch
    // public float ConditionChangeMultiplier = 1;

    // override OnLoaded(ICoreAPI api)
    // {
    //     base.OnLoaded(api);
    //     // Normal behavior is full reduction in 1/2 year (1296 hours).
    //     // A value of 1296 hours matches vanilla behavior (multiplier = 1).
    //     // A value of 1 hour results in a multiplier of 1296.
    //     //
    //     // I could just as well have used a "ConditionChangeMultiplier" attribute, but this
    //     // one seems easier to adjust/balance when editing the json.
    //     // ConditionChangeMultiplier = 1296 / Attributes?.GetFloat("hoursToDrainCondition", 1296);
    // }

    // /// <param name="didReceiveHeat">The amount of time it did receive heat since last update/call to this methode</param>
    // public void UpdateConditionFromTemp(ItemStack stack, double didReceiveHeat)
    // {
    //     float stored = stack?.Attributes?.GetFloat("storedheat", 0) ?? 0;
    //     if (stack?.Attributes?["temperature"] is not ITreeAttribute attr || attr == null)
    //         return;
    //
    //     if (stack?.Attributes?.GetBool("timeFrozen", false))
    //         return;
    //
    //     double now = world.Calendar.TotalHours;
    //     float temp = (float)attr.GetDecimal("temperature", 20);
    //     double lastUpdate = attr.GetDecimal("temperatureLastUpdate");
    //     double dt = now - lastUpdate - didReceiveHeat;
    //
    //     // 1.5 deg per irl second
    //     // 1 game hour = irl 60 seconds
    //     //
    //     // No idea why those constants, but that's what the game does.
    //     // When called, the game sets the new temperature to `temp - (time * cooldownSpeed)`.
    //     // We're keeping the base implementation, but we want the stored-heat to match its behavior.
    //     if (dt < 1 / 85f || temp <= 0f)
    //         return;
    //
    //     float condition = slot.Itemstack.Attributes.GetFloat("condition", 1);
    //
    //     // TODO: Update stored
    //     // TODO: Keep in mind that dt can be negative.
    //     base.ChangeCondition(stack.ItemSlot, delta);
    // }

    // We don't need to overwrite GetTemperature because it only lowers temperature and doesn't increase
    // it (even though it has a didReceiveHeat argument).

    // Called by the fireplace.
    // Note that delayCooldown functionality is intentionally disabled.
    public override void SetTemperature(IWorldAccessor world, ItemStack stack, float temperature, bool delayCooldown = true)
    {
        var attrs_ = stack?.Attributes?["temperature"];
        if (attrs_ == null)
        {
            // Set a baseline, we don't know the start time and thus cannot compute the real temperature change.
            // We simply allow a small temperature change without restrictions.
            temperature = temperature < 25 ? temperature : 25;
        }
        else if (attrs_ is ITreeAttribute attrs)
        {
            // We have a start timestamp + temperature => Compute how much our temperature has actually changed.
            float oldTemp = (float)attrs.GetDecimal("temperature", 20);
            double lastUpdate = attrs.GetDecimal("temperatureLastUpdate");
            double dt = world.Calendar.TotalHours - lastUpdate;

            // Probably quite inaccurate, but we don't have enough information to be physically accurate
            // anyways. Should be enough though.
            if (temperature > oldTemp)
                temperature = oldTemp + (temperature - oldTemp) * (Attributes?["heatingRate"]?.AsFloat() ?? 1);

            // // There are a few situations that can cause a negative time elapsed (e.g. via delayCooldown).
            // // If that is the case we don't change the temperature.
            // if (dt <= 0) return;
            //
            // // Smaller means slower response (slower temperature change)
            // double invtau = Attributes?.GetFloat("invtau", 0.1) ?? 0.1;
            // double alpha = 1 - Math.Exp(-dt * tau);
            //
            // // This is not quite physically acurate. It would be if temperature is the fireplace temperature,
            // // but this code runs after the fireplace computes a (hard-coded) new temperature (where we can't
            // // easily modify the rate of change).
            // //
            // // I didn't want to mess with that specific implementation because it would likely add
            // // incompatibilities with other mods that add heating/smelting.
            // temperature = oldTemp + (temperature - oldTemp) * alpha;
        }

        // Apply the "real" temperature.
        // If delayCooldown is true the lastUpdate time is set in the future, breaking the math/logic below.
        base.SetTemperature(world, stack, temperature, false);

        // Make sure it's still the correct value.
        if (stack?.Attributes?["temperature"] is ITreeAttribute attr)
            attr.SetFloat("cooldownSpeed", (Attributes?["cooldownSpeed"].AsFloat() ?? 90));
    }

    public override void GetHeldItemInfo(ItemSlot inSlot, StringBuilder dsc, IWorldAccessor world, bool withDebugInfo)
    {
        base.GetHeldItemInfo(inSlot, dsc, world, withDebugInfo);
    }
}
