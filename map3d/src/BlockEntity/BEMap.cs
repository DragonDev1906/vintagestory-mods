using ProtoBuf;
using Vintagestory.API.Common;
using Vintagestory.API.Client;
using Vintagestory.API.Server;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Util;
using Vintagestory.Common;
using System.Text;
using System;

namespace Map3D;

internal class BlockEntityMap : BlockEntity
{
    // Can be null even if we know which dimensionID we need/use.
    internal MapMiniDimension? dimension;

    // Configuration for block copying and where the blocks are stored.
    private int dimId;
    internal MapMode mode;
    internal Lod lod;
    internal BlockPos center = null!; // Set during Initialize
    internal Vec3i srcSize = new Vec3i(101, 256, 101);

    private Map3DModSystem system = null!; // Set during Initialize

    private double distanceSq
    {
        get
        {
            int dx = Pos.X - center.X;
            int dz = Pos.Z - center.Z;
            return dx * dx + dz * dz;
        }
    }
    private double distance
    {
        get
        {
            return Math.Sqrt(distanceSq);
        }
    }

    // How and where should it be rendered.
    internal Vec3i offset = Vec3i.Zero; // In voxels (1/32 pixel)
    internal Vec3f rotation = Vec3f.Zero;
    internal int size = 32; // In voxels

    // Specifying a size is more intuitive than specifying a scale, so that's what
    // we primarily use. For rendering a scale is way more useful though.
    internal float scale
    {
        get
        {
            int dataSize = Math.Max(srcSize.X, srcSize.Z);
            return (float)this.size / (float)dataSize / 32f;
        }
    }

    private GuiDialogMap3d? dlg;

    private void CreateDimensionClientSide(ICoreClientAPI api)
    {
        dimension = new MapMiniDimension((BlockAccessorBase)api.World.BlockAccessor, Pos.ToVec3d(), api, scale);
        api.World.MiniDimensions[dimId] = dimension; // Usually done by GetOrCreateDimension
        dimension.SetSubDimensionId(dimId);
        dimension.CurrentPos = new(Pos.X, Pos.Y, Pos.Z);
        // We need to set this, otherwise Render will just ignore this dimension.
        dimension.selectionTrackingOriginalPos = Pos;

        // Tell the server we're ready to receive chunks (this explicity opt-in lets us avoid performance problems
        // in the default MiniDimension implementation when there is a significant amount of chunks).
        // This is also where we could let the player decide if he even wants chunks from this dimension.
        api.Network.SendBlockEntityPacket(Pos, (int)MapPacket.Ready4Chunks);
    }

    // Interesting/Relevant
    // - BroadcastChunk: https://apidocs.vintagestory.at/api/Vintagestory.API.Server.IWorldManagerAPI.html#Vintagestory_API_Server_IWorldManagerAPI_BroadcastChunk_System_Int32_System_Int32_System_Int32_System_Boolean_
    // - ForceSendChunkColumn: https://apidocs.vintagestory.at/api/Vintagestory.API.Server.IWorldManagerAPI.html#Vintagestory_API_Server_IWorldManagerAPI_ForceSendChunkColumn_Vintagestory_API_Server_IServerPlayer_System_Int32_System_Int32_System_Int32_
    // - Block name to ID: https://apidocs.vintagestory.at/api/Vintagestory.API.Server.IWorldManagerAPI.html#Vintagestory_API_Server_IWorldManagerAPI_GetBlockId_Vintagestory_API_Common_AssetLocation_
    // - LoadChunkColumn: https://apidocs.vintagestory.at/api/Vintagestory.API.Server.IWorldManagerAPI.html#Vintagestory_API_Server_IWorldManagerAPI_LoadChunkColumn_System_Int32_System_Int32_System_Boolean_
    //   - Better: https://apidocs.vintagestory.at/api/Vintagestory.API.Server.IWorldManagerAPI.html#Vintagestory_API_Server_IWorldManagerAPI_LoadChunkColumnForDimension_System_Int32_System_Int32_System_Int32_
    // - Might be useful for loading world-gen without changing the existing chunks: https://apidocs.vintagestory.at/api/Vintagestory.API.Server.IWorldManagerAPI.html#Vintagestory_API_Server_IWorldManagerAPI_PeekChunkColumn_System_Int32_System_Int32_Vintagestory_API_Server_ChunkPeekOptions_

