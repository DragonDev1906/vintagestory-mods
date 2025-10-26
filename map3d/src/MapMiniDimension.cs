using Vintagestory.Common;
using Vintagestory.Server;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace Map3D;

public class MapMiniDimension : BlockAccessorMovable, IMiniDimension
{
    // TODO: Remove api
    ICoreAPI api;
    Map3DModSystem system;
    public float scale { get; set; }

    // See https://github.com/bluelightning32/vs-dimensions-demo/blob/main/src/AnchoredDimension.cs
    public MapMiniDimension(BlockAccessorBase parent, Vec3d pos, ICoreAPI api, float scale = 1f)
        : base(parent, pos)
    {
        this.scale = scale;
        this.api = api;
        this.system = api.ModLoader.GetModSystem<Map3DModSystem>();
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

        system.AddChunkToLoadedList(cindex, chunk);
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

    // REALLY IMPORTANT OVERRIDE.
    // This is called on the client side when receiving a chunk and on the server side
    // when a chunk is loaded. It iterates over ALL blocks (including air) in the mini dimension.
    // Normally it computes the center of mass, which is used for the RenderTransformMatrix,
    // but we don't use that value, so this override prevents a lot of wasted computation.
    public override void RecalculateCenterOfMass(IWorldAccessor world) { }

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
            IWorldChunk ret = null;
            for (int y = 0; y < chunkMapSizeY; y++)
            {
                IWorldChunk chunk = base.CreateChunkAt(posX, 32 * y, posZ);
                if (y == idx)
                    ret = chunk;
            }
            if (ret == null)
                system.Mod.Logger.Error("Could not find chunk at y={0} (idx={1}) after generating {2} chunks", posY, idx, chunkMapSizeY);
            return ret;
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
            (chunk.Data as ChunkData).SetFluid(index3d, blockId);
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
