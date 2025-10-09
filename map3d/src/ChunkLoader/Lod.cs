using System.Diagnostics;

namespace Map3D;

public enum Lod
{
    None = 0,
    Lod2,
    Lod4,
    Lod8,
    Lod16,
    ChunkAsOneBlock,
}

static class LodMethods
{
    public static int size(this Lod lod)
    {
        switch (lod)
        {
            case Lod.None:
                return 1;
            case Lod.Lod2:
                return 2;
            case Lod.Lod4:
                return 4;
            case Lod.Lod8:
                return 8;
            case Lod.Lod16:
                return 16;
            case Lod.ChunkAsOneBlock:
                return 32;
            default:
                throw new UnreachableException();
        }
    }
    public static int shift(this Lod lod)
    {
        switch (lod)
        {
            case Lod.None:
                return 0;
            case Lod.Lod2:
                return 1;
            case Lod.Lod4:
                return 2;
            case Lod.Lod8:
                return 3;
            case Lod.Lod16:
                return 4;
            case Lod.ChunkAsOneBlock:
                return 5;
            default:
                throw new UnreachableException();
        }
    }
}