    public override void Initialize(ICoreAPI api)
    {
        base.Initialize(api);
        system = Api.ModLoader.GetModSystem<Map3DModSystem>();

        // Avoid the risk of recursion.
        if (Pos.dimension != 0)
        {
            return;
        }

        // These can differ from the current position, but these are some sane defaults.
        if (center == null)
            center = Pos;

        // Create the dimension when this BE is loaded, thus it should
        // work for other players that have not interacted with the MapDisplay.
        if (api is ICoreClientAPI capi && dimId > 0)
        {
            // I'm not completely sure if/when this happens, as network communication
            // should happen through FromTreeAttributes.
            //
            // I think this only works if the server-side dimension is created on interaction,
            // as the server-side init is probably called first and thus we don't have a 
            // matching mini dimension.
            system.Mod.Logger.Notification("Creating dimension {0} at {1}", dimId, Pos);
            CreateDimensionClientSide(capi);
            UpdateDimension();
        }

        // NOTE: We may have to initialize the chunks on the client side first or ask for the chunks manually.
        if (api is ICoreServerAPI sapi && dimId > 0)
        {
            // Either this doesn't connect to the save file properly (and overrides what is there)
            // Or the "doesn't send chunks to new/joining players" restriction doesn't send
            // the entire chunk if we modify a block.
            //
            // Either way: This limitation is a bummer.
            //
            // In the future we may need/want to have our custom dimension class on the server
            // side, too. But I don't think we need that yet (it only changes the render matrix).

            dimension = new MapMiniDimension((BlockAccessorBase)api.World.BlockAccessor, Pos.ToVec3d(), sapi, 0.1f);
            // dimension = sapi.World.BlockAccessor.CreateMiniDimension(Pos.ToVec3d());
            int id = sapi.Server.SetMiniDimension(dimension, dimId);
            dimension.SetSubDimensionId(dimId);

            // Load the chunks
            // We could also lazily load these only after the first player said he is ready for chunks.
            dimension.LoadChunksServer(srcSize);
        }
    }

    public override void OnBlockRemoved()
    {
        if (dimension != null)
            system.FreeMiniDimension(dimension);
        // Probably not neccessarily to reset these, but it won't hurt.
        dimension = null;
        dimId = 0;
        base.OnBlockRemoved();
    }

    public override void OnBlockUnloaded()
    {
        if (dimension != null)
        {
            // Unfortunately, this doesn't actually unload the chunks, unless that's done
            // in the background when only referenced by the chunk manager. It doesn't
            // seem possible to unload all chunks in this dimension. The only way I can
            // think of is keeping our own list of chunk indicies, destroying the dimension
            // and manually telling the game to unload these chunks, as the BlockAccessorMovable
            // only allows unloading chunks that are empty. For now we only destroy / unregister
            // the mini dimension so it isn't iterated in every tick.

            // At the moment there doesn't seem to be a way to unbload/remove a mini dimension
            // on the server side. The best we could do is to keep it around and exit early,
            // which is already the case (see CollectChunksForSending). Since we cannot unload
            // it it's probably best to keep the reference stored here, that way we don't risk
            // a new dimension accidentally destroying chunk data.

            // Ideally we'd have a Hysteresis to not unload and load this dimension repeatedly
            // if the player is near the edge of render distance, but I haven't yet found a
            // good way to implement that.

            if (Api is ICoreClientAPI capi)
            {
                system.Mod.Logger.Notification("Unloading dimension {0} at {1}", dimId, Pos);
                // This stops rendering the mini dimension. Not sure if it deletes the chunk data.
                capi.World.MiniDimensions.Remove(dimId);
                dimension = null;
            }
            else
                system.Mod.Logger.Notification("Cannot unload dimension {0} at {1}", dimId, Pos);
        }
        base.OnBlockUnloaded();
    }

