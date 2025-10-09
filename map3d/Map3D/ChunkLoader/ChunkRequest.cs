using System.Collections.Generic;
using Vintagestory.Server;

namespace Map3D;

public enum RequestType
{
    Load,
    Copy,
    LoadOrCopy,
}

public delegate void ChunkLoaded(int cx, int cy, int cz, ServerChunk chunk);

public class ChunkRequest
{
    public bool addToLoadedList = true;
    public RequestType type;
    public Lod lod; // Ignored if type==RequestType.Load

    internal ChunkLoaded onLoaded;

    // The chunk we want (i.e. in the subdimension)
    internal int dstX;
    internal int dstY;
    internal int dstZ;

    // Where to copy from
    internal int srcX;
    internal int srcY;
    internal int srcZ;

    // Number of chunks before applying Lod.
    internal int sizeX;
    internal int sizeY;
    internal int sizeZ;


    // TODO: mode enum/interface

    private ChunkRequest() { }
    public ChunkRequest(
        ChunkLoaded onLoaded,
        int srcX, int srcY, int srcZ,
        int sizeX, int sizeY, int sizeZ,
        int dstX, int dstY, int dstZ,
        Lod lod = Lod.None,
        RequestType type = RequestType.Load
    )
    {
        this.type = type;
        this.lod = lod;
        this.onLoaded = onLoaded;
        this.dstX = dstX;
        this.dstY = dstY;
        this.dstZ = dstZ;
        this.srcX = srcX;
        this.srcY = srcY;
        this.srcZ = srcZ;
        this.sizeX = sizeX;
        this.sizeY = sizeY;
        this.sizeZ = sizeZ;
    }

    public static ChunkRequest SimpleLoad(
        ChunkLoaded onLoaded,
        int cx, int cy, int cz,
        int sizeX, int sizeY, int sizeZ,
        Lod lod = Lod.None,
        RequestType type = RequestType.Load)
    {
        return new ChunkRequest()
        {
            type = type,
            lod = lod,
            onLoaded = onLoaded,
            dstX = cx,
            dstY = cy,
            dstZ = cz,
            sizeX = sizeX,
            sizeY = sizeY,
            sizeZ = sizeZ,
        };
    }

    // internal IEnumerable<(int, int, int, bool)> IterLod()
    // {
    //     // for (int x = 0; x < sizeX; x++)
    //     //     for (int z = 0; z < sizeZ; z++)
    //     //         for (int y = 0; y < sizeY; y++)
    //     //             yield return (dstX + x, dstY + y, dstZ + z);
    // }

    internal IEnumerable<(int, int, int)> IterDestination()
    {
        for (int x = 0; x < sizeX; x++)
            for (int z = 0; z < sizeZ; z++)
                for (int y = 0; y < sizeY; y++)
                    yield return (dstX + x, dstY + y, dstZ + z);
    }
    internal int TotalChunkCount()
    {
        return sizeX * sizeY * sizeZ;
    }
}
