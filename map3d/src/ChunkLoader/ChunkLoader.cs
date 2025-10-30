using System;
using System.Collections.Concurrent;
using System.IO;
using Vintagestory.API.Common;
using Vintagestory.API.Server;
using Vintagestory.Common;
using Vintagestory.Server;

namespace Map3D;

class NoSavefileException : Exception { }

public interface IChunkLoader
{
    ILogger logger { get; }
    GameFile db { get; }

    public ServerChunk? loadFromDB(ulong cindex);
    public void Send(IChunkReceiver receiver, ulong cindex, ServerChunk chunk);
}

class ChunkLoader : IAsyncServerSystem, IChunkLoader
{
    IEventAPI api;
    ConcurrentQueue<IChunkRequest> tasks;
    public ILogger logger { get; private set; }
    ChunkDataPool chunkPool;
    IWorldAccessor worldAccessorForResolve; // Don't read blocks from this, we're in another thread.
    public GameFile db { get; private set; }

    // Lot's of things we need. Unfortunately we can't use the existing ChunkDataPool
    // because we don't have access to it.
    public ChunkLoader(
        IEventAPI api,
        Map3DModSystem modSystem,
        ConcurrentQueue<IChunkRequest> tasks,
        ILogger logger,
        ChunkDataPool chunkPool,
        IWorldAccessor worldAccessorForResolve,
        string databaseFileName
    )
    {
        this.api = api;
        this.tasks = tasks;
        this.logger = logger;
        this.chunkPool = chunkPool;
        this.worldAccessorForResolve = worldAccessorForResolve;

        // NOTE: The GameDatabase is annoying to use because connecting to it times out after 1ms.
        // It's also restricting, so we're setting up our own connection and sending sql directly.
        if (!File.Exists(databaseFileName))
        {
            logger.Error("Save-file database does not exist");
            throw new NoSavefileException();
        }

        db = new GameFile(logger, chunkPool, worldAccessorForResolve, databaseFileName);
        // We're not writing => We don't need the pragma (might even already be set).
    }

    public int OffThreadInterval()
    {
        if (tasks.IsEmpty)
            return 5;
        else
            return -1;
    }

    // Initially I thought about implementing the entire thread main loop myself,
    // this would give maximum control but is more likely to break at some point
    // (granted, it's more likely to break due to the chunk loading functionality
    // itself). Another benefit of using IAsyncServerSystem is that it gives us
    // profiling. The biggest downsides: The game might not like it if we take
    // too long and we don't have access to the cancellation token.
    public void OnSeparateThreadTick()
    {
        // For simplicity let's only process one request per tick, which also
        // allows us to be shut down after every request. May be changed in
        // the future, but the overhead of doing this should be minimal.
        if (!tasks.TryDequeue(out IChunkRequest? req)) return;

        req.process_all(this);
    }

    public void ThreadDispose()
    {
        this.db.Dispose();
    }

    public ServerChunk? loadFromDB(ulong cindex)
    {
        // DB file uses a different indexing scheme.
        // I'm not sure if this is the most efficient way, but we need the cindex regardless.

        /*
            cindex:
            reserved 	dimension 	guard 	chunkY 	chunkZ 	chunkX
            2 bits 	    10 bits 	1 bit 	9 bits 	21 bits 21 bits

            cpos:
            reserved 	chunkY 	dimension high part 	guard 	chunkZ 	dimension low part 	guard 	chunkX
            1 bit 	    9 bits 	5 bit 	                1 bit 	21 bits 5 bits 	            1 bit 	21 bits

RRDDDDDDDDDD_YYYYYYYYYZZZZZZZZZZZZZZZZZZZZZXXXXXXXXXXXXXXXXXXXXX
_YYYYYYYYYDDDDD_ZZZZZZZZZZZZZZZZZZZZZDDDDD_XXXXXXXXXXXXXXXXXXXXX
         */

        ulong cpos = (cindex & 0x1fffff) // x
            | ((cindex << 6) & ((ulong)0x1fffff << 27)) // z
            | ((cindex << 12) & ((ulong)0x1ff << 54)) // y
            | ((cindex >> 8) & ((ulong)0x1f << 49)) // dim upper
            | ((cindex >> 30) & ((ulong)0x1f << 22)); // dim lower

        if (((cindex >> 42) & 0xff) == 0)
            logger.Notification("cpos: {0}", cpos);

        return db.loadChunk(cpos);
    }

    public void Send(IChunkReceiver receiver, ulong cindex, ServerChunk chunk)
    {
        api.EnqueueMainThreadTask(delegate { receiver.LoadChunk(cindex, chunk); }, "map3d-loadchunk");
    }
}
