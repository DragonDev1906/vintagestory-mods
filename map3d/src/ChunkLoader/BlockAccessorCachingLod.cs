using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.Server;

namespace Map3D;


// This does not implement the entire IBlockAccessor interface, but that shouldn't be needed.
//
// All chunk indicies (regardless of whether they are individual ints or a long) or after the
// Lod is applied and thus don't directly correlate to the original chunks index. They are
// divided by the Lod level.
class BlockAccessorLodCaching
{
    private GameFile db;
    private Dictionary<long, ServerChunk> lodChunks;
    private Lod lod;
    private ushort size;    // Number of chunks to combine (same as number of blocks perblock) per dimension.
    private ushort invsize; // Non-descriptive name, but this is just 32/size.
    private ushort shift;
    private long mask; // Applied before shifting

    // This will (probably) only work with powers of 2, as I want to avoid the complexity
    // we get with other levels. If other levels are required, dimension scaling should
    // be used.
    internal BlockAccessorLodCaching(GameFile db, Lod lod)
    {
        this.db = db;
        this.lod = lod;

        // Setup mask for quick index conversion.
        // chunkX: 21bit = 5 nibble + 1 bit             Start:  0:0
        // chunkZ: 21bit = 5 nibble + 1 bit             Start:  5:1
        // chunkY: 9bit = 2 nibble + 1 bit              Start: 10:2
        // guard: 1 bit                                 Start: 12:3
        // dimension: 10 bit = 2 nibble + 2 bit         Start: 13:0
        // reserved: 2 bit (includes sign)              Start: 15:2
        switch (lod)
        {
            //              0bRRDDDDDDDDDDGYYYYYYYYYZZZZZZZZZZZZZZZZZZZZZXXXXXXXXXXXXXXXXXXXXX
            case Lod.None:
                this.mask = 0b0111111111111111111111111111111111111111111111111111111111111111;
                this.shift = 0;
                this.size = 1;
                break;
            case Lod.Lod2:
                this.mask = 0b0111111111111111111110111111111111111111110111111111111111111110;
                this.shift = 1;
                this.size = 2;
                break;
            case Lod.Lod4:
                this.mask = 0b0111111111111111111100111111111111111111100111111111111111111100;
                this.shift = 2;
                this.size = 4;
                break;
            case Lod.Lod8:
                this.mask = 0b0111111111111111111000111111111111111111000111111111111111111000;
                this.shift = 3;
                this.size = 8;
                break;
            case Lod.Lod16:
                this.mask = 0b0111111111111111110000111111111111111110000111111111111111110000;
                this.shift = 4;
                this.size = 16;
                break;
            case Lod.ChunkAsOneBlock:
                this.mask = 0b0111111111111111100000111111111111111100000111111111111111100000;
                this.shift = 5;
                this.size = 32;
                break;
        }
        this.invsize = (ushort)(32 / size);
    }

    // This is mainly for convenience. We want the lod chunks to be neighbors as far as the integer
    // coordinates are concerned, even though they are not in the lodindex.
    public int AdjustChunkPos(int c) { return c >> shift; }
    public int AdjustSize(int s) { return (s + size - 1) >> shift; } // Round up

    // Call this if you're sure you no longer need data from this chunk.
    // If you do use it again after calling this the chunk will need to be loaded
    // from disk again, which can have a performance impact (depending on the Lod level).
    //
    // Index can be any normal chunkindex in this region, but all chunks that share this lodindex
    // will be discarded.
    // public void DiscardChunk(long index)Value
    // {
    //     if (lodChunks.Remove(index, out ServerChunk out))
    // }
    // public 

