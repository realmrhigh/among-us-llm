using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using Impostor.Server.LlmBots.Maps;
using Xunit;

namespace Impostor.LlmBots.Tests
{
    // Wall data behaviour on a made-up ship, so it is checked without any game data on the machine.
    public class CollisionShapeTests : IDisposable
    {
        private readonly string _file = Path.Combine(Path.GetTempPath(), "ship-" + Guid.NewGuid().ToString("N") + ".json");

        public void Dispose() => File.Delete(_file);

        private static string Square(float x0, float y0, float x1, float y1) =>
            $"{{\"name\":\"room\",\"layer\":9,\"closed\":true,\"points\":[[{x0},{y0}],[{x1},{y0}],[{x1},{y1}],[{x0},{y1}]]}}";

        private CollisionGrid Load(IEnumerable<string> solids, IReadOnlyCollection<Vector2> seeds)
        {
            File.WriteAllText(_file, "{\"ship\":\"test\",\"solids\":[" + string.Join(",", solids) + "]}", Encoding.UTF8);
            return CollisionGrid.TryLoadFile(_file, seeds)!;
        }

        private static List<Vector2> Around(float x, float y, int n) =>
            Enumerable.Range(0, n).Select(i => new Vector2(x + (i * 0.4f), y + (i * 0.3f))).ToList();

        [Fact]
        public void ShipsWithSeparatePartsKeepEveryPartThatHoldsPoints()
        {
            var seeds = Around(2, 2, 4).Concat(Around(12, 2, 4)).Append(new Vector2(-8, -8)).ToList();
            var grid = Load(new[] { Square(0, 0, 6, 6), Square(10, 0, 16, 6) }, seeds);

            Assert.True(grid.IsWalkable(new Vector2(3, 3)));
            Assert.True(grid.IsWalkable(new Vector2(13, 3)));
            Assert.False(grid.IsWalkable(new Vector2(-8, -8)));
            Assert.Null(grid.FindPath(new Vector2(3, 3), new Vector2(13, 3)));
            Assert.Equal(2, grid.Measure(seeds).KeptAreas);
        }

        [Fact]
        public void AMisplacedShipFitsBadly()
        {
            var right = Around(2, 2, 6);
            var wrong = right.Select(p => p + new Vector2(30, 0)).ToList();
            var grid = Load(new[] { Square(0, 0, 6, 6) }, right);

            Assert.True(grid.Measure(right).Fit > 0.9);
            Assert.True(grid.Measure(wrong).Fit < 0.5);
        }

        [Fact]
        public void RoutesGoAroundWalls()
        {
            var wall = "{\"name\":\"wall\",\"layer\":9,\"closed\":false,\"points\":[[5,0.5],[5,5.5]]}";
            var seeds = Around(1, 1, 3).Concat(Around(8, 1, 3)).ToList();
            var grid = Load(new[] { Square(0, 0, 10, 6), wall }, seeds);

            var route = grid.FindPath(new Vector2(2, 3), new Vector2(8, 3))!;
            Assert.NotNull(route);
            Assert.True(route.Max(p => p.Y) > 5.3f || route.Min(p => p.Y) < 0.7f);
            Assert.False(grid.HasClearLine(new Vector2(2, 3), new Vector2(8, 3)));
        }
    }
}
