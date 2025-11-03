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
public class GameFile
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
        return loadChunkPos(ChunkPos.ToChunkIndex(cx, cy & 0x1ff, cz, cy >> 10));
    }
    internal ServerChunk? loadChunk(long cindex)
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
        ulong _cindex = (ulong)cindex;
        ulong cpos = (_cindex & 0x1fffff) // x
            | ((_cindex << 6) & ((ulong)0x1fffff << 27)) // z
            | ((_cindex << 12) & ((ulong)0x1ff << 54)) // y
            | ((_cindex >> 8) & ((ulong)0x1f << 49)) // dim upper
            | ((_cindex >> 30) & ((ulong)0x1f << 22)); // dim lower

        return loadChunkPos(cpos);
    }
    // Note that this differs from the cindex used everywhere else.
    // See https://wiki.vintagestory.at/Special:MyLanguage/Modding:VCDBS_format#ChunkPos
    internal ServerChunk? loadChunkPos(ulong cpos)
    {
        byte[]? data = getChunk(cpos);

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
