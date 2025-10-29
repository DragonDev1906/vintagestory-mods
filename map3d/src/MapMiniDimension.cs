using Vintagestory.Common;
using Vintagestory.Server;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using System.Collections.Generic;
using System.Linq;

namespace Map3D;

public class MapMiniDimension : BlockAccessorMovable, IMiniDimension
{
    // TODO: Remove api
    ICoreAPI api;
    Map3DModSystem system;
    public float scale { get; set; }

    // Wish I wouldn't have to do this (I avoided it long enough), but we do need a way to list all
    // chunks in the dimension. This is a reference to the underlying BlockAccessorMovable, obtained
    // via reflection. It is only null if we failed to get it via reflection, in which case not all
    // functionality will be available (mainly the ability to send all loaded chunks to a player).
    // This is probably preferrable to us crashing.
    private Dictionary<long, IWorldChunk>? _chunks;

    // See https://github.com/bluelightning32/vs-dimensions-demo/blob/main/src/AnchoredDimension.cs
    public MapMiniDimension(BlockAccessorBase parent, Vec3d pos, ICoreAPI api, float scale = 1f)
        : base(parent, pos)
    {
        this.scale = scale;
        this.api = api;
        this.system = api.ModLoader.GetModSystem<Map3DModSystem>();
        this._chunks = Map3DModSystem.readInternalField<BlockAccessorMovable, Dictionary<long, IWorldChunk>>(
            system.Mod.Logger, (BlockAccessorMovable)this, "chunks"
        );
    }

    public override void AdjustPosForSubDimension(BlockPos pos)
    {
        pos.X += subDimensionId % 4096 * 16384 + 8192;
        pos.Z += subDimensionId / 4096 * 16384 + 8192;
    }

    // This probably exists to avoid problems with accuracy, since the matrix is only using floats.
    public override FastVec3d GetRenderOffset(float dt)
    {
        // Copied from the base class. Changes:
        // - Use CurrentPos instead of SelectioNTrackingOriginalPos, as it makes way more sense for us.
        // - Place center at the bottom instead of the middle, otherwise we can't load chunks from disk.
        //   Use AdjustPosForSubDimension, it accounts for having this not at the center of the mini dimension.
        FastVec3d fastVec3d = new FastVec3d(
            -(subDimensionId % 4096) * 16384 - 8192,
            0,
            -(subDimensionId / 4096) * 16384 - 8192
        );
        return fastVec3d.Add(CurrentPos.X, CurrentPos.InternalY, CurrentPos.Z);
    }
    public override float[] GetRenderTransformMatrix(float[] currentModelViewMatrix, Vec3d playerPos)
    {
        // Skip the matrix math in the following cases:
        // - It would do nothing because we don't have rotation or scale
        // - I'm not 100% sure about initialization, so we also don't apply scale if it is zero (which isn't useful anyways).
        if (
            CurrentPos.Yaw == 0f && CurrentPos.Pitch == 0f && CurrentPos.Roll == 0f &&
            (scale == 0f || scale == 1f)
        )
        {
            return currentModelViewMatrix;
        }

        // currentModelViewMatrix doesn't contain the translation to move it to the given block/origin,
        // nor is it added afterwards automatically. The BlockAccessorMovable implementation initially
        // confused me a bit because it translates to the block, rotates  and then back, thus not
        // applying any translations. Apparently, the chunk position is passed to the shader in a separate
        // uniform ("originUniformName") and applied in the shader, instead of being part of the
        // modelViewMatrix.
        //
        // This means we don't want to apply a translation to move the chunks to us.
        // 
        // We don't want to apply scaling or rotation around that origin point, so we have to apply
        // the translation and undo it afterwards, like the BlockAccessorMovable does.
        //
        // Unfortunately, this also makes this a bit brittle should BlockAccessorMovable or the way
        // this matrix works ever changes, which will likely result in the blocks not getting rendered.
        //
        // Note: This differs slightly from BlockAccessorMovable: We don't use CenterOfMass because we
        // don't need it. Also that causes null reference exceptions for some reason.
        float x = (float)(CurrentPos.X - playerPos.X);
        float y = (float)(CurrentPos.Y - playerPos.Y);
        float z = (float)(CurrentPos.Z - playerPos.Z);

        float[] array = new float[currentModelViewMatrix.Length];
        // Translate to position around which to scale and rotate.
        Mat4f.Translate(array, currentModelViewMatrix, x, y, z);
        // Apply scale (can also be applied after rotation)
        Mat4f.Scale(array, array, scale, scale, scale);
        // Apply the rotation like the base class does (even though we don't use rotations, yet).
        ApplyCurrentRotation(array);
        // Undo the translation so things render where they should.
        return Mat4f.Translate(array, array, 0f - x, 0f - y, 0f - z);
    }