    // Avoid these unless you need the entire chunk, they can get expensive at higher Lod levels.
    public ServerChunk GetChunkOnce(int cx, int cy, int cz)
    {
        // Fast path
        if (lod == Lod.None)
        {
            return cleanupLikeIfWeCopied(db.loadChunk(cx, cy, cz));
        }

        // Convert lod coordinates to real coordinates.
        cx = cx << shift;
        cy = cy << shift;
        cz = cz << shift;

        ServerChunk lodchunk = ServerChunk.CreateNew(db.chunkPool);
        lodchunk.Unpack();
        lodchunk.Lighting.FloodWithSunlight(18);

        // To get a complete chunk we need all individual chunks in it.
        // Again: Let's be somewhat cache friendly in the way we iterate
        for (ushort y = 0; y < size; y++)
            for (ushort z = 0; z < size; z++)
                for (ushort x = 0; x < size; x++)
                {
                    ServerChunk chunk = db.loadChunk(cx + x, cy + y, cz + z);
                    if (chunk != null)
                    {
                        chunk.Unpack();
                        fillLod(x, y, z, lodchunk, chunk);
                        chunk.Dispose();
                    }
                }
        return lodchunk;
    }

    // The two chunks are identical and the same objects,
    // I'm returning it just for convenience.
    private ServerChunk cleanupLikeIfWeCopied(ServerChunk chunk)
    {
        // We are copying a normal (overworld) chunk here and are not initializing entities
        // and block entities properly when it is loaded. In addition, there may be some
        // issues if we just duplicate data and it wastes memory. To avoid these issues
        // it's probably a good idea to  clean up these chunks and only keep the relevant
        // data (we don't care about interactions so missing block entities shouldn't
        // matter much unless a block does initialization logic for them when it is
        // deserialized).
        //
        // We also can't use/call `RemoveEntitiesAndBlockEntities` because that calls the
        // problematic code.
        //
        // Care must be taken in regards to what `chunk.AfterDeserialization` does during
        // `FromBytes`. We could also harmony patch `ServerChunk.FromBytes` to not create
        // the entity list in the first place, but that feels error prone, too (might save
        // some memory allocs though):
        // - The constructor for entities is called (but start is not)
        // - FromBytes is called for those entities, which calls `FromTreeAttributes`
        //   (some few mods may not like this).
        // - The same goes for BlockEntities.
        chunk.Entities = Array.Empty<Entity>();
        chunk.EntitiesCount = 0;
        if (chunk.BlockEntities != null)
            chunk.BlockEntities.Clear();
        chunk.BlockEntitiesCount = 0;
        if (chunk.ModData != null)
            chunk.ModData.Clear();
        return chunk;
    }

    private int chooseBlock(Dictionary<int, int> counter, int airBlocks)
    {
        // Threshold for keeping a block as air
        // if (size >= 16 && airBlocks > size * size * (size - 1))
        //     return 0;

        int nonOpaqueCount = 0;
        int nonOpaque = 0;
        foreach (KeyValuePair<int, int> entry in counter.OrderBy(e => -e.Value))
        {
            // Ignore air blocks (shouldn't be needed if we skip them in the calller).
            if (entry.Key == 0)
                continue;

            // We may want to further restrict this if we encounter transparent blocks.
            Block block = this.db.worldAccessorForResolve.GetBlock(entry.Key);
            if (block.AllSidesOpaque)
            {
                // Prefer the nonOpaque block if the count difference is too high.
                // This is based on the exposed scale, which tends to favor
                // nonOpaque blocks.
                if (nonOpaque > 0 && nonOpaqueCount > entry.Value * 2)
                    return nonOpaque;
                else
                    return entry.Key;
            }
            else if (nonOpaque == 0)
            {
                nonOpaque = entry.Key;
                nonOpaqueCount = entry.Value;
            }
        }

        // We have not found a sufficiently relevant opaque block => choose nonOpaque or air.
        return nonOpaque;

        // var sorted = from entry in counter orderby entry.Value descending select entry;



        // short total = 1 << (3 * shift);

        // For now let's ignore air and use the block even if a single block is present.
        // First check if the block should be air.
        // If this is the case we may want to push down the second most common block.
        // if (counter.TryGetValue(0, out short air) && air > total / 2)
        //     return 0;

        // Find most common block (excluding air)
        // int mostCommonId = 0;
        // int highest = 0;
        // foreach (var item in counter)
        // {
        //     if (item.Value > highest && item.Key != 0)
        //     {
        //         highest = item.Value;
        //         mostCommonId = item.Key;
        //     }
        // }
        // return mostCommonId;
    }