    public override void FromTreeAttributes(ITreeAttribute tree, IWorldAccessor worldAccessForResolve)
    {
        base.FromTreeAttributes(tree, worldAccessForResolve);

        // Load/Overwrite all the data
        int oldId = dimId;
        dimId = tree.GetAsInt("dimensionId");
        mode = (MapMode)tree.GetInt("mode");
        lod = (Lod)tree.GetInt("lod");
        center = tree.GetBlockPos("center");
        Vec3i? srcSize = tree.GetVec3i("srcSize");
        if (srcSize != null)
            this.srcSize = srcSize;
        offset = tree.GetVec3i("offset", Vec3i.Zero);
        float rotX = tree.GetFloat("rotX", 0);
        float rotY = tree.GetFloat("rotY", 0);
        float rotZ = tree.GetFloat("rotZ", 0);
        rotation = new(rotX, rotY, rotZ);
        size = tree.GetInt("size", 32);

        // If the dimension is created on the server-side this function is called. Since we didn't have dimID
        // previously we have not created the client-side dimension in Initialize, so we must do it now.
        if (Api is ICoreClientAPI api && oldId != dimId && dimId > 0)
        {
            system.Mod.Logger.Notification("Creating new dimension {0} at {1}", dimId, Pos);
            CreateDimensionClientSide(api);
        }
        UpdateDimension();
    }

    public override void ToTreeAttributes(ITreeAttribute tree)
    {
        base.ToTreeAttributes(tree);

        // Send dimensionId
        tree.SetInt("dimensionId", dimId);
        tree.SetInt("mode", (int)mode);
        tree.SetInt("lod", (int)lod);
        if (center != null)
            tree.SetBlockPos("center", center);
        if (srcSize != null)
            tree.SetVec3i("srcSize", srcSize);
        if (offset != null)
            tree.SetVec3i("offset", offset);
        if (rotation != null)
        {
            tree.SetFloat("rotX", rotation.X);
            tree.SetFloat("rotY", rotation.Y);
            tree.SetFloat("rotZ", rotation.Z);
        }
        tree.SetInt("size", size);
    }

    public override void OnReceivedClientPacket(IPlayer fromPlayer, int packetid, byte[] data)
    {
        switch (packetid)
        {
            case (int)MapPacket.UpdateDimension:
                {
                    UpdateDimensionPacket p = SerializerUtil.Deserialize<UpdateDimensionPacket>(data);
                    // Be defensive and don't change something if we get null.
                    if (p.center != null)
                    {
                        // Check validity on the server-side (not just in the client-side GUI)
                        int maxDistance = Block.Attributes?["maxDistance"]?.AsInt() ?? 0;
                        long dx = Pos.X - p.center.X;
                        long dz = Pos.Z - p.center.Z;
                        if (dx * dx + dz * dz > maxDistance * maxDistance)
                        {
                            system.Mod.Logger.Warning("Player '{0}' sent invalid UpdateDimensionPacket at {1}", fromPlayer.PlayerName, Pos);
                            return;
                        }
                        this.center = p.center;
                    }
                    if (p.size != null)
                        this.srcSize = p.size;
                    this.mode = p.mode;
                    this.lod = p.lod;
                    UpdateDimensionDataServer();
                    break;
                }
            case (int)MapPacket.Configure:
                {
                    ConfigurePacket p = SerializerUtil.Deserialize<ConfigurePacket>(data);
                    ApplyConfigPacket(p);
                }
                break;
            case (int)MapPacket.Ready4Chunks:
                if (dimension != null)
                    dimension.SetPlayerReady(fromPlayer, true);
                else
                    system.Mod.Logger.Warning("Player {0} sent Ready4Chunks but dimension {1} doesn't exist", fromPlayer.PlayerName, dimId);
                break;
            default:
                base.OnReceivedClientPacket(fromPlayer, packetid, data);
                break;
        }
    }
    // Called server-side when receiving new configuration settings from a client
    // and client-side when this packet is sent (to hide latency).
    // It is NOT called for other players that happen to be nearby (that's done
    // through FromTreeAttributes).
    internal void ApplyConfigPacket(ConfigurePacket p)
    {
        bool allowRotation = Block.Attributes?["rotation"]?.AsBool() ?? false;
        bool restrictedRotation = Block.Attributes?["restrictedRotation"]?[Block.Variant["type"]]?.AsBool() ?? false;
        int maxOffset = Block.Attributes?["maxOffset"]?.AsInt() ?? 0;
        int maxSize = Block.Attributes?["maxSize"]?.AsInt() ?? 0;

        // Limit the offset
        if (p.offset != null)
        {
            offset = ClampVec3iInplace(p.offset, -32 * maxOffset, 32 * maxOffset);
        }
        // Only apply rotation if it is allowed
        if (allowRotation && restrictedRotation)
        {
            rotation.Y = 0;
            rotation.Z = 0;
        }
        if (allowRotation && p.rotation != null)
        {
            rotation = p.rotation;
        }
        // Limit the scale (we could calculate the scale from the bounding box size,
        // but I think this is more flexible and it is the approach I previously had).
        this.size = Math.Clamp(p.size, 1, 32 * maxSize);

        // Tell the dimension about its new configuration
        UpdateDimension();
        MarkDirty();
    }

