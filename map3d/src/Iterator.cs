using Vintagestory.API.MathTools;
using System.Collections.Generic;

namespace Map3D;

// We're not in Rust, but I think this kind of iterator is beeter suited for this
// than a function that returns all positions in an array.


public class Iter
{
    public static IEnumerable<(int, int)> RectChunks(BlockPos corner1, BlockPos corner2)
    {
        return Rect(corner1.X / 32, corner1.Z / 32, corner2.X / 32, corner2.Z / 32);
    }

    // public static IEnumerable<(int, int)> RectCentered(int cx, int cy, int sizeX, int sizeY)
    // {
    //     return Rect(cx - sizeX / 2, cy - sizeY / 2, cx + sizeX / 2, cy + sizeY / 2);
    // }

    public static IEnumerable<(int, int)> Rect(int x1, int y1, int x2, int y2)
    {
        // Normalize corners
        if (x1 > x2)
        {
            int tmp = x1;
            x1 = x2;
            x2 = tmp;
        }
        if (y1 > y2)
        {
            int tmp = y1;
            y1 = y2;
            y2 = tmp;
        }

        for (int x = x1; x <= x2; x++)
            for (int y = y1; y <= y2; y++)
                yield return (x, y);
    }


    // Currently Broken, do not use this.
    private static IEnumerable<Vec2i> RectSpiralOut(int cx, int cy, int sizeX, int sizeY)
    {
        if (sizeX == 0 || sizeY == 0)
            yield break;


        int x = 0;
        int y = 0;
        int layer = 1;
        int dir = 0;

        while (true)
        {
            yield return new(cx + x, cy + y);

            switch (dir)
            {
                case 0:
                    x++;
                    if (x == layer)
                    {
                        if (layer > sizeX / 2 && layer > sizeY / 2)
                            yield break;
                        else if (layer > sizeY / 2)
                            dir = 2;
                        else
                            dir++;
                    }
                    break;
                case 1:
                    y++;
                    if (y == layer)
                    {
                        if (layer > sizeY / 2 && layer > sizeY / 2)
                            yield break;
                        else if (layer > sizeX / 2)
                            dir = 3;
                        else
                            dir++;
                    }
                    break;
                case 2:
                    x--;
                    if (-x == layer)
                    {
                        // TODO: Off by one?
                        if (layer > sizeX / 2 && layer > sizeY / 2)
                            yield break;
                        else if (layer > sizeY / 2)
                            dir = 0;
                        else
                            dir++;
                    }
                    break;
                case 3:
                    y++;
                    if (y == layer)
                    {
                        // TODO: Off by one?
                        if (layer > sizeY / 2 && layer > sizeY / 2)
                            yield break;
                        else if (layer > sizeX / 2)
                            dir = 1;
                        else
                            dir = 0;
                    }
                    break;
            }
        }
    }
}
