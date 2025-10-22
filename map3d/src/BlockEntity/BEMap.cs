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
    internal MapMiniDimension dimension;

    // Configuration for block copying and where the blocks are stored.
    private int dimId;
    internal MapMode mode;
    internal Lod lod;
    internal BlockPos corner1;
    internal BlockPos corner2;

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
            int dataSize = Math.Max(corner2.X - corner1.X, corner2.Z - corner1.Z);
            return (float)this.size / (float)dataSize / 32f;
        }
    }

    private GuiDialogMap3d dlg;

    private void CreateDimensionClientSide(ICoreClientAPI api)
    {
        api.Logger.Notification("Dimension: " + api.World.MiniDimensions.TryGetValue(dimId) + ".");
        dimension = new MapMiniDimension((BlockAccessorBase)api.World.BlockAccessor, Pos.ToVec3d(), api, scale);
        api.World.MiniDimensions[dimId] = dimension; // Usually done by GetOrCreateDimension
        dimension.SetSubDimensionId(dimId);
        dimension.CurrentPos = new(Pos.X, Pos.Y, Pos.Z);
        // We need to set this, otherwise Render will just ignore this dimension.
        dimension.selectionTrackingOriginalPos = Pos;
        api.Logger.Notification("Dimension created on client-side: " + dimId.ToString() + " : " + Pos.ToVec3d().ToString());
        UpdateDimension();
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

        // Avoid the risk of recursion.
        if (Pos.dimension != 0)
        {
            return;
        }

        // These can differ from the current position, but these are some sane defaults.
        if (corner1 == null)
            corner1 = Pos.AddCopy(-100, 0, -100);
        if (corner2 == null)
            corner2 = Pos.AddCopy(100, 0, 100);

        api.Logger.Notification("Initialize: " + dimId);
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
            CreateDimensionClientSide(capi);
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
            api.Logger.Notification("SetMiniDimension: " + dimId + ":" + id);
        }
    }

    public override void OnBlockRemoved()
    {
        if (dimension != null)
            Api.ModLoader.GetModSystem<Map3DModSystem>().FreeMiniDimension(dimension);
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
                Api.Logger.Notification("Unloading dimension {0}", dimId);
                // This stops rendering the mini dimension. Not sure if it deletes the chunk data.
                capi.World.MiniDimensions.Remove(dimId);
                dimension = null;
            }
            else
                Api.Logger.Notification("Would love to unload dimension on server side {0}", dimId);
        }
        base.OnBlockUnloaded();
    }

    public override void FromTreeAttributes(ITreeAttribute tree, IWorldAccessor worldAccessForResolve)
    {
        base.FromTreeAttributes(tree, worldAccessForResolve);

        int oldId = dimId;
        dimId = tree.GetAsInt("dimensionId");
        mode = (MapMode)tree.GetInt("mode");
        lod = (Lod)tree.GetInt("lod");
        corner1 = tree.GetBlockPos("corner1");
        corner2 = tree.GetBlockPos("corner2");
        offset = tree.GetVec3i("offset", Vec3i.Zero);
        float rotX = tree.GetFloat("rotX", 0);
        float rotY = tree.GetFloat("rotY", 0);
        float rotZ = tree.GetFloat("rotZ", 0);
        rotation = new(rotX, rotY, rotZ);
        size = tree.GetInt("size", 32);

        // Api.Logger.Notification("FromTreeAttributes: " + oldId + " -> " + dimensionId);
        if (Api is ICoreClientAPI api && oldId != dimId && dimId > 0)
        {
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
        if (corner1 != null)
            tree.SetBlockPos("corner1", corner1);
        if (corner2 != null)
            tree.SetBlockPos("corner2", corner2);
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
                    // Check validity on the server-side (not just in the client-side GUI)
                    if (setCorners(p.corner1, p.corner2))
                    {
                        this.mode = p.mode;
                        this.lod = p.lod;
                        UpdateDimensionData();
                    }
                    else
                    {
                        Api.Logger.Warning("Client sent invalid corners");
                    }
                }
                break;
            case (int)MapPacket.Configure:
                {
                    ConfigurePacket p = SerializerUtil.Deserialize<ConfigurePacket>(data);
                    ApplyConfigPacket(p);
                }
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
        if (allowRotation)
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
            capi.Logger.Notification("Dimension count: " + capi.World.MiniDimensions.Count);
            // CreateDimensionClientSide(capi);

            // This will probably fail because we currently don't set the dimension.
            if (dimension != null && (dlg == null || !dlg.IsOpened()))
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
            dimId = Map3DModSystem.Instance(sapi).AllocateMiniDimension(dimension);

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
                Api.Logger.Notification("SetMiniDimension: " + dimId + ":" + id);

            }

            // TODO: Do we need this?
            MarkDirty();

            LoadChunks();
        }

        return true;
    }

    // Returns true if the corners are valid (does nothing if they are not).
    private bool setCorners(BlockPos corner1, BlockPos corner2)
    {
        // Normalize corners so the smaller coordinate is always in corner1.
        if (corner1.X > corner2.X)
        {
            int tmp = corner1.X;
            corner1.X = corner2.X;
            corner2.X = tmp;
        }
        if (corner1.Y > corner2.Y)
        {
            int tmp = corner1.Y;
            corner1.Y = corner2.Y;
            corner2.Y = tmp;
        }
        if (corner1.Z > corner2.Z)
        {
            int tmp = corner1.Z;
            corner1.Z = corner2.Z;
            corner2.Z = tmp;
        }

        // Check on server-side if both corners are within maxDistance.
        int maxDistance = Block.Attributes?["maxDistance"]?.AsInt() ?? 0;
        bool valid = maxDistance == 0 || (
            corner1.X - Pos.X >= -maxDistance && corner2.X - Pos.X <= maxDistance &&
            corner1.Y >= 0 && corner2.Y <= 256 &&
            corner1.Z - Pos.Z >= -maxDistance && corner2.Z - Pos.Z <= maxDistance
        );

        // Only set the corners if they are valid.
        if (valid)
        {
            this.corner1 = corner1;
            this.corner2 = corner2;
        }
        return valid;
    }

    // TODO: Don't load all chunks in a single tick, that results in a giant lag spike.
    private void LoadChunks()
    {
        if (Api is ICoreServerAPI sapi)
        {
            // TODO: Make sure the following gets updated to the new chunk aligned copying.

            // This is what our sub dimension coordinates are based on.
            int sx = corner2.X - corner1.X + 1;
            int sz = corner2.Z - corner1.Z + 1;

            // Start with the center of the subdimension, see AdjustPosForSubDimension which
            // does this calculation with block coordinates instead of chunk coordinates.
            int cxmid = (dimId % 4096) * 512 + 256;
            int czmid = (dimId / 4096) * 512 + 256;

            // Go to the lower edge (and do the correct rounding).
            // We need to divide by 2 because we're centered, then we need to divide by 32
            // to get chunk coordinates. But we need to round up, otherwise we'll be missing data.
            int cxstart = cxmid - ((sx + 62) >> 6);
            int czstart = czmid - ((sz + 62) >> 6);

            // From now on we need the size in chunks.
            sx = (sx + 31) / 32;
            sz = (sz + 31) / 32;

            sapi.Logger.Notification("Loading map3d chunks: ({0},{1}), size = ({2},{3})", cxstart, czstart, sx, sz);



            ChunkRequest req = ChunkRequest.SimpleLoad(
                dimension.OnChunkLoaded,
                cxstart, 1024, czstart,
                sx, 8, sz,
                Lod.None
            );
            sapi.ModLoader.GetModSystem<Map3DModSystem>().LoadChunksV2(req);



            // int sx = corner2.X - corner1.X;
            // int sy = corner2.X - corner1.X;
            // var iter = Iter.ChunksInMinidim(Iter.Rect(-sx / 64, -sy / 64, sx / 64, sy / 64), dimId);
            // sapi.ModLoader.GetModSystem<Map3DModSystem>().LoadChunks(iter);



            // // We need to divide by 2 to get to the edge => Shift by 1
            // // And we need to divide by 32 to convert to chunk coordinates => Shift by 5
            // // Both together: Shift by 6.
            // for (int cx = cxstart; cx <= cxend; cx++)
            //     for (int cz = czstart; cz <= czend; cz++)
            //         sapi.WorldManager.LoadChunkColumnForDimension(cx, cz, 1);
        }
    }

    // Should be called server-side.
    public void UpdateDimensionData()
    {
        Api.Logger.Notification("Update Dimension Data");

        dimension.ClearChunks();
        if (Api is ICoreServerAPI sapi)
            sapi.Network.BroadcastBlockEntityPacket(Pos, (int)MapPacket.ClearDimension);

        // dimension.UnloadUnusedServerChunks();

        var watch = System.Diagnostics.Stopwatch.StartNew();

        switch (mode)
        {
            case MapMode.Copy:
                copyToDimensionV2();
                break;
            case MapMode.Surface:
                copySurfaceToDimension();
                break;
        }

        watch.Stop();
        Api.Logger.Notification("UpdateDimensionData with mode {0} finished in {1} ms", mode, watch.ElapsedMilliseconds);

        MarkDirty();
    }

    private void copyToDimensionV2()
    {
        // This is what our sub dimension coordinates are based on.
        int sx = corner2.X - corner1.X + 1;
        int sz = corner2.Z - corner1.Z + 1;

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

        // From now on we need the size in chunks.
        sx = (sx + 31) / 32;
        sz = (sz + 31) / 32;

        Api.Logger.Notification("Copying map3d chunks: ({0},{1}), size = ({2},{3})", cxstart, czstart, sx, sz);

        var req = new ChunkRequest(
            dimension.OnChunkLoaded,
            corner1.X / 32, corner1.Y / 32, corner1.Z / 32,
            sx, 8, sz,
            cxstart, 1024, czstart,
            lod, RequestType.Copy
        );
        Api.ModLoader.GetModSystem<Map3DModSystem>().LoadChunksV2(req);
    }

    private void copyToDimension()
    {
        // Probably the least efficient way to do this.
        int x0 = corner1.X;
        int y0 = corner1.Y;
        int z0 = corner1.Z;
        int xlen = corner2.X - corner1.X;
        int ylen = corner2.Y - corner1.Y;
        int zlen = corner2.X - corner1.X;
        int x0dst = -xlen / 2;
        int y0dst = corner1.Y;
        int z0dst = -zlen / 2;
        for (int x = 0; x < xlen; x++)
            for (int z = 0; z < zlen; z++)
                for (int y = 0; y < ylen; y++)
                {
                    int blockid = Api.World.BlockAccessor.GetBlockId(new BlockPos(x0 + x, y0 + y, z0 + z, 0));
                    var dst = new BlockPos(x0dst + x, y0dst + y, z0dst + z, 1);
                    dimension.AdjustPosForSubDimension(dst);
                    dimension.ExchangeBlock(blockid, dst);
                }
    }

    private void copySurfaceToDimension()
    {
        int sx = corner2.X - corner1.X + 1;
        int sy = corner2.Y - corner1.Y + 1;
        int sz = corner2.Z - corner1.Z + 1;

        // Walk through all chunks. We start with z so we get the same order as when walking
        // the blocks (makes neighbor detection easier if we add/need that). We go over y
        // last because we don't need to iterate over all Y chunks.

        BlockPos offset = new BlockPos(-sx / 2 - corner1.X, -corner1.Y, -sz / 2 - corner1.Z, 1);
        dimension.AdjustPosForSubDimension(offset);
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
                        for (int y = cSurface; y >= corner1.Y; y--)
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
        dsc.AppendLine(String.Format(
            "Abs: ({0},{1},{2}) - ({3},{4},{5})",
            corner1.X - D, corner1.Y, corner1.Z - D,
            corner2.X - D, corner2.Y, corner2.Z - D
        ));
        dsc.AppendLine(String.Format(
            "Relative: ({0},{1},{2}) - ({3},{4},{5})",
            corner1.X - Pos.X, corner1.Y - Pos.Y, corner1.Z - Pos.Z,
            corner2.X - Pos.X, corner2.Y - Pos.Y, corner2.Z - Pos.Z
        ));
        dsc.AppendLine(String.Format(
            "Technical: ({0},{1},{2}) - ({3},{4},{5})",
            corner1.X, corner1.Y, corner1.Z,
            corner2.X, corner2.Y, corner2.Z
        ));
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
}

[ProtoContract]
internal class UpdateDimensionPacket
{
    [ProtoMember(1)]
    public MapMode mode;
    [ProtoMember(2)]
    public BlockPos corner1;
    [ProtoMember(3)]
    public BlockPos corner2;
    [ProtoMember(4)]
    public Lod lod;

    public UpdateDimensionPacket() { }
    public UpdateDimensionPacket(MapMode mode, BlockPos corner1, BlockPos corner2, Lod lod)
    {
        this.mode = mode;
        this.corner1 = corner1;
        this.corner2 = corner2;
        this.lod = lod;
    }
}

[ProtoContract]
internal class ConfigurePacket
{
    [ProtoMember(1)]
    public Vec3i offset; // In voxels
    [ProtoMember(2)]
    public Vec3f rotation; // In °
    [ProtoMember(3)]
    public int size; // In voxels

    public ConfigurePacket() { }
    public ConfigurePacket(Vec3i offset, Vec3f rotation, int size)
    {
        this.offset = offset;
        this.rotation = rotation;
        this.size = size;
    }
}