    Vec3i ClampVec3iInplace(Vec3i v, int min, int max)
    {
        v.X = Math.Clamp(v.X, min, max);
        v.Y = Math.Clamp(v.Y, min, max);
        v.Z = Math.Clamp(v.Z, min, max);
        return v;
    }
    public override void OnReceivedServerPacket(int packetid, byte[] data)
    {
        switch (packetid)
        {
            case (int)MapPacket.ClearDimension:
                if (dimension != null)
                    dimension.ClearChunks();
                break;
            default:
                base.OnReceivedServerPacket(packetid, data);
                break;
        }
    }

    internal void UpdateDimension()
    {
        if (dimension != null)
        {
            float xcenter = Block.Attributes?["center"]?["x"]?.AsFloat() ?? 0;
            float ycenter = Block.Attributes?["center"]?["y"]?.AsFloat() ?? 0;
            float zcenter = Block.Attributes?["center"]?["z"]?.AsFloat() ?? 0;

            bool allowRotation = Block.Attributes?["rotation"]?.AsBool() ?? false;
            bool restrictedRotation = Block.Attributes?["restrictedRotation"]?[Block.Variant["type"]]?.AsBool() ?? false;

            dimension.CurrentPos.X = Pos.X + xcenter + offset.X / 32f;
            dimension.CurrentPos.Y = Pos.Y + ycenter + offset.Y / 32f;
            dimension.CurrentPos.Z = Pos.Z + zcenter + offset.Z / 32f;
            if (allowRotation && rotation != null)
            {
                dimension.CurrentPos.Yaw = rotation.X;
                if (!restrictedRotation)
                {
                    dimension.CurrentPos.Pitch = rotation.Y;
                    dimension.CurrentPos.Roll = rotation.Z;
                }
            }
            dimension.scale = scale;
        }
    }

    public bool OnBlockInteractStart(IWorldAccessor world, IPlayer byPlayer, BlockSelection blockSel)
    {
        if (Api is ICoreClientAPI capi)
        {
            // CreateDimensionClientSide(capi);

            // This will probably fail because we currently don't set the dimension.
            if (dlg == null || !dlg.IsOpened())
            {
                dlg = new GuiDialogMap3d("3D Map settings", capi, this);
                dlg.OnClosed += () => { dlg.Dispose(); dlg = null; };
                dlg.TryOpen();
            }
        }

        if (Api is not ICoreServerAPI sapi)
        {
            return true;
        }

        if (dimId == 0)
        {
            // First time someone interacted with this block.
            // For now we create a dimension immediately, later we'all
            // want a UI to select how that dimension should behave.
            dimension = new MapMiniDimension((BlockAccessorBase)sapi.World.BlockAccessor, Pos.ToVec3d(), Api, scale);
            dimId = Map3DModSystem.Instance(sapi).AllocateMiniDimensionServer(dimension);

            // Send dimensionId to the client
            MarkDirty();
        }
        else
        {
            // If the dimension is created here everything works.
            if (dimension == null)
            {
                // This should be done in init, but for testing if the code works this place is more useful.
                // This way the chunk is guaranteed to exist on the client-side first (I think).
                dimension = new MapMiniDimension((BlockAccessorBase)sapi.World.BlockAccessor, Pos.ToVec3d(), Api, scale);
                int id = sapi.Server.SetMiniDimension(dimension, dimId);
                dimension.SetSubDimensionId(dimId);
                system.Mod.Logger.Notification("Creating new dimension {0} at {1}", dimId, Pos);

            }

            // TODO: Do we need this?
            MarkDirty();
        }

        return true;
    }


