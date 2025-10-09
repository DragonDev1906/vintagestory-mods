using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace Map3D;

public class BlockMap : Block
{
    public override bool OnBlockInteractStart(IWorldAccessor world, IPlayer byPlayer, BlockSelection blockSel)
    {
        BlockEntityMap entity = world.BlockAccessor.GetBlockEntity<BlockEntityMap>(blockSel.Position);
        if (entity == null)
        {
            api.Logger.Warning("Block Entity no longer exists, please break and replace this block");
            if (api.Side == EnumAppSide.Client)
                ((IClientPlayer)byPlayer).ShowChatNotification("Block Entity no longer exists, please break and replace this block");
            return false;
        }
        return entity.OnBlockInteractStart(world, byPlayer, blockSel);
    }
}
