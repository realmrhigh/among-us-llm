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
    public class MapTests
    {
        private readonly ITestOutputHelper _output;

        public MapTests(ITestOutputHelper output)
        {
            _output = output;
        }

        public static IEnumerable<object[]> AllMaps => Enum.GetValues<MapTypes>().Select(m => new object[] { m });

        [Theory]
        [MemberData(nameof(AllMaps))]
        public void NavGraphIsConnected(MapTypes type)
        {
            var map = BotMap.Get(type);
            Assert.True(map.Nav.Nodes.Count > 10);
            Assert.True(map.Nav.IsConnected(), $"{type} graph has islands");
        }

        [Theory]
        [InlineData(MapTypes.Skeld)]
        [InlineData(MapTypes.Dleks)]
        public void EveryConsoleAndVentIsOnTheGraph(MapTypes type)
        {
            var map = BotMap.Get(type);
            foreach (var task in map.Tasks.Values)
            {
                foreach (var console in task.Consoles)
                {
                    var p = type == MapTypes.Dleks ? new Vector2(-console.Position.X, console.Position.Y) : console.Position;

                    // Dleks data is already mirrored, Skeld data is not; both must land next to a waypoint.
                    var near = map.Nav.Nearest(console.Position);
                    var d = Vector2.Distance(near.Position, console.Position);
                    Assert.True(d < 2.5f, $"{task.TaskType} console at {console.Position} in {console.Room} is {d:0.0} from {near.Id}");
                }
            }

            foreach (var vent in map.Vents)
            {
                var near = map.Nav.Nearest(vent.Position);
                Assert.True(Vector2.Distance(near.Position, vent.Position) < 0.06f, $"vent {vent.Name} not on graph");
            }
        }

        [Fact]
        public void SkeldPathsStayOnHallways()
        {
            var map = BotMap.Get(MapTypes.Skeld);
            var stops = map.Tasks.Values.SelectMany(t => t.Consoles).Select(c => c.Position).Distinct().ToList();
            var worst = 0f;
            foreach (var a in stops)
            {
                foreach (var b in stops)
                {
                    var path = map.Nav.FindPath(a, b);
                    for (var i = 1; i < path.Count; i++)
                    {
                        worst = Math.Max(worst, Vector2.Distance(path[i - 1], path[i]));
                    }
                }
            }

            _output.WriteLine($"longest straight segment: {worst:0.0}");
            Assert.True(worst < 12f, "a route makes a straight jump that is too long, so it would cut through rooms");
        }

        [Fact]
        public void ReactorToNavigationGoesThroughTheCafeteria()
        {
            var map = BotMap.Get(MapTypes.Skeld);
            var path = map.Nav.FindPath(map.Nav["RX_START"].Position, map.Nav["NAV_STEER"].Position);
            var rooms = path.Select(map.RoomAt).Distinct().ToList();
            _output.WriteLine(string.Join(" -> ", rooms));
            Assert.Contains("Cafeteria", rooms);
        }

        [Theory]
        [MemberData(nameof(AllMaps))]
        public void TasksCanBePlanned(MapTypes type)
        {
            var map = BotMap.Get(type);
            var rng = new Random(3);
            foreach (var task in map.Tasks.Values)
            {
                var stops = TaskPlanner.Plan(map, task, rng);
                Assert.NotEmpty(stops);
                Assert.All(stops, s => Assert.True(s.Seconds > 0));
            }
        }
    }
}
