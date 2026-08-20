using System.Text;
using Vintagestory.GameContent;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using System;

namespace Heatstone;

/// # Added/Relevant attributes
/// - "cooldownSpeed" (float): Previously just on the itemstack, now it can be used
///   to specify passive temperature drain.
/// - "hoursToDrainCondition" (float): Hours it takes to drain the items temperature
///   while wearing it. Does not include the time from cooldownSpeed.
/// - "disableDirectRepair" (bool): When true, prevents twine/linen/sewing kit from
///   being applied directly to this item in the inventory to repair it.
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
public class CollectibleBehaviorHeatWearable : CollectibleBehaviorWearable
{
    private ICoreAPI api;

    public CollectibleBehaviorHeatWearable(CollectibleObject collObj) : base(collObj) { }

    public override void OnLoaded(ICoreAPI api)
    {
        base.OnLoaded(api);
        this.api = api;
    }

    // We don't need to overwrite GetTemperature because it only lowers temperature and doesn't increase
    // it (even though it has a didReceiveHeat argument).

    // Called by the fireplace.
    // Note that delayCooldown functionality is intentionally disabled.
    public override void SetTemperature(IWorldAccessor world, ItemStack stack, float temperature, bool delayCooldown, ref EnumHandling handling)
    {
        ITreeAttribute attr = (ITreeAttribute)stack?.Attributes?["temperature"];
        if (attr == null)
        {
            stack.Attributes["temperature"] = (attr = new TreeAttribute());
        }

        // We have a start timestamp + temperature => Compute how much our temperature has actually changed.
        float oldTemp = (float)attr.GetDecimal("temperature", 20);
        double lastUpdate = attr.GetDecimal("temperatureLastUpdate");
        double now = world.Calendar.TotalHours;
        double dt = now - lastUpdate;

        // Probably quite inaccurate, but we don't have enough information to be physically accurate
        // anyways. Should be enough though.
        if (temperature > oldTemp)
            temperature = oldTemp + (temperature - oldTemp) * (collObj.Attributes?["heatingRate"]?.AsFloat() ?? 1);

        // The default impl always sets temperatureLastUpdate to now (same as we do),
        // lowers the temperature immediately if the current temp is higher, and
        // increases it by a fixed 0.5 if it is lower (on every call).
        // The main change we did was to make it independent of how often this function was called
        // (not sure why it is done based on how often it is called in the base game), and
        // change the heating amount to depend on heatingRate and the temperature difference
        // instead of being a flat amount.
        attr.SetDouble("temperatureLastUpdate", now);
        attr.SetFloat("temperature", temperature);

        handling = EnumHandling.PreventDefault;

        // Make sure it's still the correct value.
        // NOTE: Don't know if we still need this, keeping it for now.
        attr.SetFloat("cooldownSpeed", (collObj.Attributes?["cooldownSpeed"].AsFloat() ?? 90));
    }

    public override float GetWarmth(ItemSlot inslot)
    {
        float maxWarmth = inslot.Itemstack.ItemAttributes?["warmth"].AsFloat(0) ?? 0;
        float mul = inslot.Itemstack.ItemAttributes?["warmthPerTempDeg"].AsFloat(0) ?? 0;
        float temp = inslot.Itemstack.Item.GetTemperature(api.World, inslot.Itemstack);
        return Math.Max(0, Math.Min(maxWarmth, (temp - 20) * mul));
    }

    public override void GetHeldItemInfo(ItemSlot inSlot, StringBuilder dsc, IWorldAccessor world, bool withDebugInfo)
    {
        base.GetHeldItemInfo(inSlot, dsc, world, withDebugInfo);
    }

    public override int GetMergableQuantity(ItemStack sinkStack, ItemStack sourceStack, EnumMergePriority priority, ref EnumHandling handling)
    {
        if (collObj.Attributes?["disableDirectRepair"].AsBool() == true
            && priority == EnumMergePriority.DirectMerge
            && (sourceStack?.ItemAttributes?["clothingRepairStrength"].AsFloat(0) ?? 0) > 0)
        {
            handling = EnumHandling.PreventDefault;
            return 0;
        }

        return base.GetMergableQuantity(sinkStack, sourceStack, priority, ref handling);
    }

    public override void TryMergeStacks(ItemStackMergeOperation op, ref EnumHandling handling)
    {
        if (collObj.Attributes?["disableDirectRepair"].AsBool() == true
            && op.CurrentPriority == EnumMergePriority.DirectMerge
            && (op.SourceSlot.Itemstack.ItemAttributes?["clothingRepairStrength"].AsFloat(0) ?? 0) > 0)
        {
            handling = EnumHandling.PreventDefault;
            return;
        }

        base.TryMergeStacks(op, ref handling);
    }
}
