using System;
using System.Reflection;
using Vintagestory.API.Client;
using Vintagestory.API.Server;
using Vintagestory.API.Config;
using Vintagestory.API.Common;
using Vintagestory.Common;

namespace TranslocatorDirectionIndicator;

public class TranslocatorDirectionIndicatorModSystem : ModSystem
{
    // Called on server and client
    // Useful for registering block/entity classes on both sides
    public override void Start(ICoreAPI api)
    {
        api.Logger.Notification("Hello from template mod: " + api.Side);
        base.Start(api);

        api.RegisterBlockEntityBehaviorClass(Mod.Info.ModID + ".TranslocatorDirectionVis", typeof(BEBehaviorTranslocatorDirectionVis));
    }

    public override void StartServerSide(ICoreServerAPI api)
    {
        api.Logger.Notification("Hello from template mod server side: " + Lang.Get("translocatordirectionindicator:hello"));
    }

    public override void StartClientSide(ICoreClientAPI api)
    {
        api.Logger.Notification("Hello from template mod client side: " + Lang.Get("translocatordirectionindicator:hello"));
    }
}