    // Should be called server-side.
    public void UpdateDimensionDataServer()
    {
        if (dimension == null)
        {
            system.Mod.Logger.Error("Called UpdateDimensionData without having a dimension at {0}", Pos);
            return;
        }

        system.Mod.Logger.Notification("Updating dimension data {0} at {1}", dimId, Pos);

        dimension.ClearChunks();
        if (Api is ICoreServerAPI sapi)
            sapi.Network.BroadcastBlockEntityPacket(Pos, (int)MapPacket.ClearDimension);

        // dimension.UnloadUnusedServerChunks();

        var watch = System.Diagnostics.Stopwatch.StartNew();

        switch (mode)
        {
            case MapMode.Copy:
                copyToDimensionV2Server();
                break;
            case MapMode.Surface:
                copySurfaceToDimensionServer();
                break;
        }

        watch.Stop();
        // TODO: I think this is no longer useful, as it is done asynchronously.
        system.Mod.Logger.Notification("UpdateDimensionData with mode {0} finished in {1} ms", mode, watch.ElapsedMilliseconds);

        MarkDirty();
    }

    private void copyToDimensionV2Server()
    {
        // This is what our sub dimension coordinates are based on.
        int sx = srcSize.X;
        int sz = srcSize.Z;

        // Start with the center of the subdimension, see AdjustPosForSubDimension which
        // does this calculation with block coordinates instead of chunk coordinates.
        int cxmid = (dimId % 4096) * 512 + 256;
        int czmid = (dimId / 4096) * 512 + 256;

        int shift = lod.shift();
        // Go to the lower edge (and do the correct rounding).
        // We need to divide by 2 because we're centered, then we need to divide by 32
        // to get chunk coordinates. But we need to round up, otherwise we'll be missing data.
        // Since we want the chunks to be centered after applying LOD we additionally have to
        // divide by the LOD size (or shift by its amount).
        int cxstart = cxmid - ((sx + 62) >> (6 + shift));
        int czstart = czmid - ((sz + 62) >> (6 + shift));

        system.LoadChunksV2(new CopyRequest(
            dimension!, // Only called if dimension is not null
            new BlockPos(
                center.X - srcSize.X / 2,
                0,
                center.Z - srcSize.Z / 2
            ),
            new BlockPos(32 * cxmid - sx / 2, 0, 32 * czmid - sz / 2, 1),
            srcSize
        ));
    }