    // Takes the src chunk, computes the Lod of it and puts the result into the
    // position of dst. x,y,z indicate which chunk it is, not by
    // specifying an offset but by specifying which (sub)chunk this is.
    private void fillLod(ushort x, ushort y, ushort z, ServerChunk dst, ServerChunk src)
    {
        // PERFORMANCE: We may not want to take out a dict if we only have 8 blocks
        // we need to combine, but doing the same thing for both is easiest.
        // Dictionary<int, int>.
        // TODO: Maybe use the chunk's palette to know how many different blocks it can have.
        Dictionary<int, int> counter = new(8); // Capacity of 8 should be fine for most cases.

        // The bits in dstIndex that are the same for all blocks in src.
        ushort dstFix = (ushort)((x | (z << 5) | (y << 10)) << (5 - shift));

        // Not sure if this is more efficient than nested loops, but it should result
        // in fewer branch misses.
        int icount = 1 << (3 * shift);
        int ocount = 1 << (3 * (5 - shift));
        int imask = (1 << shift) - 1;
        int omask = (1 << (5 - shift)) - 1;
        for (int o = 0; o < ocount; o++)
        {
            int dstVar = (o & omask)                         // X
                | ((o << (shift)) & (omask << 5))        // Z
                | ((o << (2 * shift)) & (omask << 10)); // Y

            int airBlocks = 0;

            // Count how often each block occurs.
            for (int i = 0; i < icount; i++)
            {
                int srcIdx = (i & imask)                    // x
                    | ((i << (5 - shift)) & (imask << 5))         // z
                    | ((i << (10 - 2 * shift)) & (imask << 10))    // y
                    | (dstVar << shift);

                // Just counting the number of blocks produces an unsatisfying result:
                // Grass blocks are only one layer and thus are usually the minority,
                // resulting in mostly stone blocks which you usually wouldn't see.
                //
                // The most realistic approach would be to count the number of exposed
                // surfaces but that might be too expensive (at least with a naiive
                // implementation) instead we'll just see if the block above it is air.
                // Counting the top of the chunk as exposed may create some visual
                // artefacts in hilly areas though.
                // Not sure what the best way to handle the chunk surface would be.
                // For accurate results we should probably load that chunk and look at
                // its bottom blocks, but I think this will be enough, even though there
                // is a bias at chunk borders.
                // Initially I just ignored blocks that are not exposed, but that resulted in an Empty
                // dictionary, which produced air blocks.
                int id = src.Data.GetBlockIdUnsafe(srcIdx);
                // Don't bother counting air blocks.
                if (id > 0)
                {
                    // Probably terrible for performance.
                    bool exposed = false;
                    if (srcIdx < 32 * 32 * 31)
                        exposed = !this.db.worldAccessorForResolve.GetBlock(
                            src.Data.GetBlockIdUnsafe(srcIdx + 32 * 32)
                        ).AllSidesOpaque;

                    int weight = exposed ? size + 1 : 1;
                    if (counter.ContainsKey(id))
                        counter[id] += weight;
                    else
                        counter.Add(id, weight);
                }
                else
                {
                    airBlocks++;
                }
            }

            int dstIdx = dstFix | dstVar;
            dst.Data[dstIdx] = chooseBlock(counter, airBlocks);
            counter.Clear();
        }

        // A note on the index manipulation below:
        // For cache efficiency we want to itearte over x in the inner-most loop. Therefore
        // we want to get x from the lowest shift bits. Then shift bits for Z, then for Y.
        // This results int the following bit layout with lower case letters being the inner loop.
        // size,shift   iteration index     srcIndex            dstIndex
        //    2,1       0YYYYZZZZXXXXyzx    0YYYYyZZZZzXXXXx    0_YYYY_ZZZZ_XXXX
        //    4,2       0YYYZZZXXXyyzzxx    0YYYyyZZZzzXXXxx    0__YYY__ZZZ__XXX
        //    8,3       0YYZZXXyyyzzzxxx    0YYyyyZZzzzXXxxx    0___YY___ZZ___XX
        //   16,4       0YZXyyyyzzzzxxxx    0YyyyyZzzzzXxxxx    0____Y____Z____X
        //   32,5       0yyyyyzzzzzxxxxx    0yyyyyzzzzzxxxxx    0YYYYYZZZZZXXXXX
        //
        // To get dstIndex we can shift and mask+combine with xyz
        // srcIndex is a bit trickier
        //
        // We could probably use SIMD here, should speed it up by a few cycles.
        // for (int i = 0; i < 

        // Maybe that's optimized too early, but I can't think of a faster implementation.
        // We combine 8 positions into a single SIMD register.
        // Vector128<short> increments = Vector128.Create(0, 1, 2, 3, 4, 5, 6, 7);
        // short xmask = size-1;
        // short zmask = (size-1) << 5;
        // short ymask = (size-1) << 10;
        // for (short i = 0; i < 4096; i++)
        // {
        //     // Vector128<short> srcIndicies = Vector128.Create((short)(i * 8));
        //     // srcIndicies += increments;
        //     // srcIndicies = srcIndicies & ((size-1) << 10)
        //     //     | (srcIndicies >> shift) & ((size-1) << 5)
        //     //     | (srcIndicies >> (2*shift) & ((size-1));
        //
        //
        //     // Vector128.
        // }

        // I'd prefer to work on the data directly without mapping every block to
        // the pallete first, but this is the API we have and it's not that
        // performance critical since we're running in a separate thread.
        // int dstBlockCount = size * size * size;
        // for (int d = 0; d < 32768; d++)
        // {
        //
        //
        //     // // This runs as often as there are destination blocks, but not in a useful
        //     // // order => index manipulation.
        //     //
        //     // for (int s = 0; s < 32768 / dstBlockCount; s++)
        //     // {
        //     //     // This runs as often as there are blocks per block (also not in
        //     //     // a useful order.
        //     // }
        //     //
        //     // counter.Clear();
        // }
        //
        // for (int index3d = 0; index3d < 32 * 32 * 32; index3d++)
        // {
        //     // We need to 
        // }
        // src.Data.GetBlockIdUnsafe()

        // for (int dy = 0; dy < invsize; dy++)
        //     for (int dz = 0; dz < invsize; dz++)
        //         for (int dx = 0; dx < invsize; dx++)
        // // src.Data.GetBlockIdUnsafe()
    }