    // TODO: Don't load all chunks in a single tick, that results in a giant lag spike.
    public void LoadChunksServer(Vec3i size)
    {
        // TODO: Make sure the following gets updated to the new chunk aligned copying.

        // This is what our sub dimension coordinates are based on.
        int sx = size.X;
        int sz = size.Z;

        // Start with the center of the subdimension, see AdjustPosForSubDimension which
        // does this calculation with block coordinates instead of chunk coordinates.
        int cxmid = (subDimensionId % 4096) * 512 + 256;
        int czmid = (subDimensionId / 4096) * 512 + 256;

        // Go to the lower edge (and do the correct rounding).
        // We need to divide by 2 because we're centered, then we need to divide by 32
        // to get chunk coordinates. But we need to round up, otherwise we'll be missing data.
        int cxstart = cxmid - ((sx + 62) >> 6);
        int czstart = czmid - ((sz + 62) >> 6);

        // From now on we need the size in chunks.
        sx = (sx + 31) / 32;
        sz = (sz + 31) / 32;

        system.Mod.Logger.Notification(
            "Loading chunks: dim={0}, cx={1}, cz={2}, sx={3}, sz={4}",
            subDimensionId, cxstart, czstart, sx, sz
        );

        ChunkRequest req = ChunkRequest.SimpleLoad(
            this.OnChunkLoaded,
            cxstart, 1024, czstart,
            sx, 8, sz,
            Lod.None
        );
        system.LoadChunksV2(req);



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

    // Called for each chunk loaded by our own chunk loading+generation system.
    // The chunk is not known to the game (engine), yet. As of now it's just some data.
    internal void OnChunkLoaded(int cx, int cy, int cz, ServerChunk chunk)
    {
        // TODO: We may have to check if the server is still running and this dimension still exists.

        // The game/engine is not aware of this chunk. Unfortunately making it aware is insanely
        // difficult because the required fields are internal and locked behind a FastRWLock that
        // is implemented via a struct, which makes getting it via Reflection difficult.
        // It might be possible with Unsafe and/or Harmony transpiling (+ maybe reverse patches),
        // but I couldn't figure that out yet.
        //
        // This means we have to create a new chunk and copy over the data and hope it's performant.
        // Would be interesting to know if this is faster or slower than processing an entire chunk
        // column.

        // Expects block positions relative to the mini dimension corner.
        // IWorldChunk dst = base.CreateChunkAt((cx & 0x1ff) << 5, (cy & 0x1ff) << 5, (cz & 0x1ff) << 5);
        // dst.Data.SetBlockAir

        long cindex = (
            cx +
            ((long)(cz) << 21) +
            ((long)(cy) << 42) // Includes the dimension
        );

        system.AddChunkToLoadedListServer(cindex, chunk);
        AddLoadedChunk(new Vec3i(cx, cy, cz), chunk);
    }

    // Chunk coord contains the dimension in its Y coordinate.
    internal void AddLoadedChunk(Vec3i chunkCoord, IWorldChunk chunk)
    {
        long cindex = (
            chunkCoord.X +
            ((long)(chunkCoord.Z) << 21) +
            ((long)(chunkCoord.Y) << 42) // Includes the dimension
        );
        // This (probably) isn't the right function to call, but it's the only one that allows us to
        // add something to base.chunks, so we have to use it.
        base.ReceiveClientChunk(cindex, chunk, api.World);
        // Annoyingly this function wants block coordinates, though not in the centered coordinate system.
        // and there is no other way to mark a chunk dirty. Luckily these are still easy to get.
        // Here we are not in the chunk but need the entire X coordinate (in the subdim)
        base.MarkChunkDirty(
            ((chunkCoord.X & 0x1ff) << 5),
            ((chunkCoord.Y & 0x1ff) << 5),
            ((chunkCoord.Z & 0x1ff) << 5)
        );
    }

    enum PlayerState : byte
    {
        None,
        AllChunks,
        DirtyChunks,
    }

    // List of players that are ready/want to receive chunks from this dimension.
    // This stores the ClientId. 
    // TODO: We probably need/want to remove players when they leave.
    private Dictionary<int, PlayerState> readyPlayers = new();

    public void SetPlayerReady(IPlayer player, bool ready)
    {
        system.Mod.Logger.Notification(
            "Player '{0}' ready for chunks {1} at {2}",
            player.PlayerName, subDimensionId, CurrentPos.XYZ
        );
        readyPlayers[player.ClientId] = PlayerState.AllChunks;
    }

    // REALLY IMPORTANT OVERRIDE.
    // This is called on the client side when receiving a chunk and on the server side
    // when a chunk is loaded. It iterates over ALL blocks (including air) in the mini dimension.
    // Normally it computes the center of mass, which is used for the RenderTransformMatrix,
    // but we don't use that value, so this override prevents a lot of wasted computation.
    public override void RecalculateCenterOfMass(IWorldAccessor world) { }

    // Another REALLY IMPORTANT OVERRIDE.
    // We have no control over when the dimension is created on the client-side, it usually happens
    // after it was created on the server-side and thus after the server is sending chunks to the
    // client. Normally this isn't a big problem, as the client-side will create a default
    // IMiniDimension and puts the chunks there. Unfortunately, the default IMiniDimension is
    // terrible at handling large amounts of chunks (see previous override), resulting in
    // exponentially longer frame times and the client-side completely freezing.
    // We have two options to avoid this:
    // - Change the behavior of the default IMiniDimension via Harmony patching (would break other
    //   mods that expect/require the center of mass)
    // - Make absolutely certain we're only sending chunks to players that are ready to handle them.
    //
    // I've chosen the second option, hence this override. We could do this in
    // `MarkChunkForSendingToPlayersInRange`, too, but this one is called less often.
    public override void CollectChunksForSending(IPlayer[] players)
    {
        // TODO: We likely need special handling for new players, as they wouldn't receive already-loaded chunks.
        var ready = new List<IPlayer>(players.Length);
        foreach (var p in players)
        {
            if (!readyPlayers.TryGetValue(p.ClientId, out PlayerState state))
                continue;

            // Nice that C# has a switch expression that is exhaustive (would love to use it),
            // but for some reason they decided that those always need a return type (and void is invalid).
            switch (state)
            {
                case PlayerState.None:
                    break;
                case PlayerState.AllChunks:
                    SendAllChunks(p);
                    readyPlayers[p.ClientId] = PlayerState.DirtyChunks;
                    break;
                case PlayerState.DirtyChunks:
                    ready.Add(p);
                    break;
            }
        }

        base.CollectChunksForSending(ready.ToArray());
    }

    private void SendAllChunks(IPlayer player)
    {
        if (_chunks == null)
        {
            system.Mod.Logger.Error("Cannot access chunk list to send all chunks to player {0}", player.PlayerName);
            return;
        }

        foreach (var e in _chunks)
            MarkChunkForSendingToPlayersInRange(e.Value, e.Key, player);
    }

    // Cordinates are relative to the corner of the mini dimension
    public IWorldChunk GetOrCreateChunk(int cx, int cy, int cz)
    {
        // Shouldn't use GetChunk, as that may be overriden in the future
        IWorldChunk chunk = base.GetChunkAt(cx * 32, cy * 32, cz * 32);
        if (chunk != null)
            return chunk;

        return CreateChunkAt(cx * 32, cy * 32, cz * 32);
    }
    public override IWorldChunk GetChunk(int cx, int cy, int cz)
    {
        return base.GetChunkAt(cx * 32, cy * 32, cz * 32);
    }

    protected override IWorldChunk CreateChunkAt(int posX, int posY, int posZ)
    {
        // We want to be able to load the chunks from disk.
        // This is currently only possible for the lowest 8 chunks and only if those all exist.
        // Therefore we want to create the entire chunk column when creating a new chunk.
        //
        // TODO: Read actual world height
        int mapSizeY = api.World.BlockAccessor.MapSizeY;
        int chunkMapSizeY = mapSizeY / 32;

        if (posY < mapSizeY)
        {
            int idx = posY / 32;
            IWorldChunk? ret = null;
            for (int y = 0; y < chunkMapSizeY; y++)
            {
                IWorldChunk chunk = base.CreateChunkAt(posX, 32 * y, posZ);
                if (y == idx)
                    ret = chunk;
            }

            if (ret == null)
                system.Mod.Logger.Error("Could not find chunk at y={0} (idx={1}) after generating {2} chunks", posY, idx, chunkMapSizeY);

            // The code above cannot introduce a null, so this could only be null if the base class returns null,
            // in which case it should be fine for us to do the same. In case this does ever happen: We have logged
            // this as an error above, so it should be findable. Not much use in explicitly throwing an exception here I think.
            return ret!;
        }
        else
            return base.CreateChunkAt(posX, posY, posZ);
    }

    // For some reason the BlockAccessorMovable just does nothing when this is called. Probably not implemented yet.
    public override void ExchangeBlock(int blockId, BlockPos pos)
    {
        // Copied from SetBlock
        pos.SetDimension(1);
        IWorldChunk chunk = GetChunkAt(pos.X, pos.Y, pos.Z);
        if (chunk == null)
        {
            if (blockId == 0)
                return;
            chunk = CreateChunkAt(pos.X, pos.Y, pos.Z);
        }
        else
        {
            chunk.Unpack();
            if (chunk.Empty)
                chunk.Lighting.FloodWithSunlight(18);
        }

        Block block = api.World.Blocks[blockId];

        chunk.Unpack();
        // Coordinates within the chunk (5 bit)
        int x = pos.X & 0x1f;
        int y = pos.Y & 0x1f;
        int z = pos.Z & 0x1f;
        int index3d = x + (z << 5) + (y << 10);
        // int index3d = api.World.ChunkSizedIndex3D(pos.X & 0x1F, pos.Y & 0x1F, pos.Z & 0x1F);
        // int oldblockid;
        if (block.ForFluidsLayer)
        {
            // oldblockid = (chunk.Data as ChunkData).GetFluid(index3d);
            if (chunk.Data is ChunkData data)
                data.SetFluid(index3d, blockId);
            else
                system.Mod.Logger.Error("Chunk data is of unexpected type, not setting block in fluid layer");
        }
        else
        {
            // oldblockid = (chunk.Data as ChunkData).GetSolidBlock(index3d);
            chunk.Data[index3d] = blockId;
        }
        // chunk.MarkModified();
        MarkChunkDirty(pos.X, pos.Y, pos.Z);
        // worldmap.MarkChunkDirty(pos.X / 32, pos.InternalY / 32, pos.Z / 32, priority: true);
        // if (relight)
        // {
        //     worldmap.UpdateLighting(oldblockid, blockId, pos);
        // }
    }
}
