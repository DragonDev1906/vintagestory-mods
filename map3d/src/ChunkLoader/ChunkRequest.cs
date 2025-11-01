using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.Common.Database;
using Vintagestory.Server;

namespace Map3D;

public enum RequestType
{
    Load,
    Copy,
    LoadOrCopy,
}

public delegate void ChunkLoaded(int cx, int cy, int cz, ServerChunk chunk);

public interface IChunkRequest
{
    void process_all(IChunkLoader loader);
}
public interface IChunkReceiver
{
    void LoadChunk(ulong cindex, ServerChunk chunk);
}

public abstract class QubeRequest : IChunkRequest
{
    public IChunkReceiver receiver;
    public BlockPos pos;
    public Vec3i size; // In blocks

    public QubeRequest(IChunkReceiver receiver, BlockPos pos, Vec3i size)
    {
        this.receiver = receiver;
        this.pos = pos;
        this.size = size;
    }

    // There is probably a better place to put this.
    internal static ulong ToChunkIndex(BlockPos pos)
    {
        return ToChunkIndex(pos.X / 32, pos.InternalY / 32, pos.Z / 32);
        // return (
        //     (ulong)(pos.X / 32) +
        //     ((ulong)(pos.Z / 32) << 21) +
        //     ((ulong)(pos.InternalY / 32) << 42)  // Includes the dimension
        // );
    }
    internal static ulong ToChunkIndex(int cx, int cyinternal, int cz)
    {
        return (
            (ulong)cx +
            ((ulong)cz << 21) +
            ((ulong)cyinternal << 42)  // Includes the dimension
        );
    }

    public virtual void process_all(IChunkLoader l)
    {
        int csx = (pos.X + size.X + 31) / 32 - pos.X / 32;
        int csy = (pos.Y + size.Y + 31) / 32 - pos.Y / 32;
        int csz = (pos.Z + size.Z + 31) / 32 - pos.Z / 32;

        ulong cpos = ToChunkIndex(pos);
        bool t = trim();
        // ulong cpos = (
        //     (ulong)(pos.X / 32) +
        //     ((ulong)(pos.Z / 32) << 21) +
        //     ((ulong)(pos.InternalY / 32) << 42)  // Includes the dimension
        // );

        l.logger.Notification("pos={0}", pos);
        l.logger.Notification("cpos={0}, cs=({1}, {2}, {3})", cpos, csx, csy, csz);

        int count = 0;
        for (int x = 0; x < csx; x++)
        {
            for (int z = 0; z < csz; z++)
            {
                for (int y = 0; y < csy; y++)
                {
                    ulong cindex = cpos + QubeRequest.ToChunkIndex(x, y, z);
                    ServerChunk? chunk = get_chunk(l, cindex);
                    if (chunk != null && !chunk.Empty)
                    {
                        if (t && x == 0) clearBox(chunk, 0, pos.X % 32, 32, 32);
                        if (t && z == 0) clearBox(chunk, 0, 32, 32, pos.Z % 32);
                        if (t && x == csx - 1) clearBox(chunk, (pos.X + size.X) % 32, 31 - (pos.X + size.X - 1) % 32, 32, 32);
                        if (t && z == csz - 1) clearBox(chunk, 32 * ((pos.Z + size.Z) % 32), 32, 32, 31 - (pos.Z + size.Z - 1) % 32);

                        count++;
                        chunk.Pack();
                        l.Send(receiver, cindex, chunk);
                    }
                }
            }
        }
        l.logger.Notification("Request '{0}' finished. chunks={1}/{2}", this.GetType().Name, count, csx * csy * csz);
    }

    // There are probably more efficient ways, but this is easy.
    void clearBox(ServerChunk chunk, int index3d, int sx, int sy, int sz)
    {
        if (sx == 0 || sy == 0 || sz == 0)
            return;

        chunk.Unpack();
        for (int y = 0; y < sy; y++)
            for (int z = 0; z < sz; z++)
                for (int x = 0; x < sx; x++)
                    chunk.Data.SetBlockAir(index3d + x + 32 * z + 32 * 32 * y);
    }

    protected virtual bool trim() { return false; }

    protected abstract ServerChunk? get_chunk(IChunkLoader loader, ulong cindex);
}

public class LoadRequest : QubeRequest
{
    public LoadRequest(IChunkReceiver receiver, BlockPos pos, Vec3i size) : base(receiver, pos, size) { }

    protected override ServerChunk? get_chunk(IChunkLoader loader, ulong cindex)
    {
        return loader.loadFromDB(cindex);
    }
}

// Chunk boundaries don't change during copy. This brings better performance at the cost
// of being off by up to 32 blocks.
public class CopyRequest : QubeRequest
{
    public ulong offset;
    public Lod lod;

    BlockAccessorLod? ba;

    // Position is the corner, not the center.
    public CopyRequest(IChunkReceiver receiver, BlockPos src, BlockPos dst, Vec3i size) : base(receiver, dst, size)
    {
        offset = QubeRequest.ToChunkIndex(src) - QubeRequest.ToChunkIndex(dst);
    }

    public override void process_all(IChunkLoader l)
    {
        ba = new(l.db, lod);
        base.process_all(l);
    }

    protected override ServerChunk? get_chunk(IChunkLoader loader, ulong cindex)
    {
        // Only called by process_all, which sets ba.
        // ServerChunk? chunk = ba!.GetChunk(cindex + offset);
        ServerChunk? chunk = loader.loadFromDB(cindex + offset);
        if (chunk == null)
            return null;

        // REALLY IMPORTANT LINE!
        // Without this entities and blockentities would be compied.
        //
        // We cannot just copy entities and block entities, or moddata, as we don't know
        // what exactly they store and if that is problematic when the chunks are laoded.
        // Best example: Our Map3d block entity stores the dimensionID and would
        // (without further precausions) try to laod a dimension a second time. Since we
        // don't care about entities we should be abel to savely delete those from the chunk.
        //
        // Another example (probably more critical) is that every block entity stores its
        // position as an absolute value (x,y,z in tree attributes). If we copy the BE to
        // a new chunk with a different chunkindex/coordinate, the BE's position no longer
        // matches its actual position, which might cause all kinds of hard to debug mayham.
        //
        // This does likely need to happen before blocks are deleted from the chunk, as that
        // might cause code to execute.
        BlockAccessorLod.cleanupLikeIfWeCopied(chunk);
        // chunk.Blocks

        // Make sure this chunk gets saved, as we didn't do that.
        chunk.DirtyForSaving = true;
        return chunk;
    }

    protected override bool trim() { return true; }
}