    private void copySurfaceToDimensionServer()
    {
        int sx = srcSize.X;
        int sy = srcSize.Y;
        int sz = srcSize.Z;

        BlockPos corner1 = center - new BlockPos(srcSize / 2, 0);
        BlockPos corner2 = corner1 + new BlockPos(srcSize, 0);

        // Walk through all chunks. We start with z so we get the same order as when walking
        // the blocks (makes neighbor detection easier if we add/need that). We go over y
        // last because we don't need to iterate over all Y chunks.

        BlockPos offset = new BlockPos(-sx / 2 - corner1.X, 0, -sz / 2 - corner1.Z, 1);
        // Private function that is only called when the dimension exists
        dimension!.AdjustPosForSubDimension(offset);
        int zmin = corner1.Z & 0x1f;
        for (int cz = corner1.Z >> 5; cz <= corner2.Z >> 5; cz++)
        {
            int xmin = corner1.X & 0x1f;
            int zmax = 32;
            if (cz + 1 > corner2.Z >> 5)
                zmax = corner2.Z & 0x1f;

            for (int cx = corner1.X >> 5; cx <= corner2.X >> 5; cx++)
            {
                int xmax = 32;
                if (cx + 1 > corner2.X >> 5)
                    xmax = corner2.X & 0x1f;

                IWorldChunk[] chunkColumn = new IWorldChunk[8];
                int cSurface = 0;
                bool notGenerated = false;
                for (int cy = 0; cy < 8; cy++)
                {
                    chunkColumn[cy] = Api.World.BlockAccessor.GetChunk(cx, cy, cz);
                    if (chunkColumn[cy] == null)
                    {
                        notGenerated = true;
                        break;
                    }
                    chunkColumn[cy].Unpack();
                    // Optimization to skip empty chunks in the per-block iteration.
                    if (!chunkColumn[cy].Empty)
                    {
                        cSurface = ((cy + 1) << 5) - 1;
                    }
                }
                if (notGenerated)
                    continue;

                // PERFORMANCE: We could probably change this iteration order to improve cache-friendliness.
                // It's not a trivial change though.
                // TODO: This is not accurate copying, but we probably want to keep chunk alignment for
                // cheaper copying, especially since it is easy to adjust when rendering.
                // TODO: Copy only what we need.
                for (int z = zmin; z < zmax; z++)
                {
                    for (int x = xmin; x < xmax; x++)
                    {
                        for (int y = cSurface; y >= 0; y--)
                        {
                            var chunk = chunkColumn[y >> 5];
                            int index3d = x + (z << 5) + (y << 10);
                            int blockid = chunk.Data.GetBlockId(index3d, 0);
                            if (blockid == 0)
                            {
                                continue;
                            }

                            var dst = new BlockPos(
                                32 * cx + x + offset.X,
                                offset.Y + y,
                                32 * cz + z + offset.Z,
                                1
                            );
                            dimension.ExchangeBlock(blockid, dst);

                            // Go to next column if we reached the last block we want.
                            if (Api.World.Blocks[blockid].AllSidesOpaque)
                                break;
                        }
                    }
                }
                xmin = 0;
            }
            zmin = 0;
        }
    }

    public override void GetBlockInfo(IPlayer forPlayer, StringBuilder dsc)
    {
        // Offset to convert to the coordinate system usually displayed to the player.
        const int D = 512000;

        base.GetBlockInfo(forPlayer, dsc);
        dsc.AppendLine("<font color=\"#99c9f9\"><i>Breaking this block will remove map data + configuration</i></font>");
        dsc.AppendLine("");
        dsc.AppendLine(String.Format("Center: ({0},{1})", center.X - D, center.Z - D));
        dsc.AppendLine(String.Format("Relative: ({0},{1})", center.X - Pos.X, center.Z - Pos.Z));
        dsc.AppendLine(String.Format("Size: ({0},{1})", srcSize.X, srcSize.Z));
        dsc.AppendLine("Distance: " + distance);
        dsc.AppendLine("");
        dsc.AppendLine("DimensionId: " + dimId);
        dsc.AppendLine("Size: " + (size / 32f) + " blocks");
        dsc.AppendLine("Scale: " + scale * 100 + "%");
    }
}

internal enum MapMode
{
    Copy = 0,
    Surface = 1,
}


internal enum MapPacket
{
    // Use a random starting location to avoid colisions with other mods.
    UpdateDimension = 0x5a7cd100, // Client -> Server
    Configure,       // Client -> Server
    ClearDimension,  // Server -> Client
    Ready4Chunks,            // Client -> Server: Opt-into the chunks from that dimension
}

[ProtoContract]
internal class UpdateDimensionPacket
{
    [ProtoMember(1)]
    public MapMode mode;
    [ProtoMember(2)]
    public BlockPos? center; // Cannot enforce these is non-null when malicious clients are involved, so let's assume they can be null.
    [ProtoMember(3)]
    public Vec3i? size;
    [ProtoMember(4)]
    public Lod lod;

    public UpdateDimensionPacket() { }
    public UpdateDimensionPacket(MapMode mode, BlockPos? center, Vec3i? size, Lod lod)
    {
        this.mode = mode;
        this.center = center;
        this.size = size;
        this.lod = lod;
    }
}

[ProtoContract]
internal class ConfigurePacket
{
    [ProtoMember(1)]
    public Vec3i? offset; // In voxels
    [ProtoMember(2)]
    public Vec3f? rotation; // In °
    [ProtoMember(3)]
    public int size; // In voxels

    public ConfigurePacket() { }
    public ConfigurePacket(Vec3i? offset, Vec3f? rotation, int size)
    {
        this.offset = offset;
        this.rotation = rotation;
        this.size = size;
    }
}
