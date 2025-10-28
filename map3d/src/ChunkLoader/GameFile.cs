using System;
using System.Data;
using Microsoft.Data.Sqlite;
using Vintagestory.API.Common;
using Vintagestory.Common;
using Vintagestory.Common.Database;
using Vintagestory.Server;

namespace Map3D;

// Like the GameDatabase abstraction but simpler and not as problematic to initialize.
// In addition to that it has some convenient functions to deserialize chunks that are
// normally found elsewhere.
internal class GameFile
{
    SqliteConnection db;
    internal ILogger logger;
    internal ChunkDataPool chunkPool;
    internal IWorldAccessor worldAccessorForResolve;

    internal GameFile(ILogger logger, ChunkDataPool chunkPool, IWorldAccessor worldAccessorForResolve, string databaseFileName)
    {
        this.logger = logger;
        this.chunkPool = chunkPool;
        this.worldAccessorForResolve = worldAccessorForResolve;

        var conf = new System.Data.Common.DbConnectionStringBuilder {
            { "Data Source", databaseFileName },
            { "Pooling", "false" }
        };

        db = new SqliteConnection(conf.ToString());
        db.Open();
    }

    internal void Dispose()
    {
        db.Close();
    }

    // See ServerSystemSupplyChunks, but without the column restriction.
    internal ServerChunk? loadChunk(int cx, int cy, int cz)
    {
        byte[]? data = getChunk(ChunkPos.ToChunkIndex(cx, cy & 0x1ff, cz, cy >> 10));

        // byte[] data = db.GetChunk(cx, cy & 0x1ff, cz, cy >> 10);
        if (data == null) return null;
        try
        {
            ServerChunk chunk = ServerChunk.FromBytes(data, chunkPool, worldAccessorForResolve);
            // ServerSystemSupplyChunks sets serverMapChunk. I hope we don't need that, as the
            // player never is in these chunks and I'm not even sure if the map works properly
            // in another dimension.
            chunk.MarkFresh();
            return chunk;
        }
        catch (Exception ex)
        {
            logger.Error("Failed deserializing a chunk, Exception: {0}", ex);
            return null;
        }
    }

    private byte[]? getChunk(ulong position)
    {
        using SqliteCommand cmd = db.CreateCommand();

        var pos = cmd.CreateParameter();
        pos.ParameterName = "position";
        pos.DbType = DbType.UInt64;
        pos.Value = position;

        cmd.CommandText = "SELECT data FROM chunk WHERE position=@position";
        cmd.Parameters.Add(pos);

        using SqliteDataReader reader = cmd.ExecuteReader();
        if (reader.Read())
        {
            return reader["data"] as byte[];
        }
        return null;
    }
}
