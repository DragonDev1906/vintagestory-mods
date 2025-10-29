using Vintagestory.API.Server;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using System.Collections.Generic;
using System.Collections.Concurrent;
using Vintagestory.Common;
using System.Reflection;
using HarmonyLib;
using Vintagestory.Server;

namespace Map3D;

public class Map3DModSystem : ModSystem
{
    private ICoreServerAPI? sapi;
    private int mapChunksY;

    // Fields normally not available but needed for chunk loading
    // If these are null we cannot load chunks. Since they depend directly on reflection, I don't want to crash the game if this happens, so we just disable functionality.
    private AccessTools.FieldRef<object, FastRWLock>? _loadedChunksLock; // I don't think we can store the bound `ref FastRWLock`.
    private Dictionary<long, ServerChunk>? _loadedChunks; // Must only be accessed while holding the lock.

    // Chunk loading queue
    private ConcurrentQueue<ChunkRequest> chunkLoadQueue = new();

    // Persisted between restarts/reloads
    private bool freeDimensionsDirty = false;
    private List<int> freeDimensions = new();

    public static Map3DModSystem Instance(ICoreAPI api)
    {
        return api.ModLoader.GetModSystem<Map3DModSystem>();
    }

    public override void Start(ICoreAPI api)
    {
        base.Start(api);
        this.mapChunksY = api.World.BlockAccessor.MapSizeY / 32;

        api.RegisterBlockClass(Mod.Info.ModID + ".BlockMap", typeof(BlockMap));
        api.RegisterBlockEntityClass(Mod.Info.ModID + ".Map", typeof(BlockEntityMap));
    }

    public override void StartServerSide(ICoreServerAPI api)
    {
        this.sapi = api;
        base.StartServerSide(api);

        api.Event.ChunkDirty += OnChunkDirtyServer;
        api.Event.GameWorldSave += OnGameWorldSaveServer;
        api.Event.SaveGameLoaded += OnSaveGameLoadedServer;

        // I'd love to be able to just read this (or don't need it in the first place),
        // but for now this seems the only option.
        //
        // Unfortunatly almost everything in VS is a class. Just this stupid lock isn't.
        ChunkDataPool? chunkPool = readInternalField<ServerMain, ChunkDataPool>(Mod.Logger, (ServerMain)api.World, "serverChunkDataPool");
        _loadedChunks = readInternalField<ServerMain, Dictionary<long, ServerChunk>>(Mod.Logger, (ServerMain)api.World, "loadedChunks");
        _loadedChunksLock = AccessTools.FieldRefAccess<FastRWLock>(typeof(ServerMain), "loadedChunksLock");

        if (chunkPool == null)
        {
            Mod.Logger.Error("Could not get ChunkDataPool using reflection, cannot load or copy chunks");
            return;
        }
        if (_loadedChunks == null)
        {
            Mod.Logger.Error("Could not get loadedChunks dict using reflection, cannot load or copy chunks");
            return;
        }

        ChunkLoader chunkLoader = new(sapi.Event, this, chunkLoadQueue, Mod.Logger, chunkPool, sapi.World, sapi.WorldManager.CurrentWorldName);
        Mod.Logger.Notification("Started ChunkLoader thread");
        sapi.Server.AddServerThread("map3d-chunkload", chunkLoader);

        // This is annoying: We can't connect to the save game DB in any of the start methods
        // because the DB is locked. We have to retry but that never succeeds during this time.
        // Therefore we have to register a tick listener that retries this.
        // Having two GameDatabase instances shouldn't be an issue but it's creation unfortunately
        // requires the DB to be unlocked and times out after 1ms.
        // This Listener is automatically unregistered once it succeeds.
        // Luckily everything that needs this to be set-up goes through a queue, so it's not a
        // huge problem if this is initialized after the game has already started.
        // chunkLoaderSetupListenerId = api.Event.RegisterGameTickListener(OnTrySetupChunkLoaderTick, 100);
    }

    public override void Dispose()
    {
        if (sapi != null)
        {
            sapi.Event.ChunkDirty -= OnChunkDirtyServer;
            sapi.Event.GameWorldSave -= OnGameWorldSaveServer;
            sapi.Event.SaveGameLoaded -= OnSaveGameLoadedServer;

            chunkLoadQueue.Clear();
        }
        base.Dispose();
    }

    private void OnGameWorldSaveServer()
    {
        if (freeDimensionsDirty)
        {
            // Only called Server-side and only after StartServerSide was called.
            sapi!.WorldManager.SaveGame.StoreData("map3d.freeDimensions", freeDimensions);
            freeDimensionsDirty = false;
        }
    }
    private void OnSaveGameLoadedServer()
    {
        // Only called Server-side and only after StartServerSide was called.
        freeDimensions = sapi!.WorldManager.SaveGame.GetData<List<int>>("map3d.freeDimensions") ?? new();
        freeDimensionsDirty = false;


    }

