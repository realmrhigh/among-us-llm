using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Impostor.Api.Innersloth;
using Impostor.Server.LlmBots.Maps;
using Xunit;
using Xunit.Abstractions;

namespace Impostor.LlmBots.Tests
{
    /// <summary>
    ///     Checks the wall data extracted with tools/extract_collision.py against what the bots already know about
    ///     each map. The data comes from a local game install, so these tests do nothing when it is missing.
    /// </summary>
    public class CollisionTests
    {
        private readonly ITestOutputHelper _output;

        public CollisionTests(ITestOutputHelper output)
        {
            _output = output;
        }

        public static IEnumerable<object[]> Maps => new[] { MapTypes.Skeld, MapTypes.Dleks }
            .Select(m => new object[] { m });

        [Theory]
        [MemberData(nameof(Maps))]
        public void ConsolesDoorsAndVentsAreOnWalkableGround(MapTypes type)
        {
            var grid = BotMap.Get(type).Nav.Collision;
            if (grid == null)
            {
                _output.WriteLine($"{type}: no wall data, skipped");
                return;
            }

            var map = BotMap.Get(type);
            var points = map.Tasks.Values.SelectMany(t => t.Consoles.Select(c => (What: $"{t.TaskType} console", Pos: c.Position)))
                .Concat(map.Doors.Select(d => (What: "door", Pos: d.Position)))
                .Concat(map.Vents.Select(v => (What: $"vent {v.Name}", Pos: v.Position)))
                .Append((What: "spawn", Pos: map.SpawnCenter))
                .Append((What: "meeting", Pos: map.MeetingCenter))
                .ToList();

            // A console can sit right in a wall or piece of furniture; it only has to be close to free ground.
            var farFromFree = points.Where(p => !NearFree(grid, p.Pos, 1.0f)).ToList();
            foreach (var p in farFromFree)
            {
                _output.WriteLine($"{type}: {p.What} at {p.Pos} is not near walkable ground");
            }

            Assert.True(farFromFree.Count <= points.Count / 20, $"{type}: {farFromFree.Count} of {points.Count} points are inside walls: the wall data does not line up with the map");
        }

        [Fact]
        public void DumpSkeldRoutesForInspection()
        {
            var path = Environment.GetEnvironmentVariable("LLMBOTS_DUMP_ROUTES");
            var map = BotMap.Get(MapTypes.Skeld);
            if (string.IsNullOrEmpty(path) || map.Nav.Collision == null)
            {
                return;
            }

            var routes = new List<object>();
            var hubs = map.Rooms.Select(r => (Room: r, Position: map.RoomHub(r))).ToList();
            foreach (var a in hubs)
            {
                foreach (var b in hubs.Where(h => h.Room != a.Room))
                {
                    routes.Add(new { from = a.Room, to = b.Room, points = map.Nav.FindPath(a.Position, b.Position).Select(p => new[] { p.X, p.Y }).ToList() });
                }
            }

            System.IO.File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(routes));
        }

        [Theory]
        [MemberData(nameof(Maps))]
        public void RoutesReachEveryTaskAndNeverCrossAWall(MapTypes type)
        {
            var map = BotMap.Get(type);
            var grid = map.Nav.Collision;
            if (grid == null)
            {
                return;
            }

            var from = map.SpawnCenter;
            var unreachable = 0;
            var total = 0;
            foreach (var console in map.Tasks.Values.SelectMany(t => t.Consoles))
            {
                total++;
                var route = map.Nav.FindPath(from, console.Position);
                var walled = grid.FindPath(from, console.Position);
                if (walled == null)
                {
                    unreachable++;
                    _output.WriteLine($"{type}: no wall-aware route to {console.Room} console at {console.Position}");
                    continue;
                }

                // Every leg except the first and last hop (which start/end on the exact spot) must be clear.
                for (var i = 1; i < walled.Count - 2; i++)
                {
                    Assert.True(grid.HasClearLine(walled[i], walled[i + 1]), $"{type}: leg {walled[i]} -> {walled[i + 1]} to {console.Room} crosses a wall");
                }

                Assert.Equal(walled.Count, route.Count);
            }

            Assert.True(unreachable <= Math.Max(1, total / 25), $"{type}: {unreachable} of {total} consoles cannot be reached without crossing a wall");
        }

        private static bool NearFree(CollisionGrid grid, Vector2 position, float radius)
        {
            for (var dx = -radius; dx <= radius; dx += 0.1f)
            {
                for (var dy = -radius; dy <= radius; dy += 0.1f)
                {
                    if (grid.IsWalkable(position + new Vector2(dx, dy)))
                    {
                        return true;
                    }
                }
            }

            return false;
        }
    }
}