    // public ServerChunk GetChunk(int cx, int cy, int cz) { return GetChunk(LodIndex(cx, cy, cz)); }
    // public ServerChunk GetChunkOnce(int cx, int cy, int cz) { return GetChunkOnce(LodIndex(cx, cy, cz)); }
    //
    // private ServerChunk GetChunk(long logindex)
    // {
    //     if (lodChunks.TryGetValue(logindex, out var c))
    //         return c;
    //
    //     ServerChunk chunk = GetChunkOnce(logindex);
    //     lodChunks.Add(logindex, chunk);
    //     return chunk;
    // }
    // private ServerChunk GetChunkOnce(long logindex)
    // {
    //     // PERFORMANCE: Maybe use a range query for high Lod levels.
    // }

    // Behaves like the normal index3d, but at higher Lods cx isn't a neighbor of cx+1,
    // and the same for the other coordinates.
    private long LodIndex(int cx, int cy, int cz)
    {
        int dim = cy >> 10;
        // The mask isn't strictly necessary, but it does add a bit of safety.
        cx = (cx << shift) & 0x1fffff;
        cy = (cy << shift) & 0x1ff;
        cz = (cz << shift) & 0x1fffff;

        return (long)cx
            | (long)cz << 21
            | (long)cy << 42
            | (long)dim << 52;

    }
    // Behaves just like the normal index3d
    private long ChunkIndex(int cx, int cy, int cz)
    {
        return (long)cx
            | (long)cz << 21
            | (long)cy << 42;
    }
    private long ChunkToLodIndex(long index)
    {
        return index & this.mask;
    }
}
