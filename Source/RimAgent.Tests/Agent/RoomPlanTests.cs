using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using RimAgent.InGameTests;

namespace RimAgent.Tests.Agent;

[TestFixture]
public class RoomPlanTests
{
    private static readonly (int X, int Z)[] Beds = [(101, 137), (102, 137)];
    private static readonly (int X, int Z)[] Benches = [(106, 137)];
    private static readonly (int X, int Z)[] Doors = [(100, 138), (109, 138)];

    private static HashSet<(int X, int Z)> Outline(int x1, int z1, int x2, int z2)
    {
        return [.. from x in Enumerable.Range(x1, x2 - x1 + 1)
                   from z in Enumerable.Range(z1, z2 - z1 + 1)
                   where x == x1 || x == x2 || z == z1 || z == z2
                   select (x, z)];
    }

    private static HashSet<(int X, int Z)> House(bool gap)
    {
        HashSet<(int X, int Z)> walls = Outline(100, 135, 109, 140);
        for (int z = 136; z <= 139; z++)
        {
            if (!gap || z != 138) walls.Add((104, z));
        }
        return walls;
    }

    [Test]
    public void SevenByFiveOutlineIsNotAFiveByFiveRoom()
    {
        RoomPlan.Room room = RoomPlan.Enclosed(Outline(121, 131, 127, 135)).Single();
        Assert.That((room.Width, room.Height), Is.EqualTo((5, 3)));
        Assert.That(room.IsRectangle(5, 5), Is.False);
        Assert.That(RoomPlan.Enclosed(Outline(121, 131, 127, 137)).Single().IsRectangle(5, 5), Is.True);
    }

    [Test]
    public void GapInTheSharedWallMakesOneRoom()
    {
        List<RoomPlan.Room> rooms = RoomPlan.Enclosed(House(gap: true));
        Assert.That(rooms, Has.Count.EqualTo(1));
        Assert.That(RoomPlan.BedroomLabPairs(rooms, Doors, Beds, Benches), Is.Zero);
    }

    [Test]
    public void ClosedSharedWallMakesBedroomAndLab()
    {
        List<RoomPlan.Room> rooms = RoomPlan.Enclosed(House(gap: false));
        Assert.That(rooms, Has.Count.EqualTo(2));
        Assert.That(RoomPlan.BedroomLabPairs(rooms, Doors, Beds, Benches), Is.EqualTo(1));
        Assert.That(RoomPlan.BedroomLabPairs(rooms, [(104, 138), (109, 138)], Beds, Benches), Is.EqualTo(1), "穿過臥室進出也算各有一扇門");
        Assert.That(RoomPlan.BedroomLabPairs(rooms, [(104, 138)], Beds, Benches), Is.Zero, "一扇門不能兩間共用");
        Assert.That(RoomPlan.BedroomLabPairs(rooms, Doors, Beds, Beds), Is.Zero);
    }

    [Test]
    public void OpenWallsEncloseNothing()
    {
        HashSet<(int X, int Z)> walls = Outline(0, 0, 6, 6);
        walls.Remove((3, 0));
        Assert.That(RoomPlan.Enclosed(walls), Is.Empty);
        Assert.That(RoomPlan.Enclosed([]), Is.Empty);
    }
}