    private void OnChunkDirtyServer(Vec3i chunkCoord, IWorldChunk chunk, EnumChunkDirtyReason reason)
    {
        // We cannot use ChunkColumnLoaded (even though we're loading the chunks that way),
        // because that event doesn't give us the Y coordinate and thus doesn't give us the dimension.
        // From what I can tell we also can't get it from chunk itself, so we have to use this more
        // verbose event.
        int dimension = chunkCoord.Y >> 10;
        if (dimension == 1 && reason == EnumChunkDirtyReason.NewlyLoaded)
        {
            // A chunk in the relevant dimension that was just loaded.
            // It's not neccessarily from us (anyone that uses the mini dimension system
            // and loads chunks can cause this), but at the moment it's likely ours.
            // Next we get the subdimension ID and check if it is actually one of our mini
            // dimensions.
            // See https://wiki.vintagestory.at/index.php/Modding:Minidimension#ChunkIndex3D
            int subDimensionId = ((chunkCoord.Z >> 9) << 12) + chunkCoord.X >> 9;
            // Only called server-side after StartServerSide was called
            IMiniDimension dim = sapi!.Server.GetMiniDimension(subDimensionId);
            if (dim is MapMiniDimension mdim)
                mdim.AddLoadedChunk(chunkCoord, chunk);
        }
    }

    internal static T? readInternalField<O, T>(ILogger logger, O obj, string fieldName)
    {
        if (obj == null)
        {
            logger.Error("{0}.{1} Cannot read internal field of null", typeof(O).Name, fieldName);
            return default(T);
        }

        FieldInfo? field = typeof(O).GetField(
            fieldName,
            // Include public in case they change it to be public
            BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance
        );
        if (field == null)
        {
            logger.Error("{0}.{1} does not exist", typeof(O).Name, fieldName);
            return default(T);
        }
        if (field.IsPublic)
        {
            logger.Warning("{0}.{1} is public, reflection is no longer needed", typeof(O).Name, fieldName);
        }
        object? val = field.GetValue(obj);
        if (val == null)
        {
            logger.Warning("{0}.{1} is null", typeof(O).Name, fieldName);
            return default(T);
        }
        if (!val.GetType().IsAssignableTo(typeof(T)))
        {
            logger.Error("{0}.{1} has an unexpected type: {2} that is not assignable to {3}", typeof(O).Name, fieldName, val.GetType(), typeof(T));
            return default(T);
        }
        return (T)val;
    }

    // I wish we had access to this method and an easier way to create chunks.
    public void AddChunkToLoadedListServer(long cindex, ServerChunk chunk)
    {
        // Should never get this far, but this is a failsave to prevent crashing should we be
        // unable to add loaded chunks due to changes in the basegame.
        if (_loadedChunksLock == null || _loadedChunks == null)
            return;

        _loadedChunksLock.Invoke(sapi!.World).AcquireWriteLock();
        try
        {
            if (_loadedChunks.TryGetValue(cindex, out var value))
                value.Dispose();
            _loadedChunks[cindex] = chunk;
        }
        finally
        {
            _loadedChunksLock.Invoke(sapi.World).ReleaseWriteLock();
        }
    }

    public void LoadChunksV2(ChunkRequest req)
    {
        chunkLoadQueue.Enqueue(req);
    }

    public int AllocateMiniDimensionServer(IMiniDimension dim)
    {
        int id;
        if (freeDimensions.Count > 0)
        {
            int last = freeDimensions.Count - 1;
            id = freeDimensions[last];
            freeDimensions.RemoveAt(last);
            freeDimensionsDirty = true;

            sapi!.Server.SetMiniDimension(dim, id);
            Mod.Logger.Notification("Reused subdimension id: {0}", id);
        }
        else
        {
            id = sapi!.Server.LoadMiniDimension(dim);
            Mod.Logger.Notification("Allocated new subdimension id: {0}", id);
        }
        dim.SetSubDimensionId(id);
        return id;
    }

    public void FreeMiniDimension(IMiniDimension dim)
    {
        dim.ClearChunks();
        // Only call this method on the server-side
        if (sapi != null)
            dim.UnloadUnusedServerChunks();
        freeDimensions.Add(dim.subDimensionId);
        freeDimensionsDirty = true;
        Mod.Logger.Notification("Freed subdimension id: {0}", dim.subDimensionId);
    }
}
