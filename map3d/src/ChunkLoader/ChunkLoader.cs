using System.Collections.Concurrent;
using System.IO;
using Vintagestory.API.Common;
using Vintagestory.API.Server;
using Vintagestory.Common;
using Vintagestory.Server;

namespace Map3D;

class ChunkLoader : IAsyncServerSystem
{
    IEventAPI api;
    ConcurrentQueue<ChunkRequest> tasks;
    ILogger logger;
    ChunkDataPool chunkPool;
    IWorldAccessor worldAccessorForResolve; // Don't read blocks from this, we're in another thread.
    GameFile db;

    // Lot's of things we need. Unfortunately we can't use the existing ChunkDataPool
    // because we don't have access to it.
    public ChunkLoader(
        IEventAPI api,
        Map3DModSystem modSystem,
        ConcurrentQueue<ChunkRequest> tasks,
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
            return;
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
        if (!tasks.TryDequeue(out ChunkRequest req)) return;

        // TODO: Process request
        switch (req.type)
        {
            case RequestType.Load: // Only load existing chunks
                load(req);
                break;
            case RequestType.Copy: // Copy from overworld, even if chunk exists
                copy(req);
                break;
            case RequestType.LoadOrCopy: // Load if it exists, otherwise copy
                break;
        }
    }

    public void ThreadDispose()
    {
        this.db.Dispose();
    }

    #region RequestType processing

    private void load(ChunkRequest req)
    {
        int count = 0;
        foreach ((int cx, int cy, int cz) in req.IterDestination())
        {
            ServerChunk chunk = db.loadChunk(cx, cy, cz);
            if (chunk != null && !chunk.Empty)
            {
                count++;
                send(req, cx, cy, cz, chunk);
            }
        }
        int total = req.TotalChunkCount();
        logger.Notification("RequestType.Load finished. chunks={0}/{1}", count, total);
    }

    private void copy(ChunkRequest req)
    {
        BlockAccessorLodCaching ba = new(db, req.lod);

        int count = 0;
        int srcX = ba.AdjustChunkPos(req.srcX);
        int srcY = ba.AdjustChunkPos(req.srcY);
        int srcZ = ba.AdjustChunkPos(req.srcZ);
        int sizeX = ba.AdjustSize(req.sizeX);
        int sizeY = ba.AdjustSize(req.sizeY);
        int sizeZ = ba.AdjustSize(req.sizeZ);

        for (int y = 0; y < sizeY; y++)
            for (int z = 0; z < sizeZ; z++)
                for (int x = 0; x < sizeX; x++)
                {
                    // TODO: Add boundry handling
                    ServerChunk chunk = ba.GetChunkOnce(srcX + x, srcY + y, srcZ + z);
                    if (chunk != null && !chunk.Empty)
                    {
                        count++;
                        chunk.DirtyForSaving = true; // Make sure this chunk gets saved, as we didn't do that.
                        send(req, req.dstX + x, req.dstY + y, req.dstZ + z, chunk);
                    }
                }

        int total = req.TotalChunkCount();
        logger.Notification("RequestType.Copy finished. chunks={0}/{1}", count, total);
    }

    #endregion

    private void send(ChunkRequest req, int cx, int cy, int cz, ServerChunk chunk)
    {
        if (chunk != null)
        {
            api.EnqueueMainThreadTask(delegate
            {
                req.onLoaded(cx, cy, cz, chunk);
            }, "map3d-loadchunk");
        }
    }
}
