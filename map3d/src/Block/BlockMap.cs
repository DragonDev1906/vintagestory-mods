using System.Text;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

#nullable enable

namespace Map3D;

public class BlockMap : Block
{
    public override bool OnBlockInteractStart(IWorldAccessor world, IPlayer byPlayer, BlockSelection blockSel)
    {
        BlockEntityMap entity = world.BlockAccessor.GetBlockEntity<BlockEntityMap>(blockSel.Position);
        if (entity == null)
        {
            api.ModLoader.GetModSystem<Map3DModSystem>().Mod.Logger.Warning("Block Entity no longer exists, please break and replace this block");
            if (api.Side == EnumAppSide.Client)
                ((IClientPlayer)byPlayer).ShowChatNotification("Block Entity no longer exists, please break and replace this block");
            return false;
        }
        return entity.OnBlockInteractStart(world, byPlayer, blockSel);
    }

    public override void AddExtraHeldItemInfoPostMaterial(ItemSlot inSlot, StringBuilder dsc, IWorldAccessor world)
    {
        base.AddExtraHeldItemInfoPostMaterial(inSlot, dsc, world);
        if (Attributes != null)
        {
            int maxDistance = Attributes["maxDistance"]?.AsInt() ?? 0;
            int maxSize = Attributes["maxSize"]?.AsInt() ?? 0;
            int maxOffset = Attributes["maxOffset"]?.AsInt() ?? 0;
            bool rotation = Attributes["rotation"]?.AsBool() ?? false;
            bool restrictRotation = Attributes["restrictedRotation"]?[Variant["type"]]?.AsBool() ?? false;

            if (maxSize > 0)
                dsc.AppendLine("Max Size: " + maxSize);
            if (maxDistance > 0)
                dsc.AppendLine("Max Distance: " + maxDistance);
            if (maxOffset > 0)
                dsc.AppendLine("Max Offset: " + maxOffset);

            if (rotation && restrictRotation)
                dsc.AppendLine("Allows basic rotations");
            else if (rotation)
                dsc.AppendLine("Allowed all rotations");
        }
    }
}
