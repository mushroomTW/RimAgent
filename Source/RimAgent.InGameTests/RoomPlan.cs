#nullable enable

using System.Collections.Generic;
using System.Linq;

namespace RimAgent.InGameTests;

/// <summary>由牆與門的格子推出圍起來的房間。純幾何；單元測試以連結方式編進 RimAgent.Tests。</summary>
internal static class RoomPlan
{
    internal sealed record Room(HashSet<(int X, int Z)> Cells, HashSet<(int X, int Z)> Border)
    {
        public int Width => Cells.Max(c => c.X) - Cells.Min(c => c.X) + 1;

        public int Height => Cells.Max(c => c.Z) - Cells.Min(c => c.Z) + 1;

        public bool IsRectangle(int width, int height)
        {
            return Width == width && Height == height && Cells.Count == width * height;
        }
    }

    /// <param name="barriers">牆、門與其他填滿整格的東西（例如裝在牆上的冷氣）。</param>
    public static List<Room> Enclosed(IReadOnlyCollection<(int X, int Z)> barriers)
    {
        var rooms = new List<Room>();
        if (barriers.Count == 0) return rooms;
        HashSet<(int X, int Z)> solid = [.. barriers];
        int minX = solid.Min(c => c.X), maxX = solid.Max(c => c.X), minZ = solid.Min(c => c.Z), maxZ = solid.Max(c => c.Z);
        var bounds = (minX, maxX, minZ, maxZ);
        var seen = new HashSet<(int X, int Z)>();
        for (int x = minX; x <= maxX; x++)
        {
            for (int z = minZ; z <= maxZ; z++)
            {
                if (solid.Contains((x, z)) || !seen.Add((x, z))) continue;
                if (FloodRoom((x, z), solid, seen, bounds) is { } room) rooms.Add(room);
            }
        }
        return rooms;
    }

    private static Room? FloodRoom(
        (int X, int Z) start,
        HashSet<(int X, int Z)> solid,
        HashSet<(int X, int Z)> seen,
        (int MinX, int MaxX, int MinZ, int MaxZ) bounds)
    {
        var room = new Room([start], []);
        bool open = false;
        var queue = new Queue<(int X, int Z)>(room.Cells);
        while (queue.TryDequeue(out (int X, int Z) cell))
        {
            foreach ((int X, int Z) next in Neighbours(cell))
            {
                if (solid.Contains(next))
                {
                    room.Border.Add(next);
                }
                else if (IsOutside(next, bounds))
                {
                    open = true;
                }
                else if (seen.Add(next))
                {
                    room.Cells.Add(next);
                    queue.Enqueue(next);
                }
            }
        }
        return open ? null : room;
    }

    private static bool IsOutside((int X, int Z) cell, (int MinX, int MaxX, int MinZ, int MaxZ) bounds) =>
        cell.X < bounds.MinX || cell.X > bounds.MaxX || cell.Z < bounds.MinZ || cell.Z > bounds.MaxZ;

    /// <summary>臥室與研究室各自圍起、各有一扇門、共用一面牆。</summary>
    public static int BedroomLabPairs(
        IReadOnlyList<Room> rooms,
        IReadOnlyCollection<(int X, int Z)> doors,
        IReadOnlyCollection<(int X, int Z)> beds,
        IReadOnlyCollection<(int X, int Z)> benches)
    {
        return rooms.Where(bedroom => beds.Where(bedroom.Cells.Contains).Skip(1).Any())
            .Sum(bedroom => rooms.Count(lab => lab != bedroom && benches.Any(lab.Cells.Contains)
                && bedroom.Border.Any(c => lab.Border.Contains(c) && !doors.Contains(c))
                && doors.Any(a => bedroom.Border.Contains(a) && doors.Any(b => b != a && lab.Border.Contains(b)))));
    }

    private static IEnumerable<(int X, int Z)> Neighbours((int X, int Z) c)
    {
        return [(c.X + 1, c.Z), (c.X - 1, c.Z), (c.X, c.Z + 1), (c.X, c.Z - 1)];
    }
}
