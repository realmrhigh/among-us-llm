using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;

namespace Impostor.Server.LlmBots.Maps
{
    /// <summary>
    ///     A walkability grid built from the wall / obstacle shapes of a ship (see tools/extract_collision.py).
    ///     Walls are widened by the size of a player so a route that stays on free cells never clips a wall.
    /// </summary>
    internal sealed class CollisionGrid
    {
        public const float CellSize = 0.1f;
        public const float PlayerRadius = 0.24f;

        private const string DirectoryName = "collision-data";

        private readonly bool[] _blocked;
        private readonly int _width;
        private readonly int _height;
        private readonly Vector2 _origin;

        private CollisionGrid(List<(Vector2 A, Vector2 B)> segments, Vector2 min, Vector2 max, IReadOnlyCollection<Vector2> seeds)
        {
            _origin = min - new Vector2(2f, 2f);
            _width = (int)Math.Ceiling((max.X - min.X + 4f) / CellSize) + 1;
            _height = (int)Math.Ceiling((max.Y - min.Y + 4f) / CellSize) + 1;
            _blocked = new bool[_width * _height];

            var reach = (int)Math.Ceiling(PlayerRadius / CellSize) + 1;
            foreach (var (a, b) in segments)
            {
                var (ax, ay) = ToCell(a);
                var (bx, by) = ToCell(b);
                var x0 = Math.Max(0, Math.Min(ax, bx) - reach);
                var x1 = Math.Min(_width - 1, Math.Max(ax, bx) + reach);
                var y0 = Math.Max(0, Math.Min(ay, by) - reach);
                var y1 = Math.Min(_height - 1, Math.Max(ay, by) + reach);
                for (var y = y0; y <= y1; y++)
                {
                    for (var x = x0; x <= x1; x++)
                    {
                        if (DistanceToSegment(CellCenter(x, y), a, b) <= PlayerRadius)
                        {
                            _blocked[(y * _width) + x] = true;
                        }
                    }
                }
            }

            KeepSeededRegion(seeds);
        }

        /// <summary>
        ///     Loads the wall data of a ship (skeld, mira, polus, airship, fungle, april), or returns null when the
        ///     data has not been extracted from a local game install.
        /// </summary>
        public static CollisionGrid? TryLoad(string ship, IReadOnlyCollection<Vector2> seeds)
        {
            var file = Find(ship);
            if (file == null)
            {
                return null;
            }

            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllBytes(file));
                var segments = new List<(Vector2, Vector2)>();
                var min = new Vector2(float.MaxValue);
                var max = new Vector2(float.MinValue);
                foreach (var solid in doc.RootElement.GetProperty("solids").EnumerateArray())
                {
                    var points = new List<Vector2>();
                    foreach (var p in solid.GetProperty("points").EnumerateArray())
                    {
                        var v = new Vector2(p[0].GetSingle(), p[1].GetSingle());
                        points.Add(v);
                        min = Vector2.Min(min, v);
                        max = Vector2.Max(max, v);
                    }

                    for (var i = 1; i < points.Count; i++)
                    {
                        segments.Add((points[i - 1], points[i]));
                    }

                    if (solid.GetProperty("closed").GetBoolean() && points.Count > 2)
                    {
                        segments.Add((points[^1], points[0]));
                    }
                }

                return segments.Count == 0 ? null : new CollisionGrid(segments, min, max, seeds);
            }
            catch (Exception e) when (e is IOException or JsonException or KeyNotFoundException or InvalidOperationException)
            {
                return null;
            }
        }

        public bool IsWalkable(Vector2 position)
        {
            var (x, y) = ToCell(position);
            return InBounds(x, y) && !_blocked[(y * _width) + x];
        }

        /// <summary>
        ///     Checks that a straight walk between two points never touches a wall.
        /// </summary>
        public bool HasClearLine(Vector2 a, Vector2 b)
        {
            var length = Vector2.Distance(a, b);
            var steps = Math.Max(1, (int)Math.Ceiling(length / (CellSize * 0.5f)));
            for (var i = 0; i <= steps; i++)
            {
                if (!IsWalkable(Vector2.Lerp(a, b, i / (float)steps)))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        ///     Finds a route that avoids walls. Returns null when either end cannot be reached.
        /// </summary>
        public List<Vector2>? FindPath(Vector2 from, Vector2 to)
        {
            var start = NearestFree(from);
            var goal = NearestFree(to);
            if (start < 0 || goal < 0)
            {
                return null;
            }

            var cells = AStar(start, goal);
            if (cells == null)
            {
                return null;
            }

            var points = new List<Vector2>(cells.Count);
            foreach (var index in cells)
            {
                points.Add(CellCenter(index % _width, index / _width));
            }

            var route = new List<Vector2> { from };
            route.AddRange(Smooth(points));
            route.Add(to);

            // Drop points that are practically on top of each other.
            var result = new List<Vector2>();
            foreach (var p in route)
            {
                if (result.Count == 0 || Vector2.Distance(result[^1], p) > 0.05f)
                {
                    result.Add(p);
                }
            }

            return result;
        }

        /// <summary>
        ///     Blocks every free area except the one most of the given points (consoles, doors, spawn...) are in. That
        ///     removes the space outside the ship and the sealed inside of tables, which would otherwise be places a
        ///     route could start or end in.
        /// </summary>
        private void KeepSeededRegion(IReadOnlyCollection<Vector2> seeds)
        {
            var label = new int[_blocked.Length];
            var count = 0;
            var queue = new Queue<int>();
            for (var i = 0; i < _blocked.Length; i++)
            {
                if (_blocked[i] || label[i] != 0)
                {
                    continue;
                }

                var id = ++count;
                label[i] = id;
                queue.Enqueue(i);
                while (queue.Count > 0)
                {
                    var current = queue.Dequeue();
                    var cx = current % _width;
                    var cy = current / _width;
                    foreach (var (nx, ny) in new[] { (cx + 1, cy), (cx - 1, cy), (cx, cy + 1), (cx, cy - 1) })
                    {
                        if (!InBounds(nx, ny))
                        {
                            continue;
                        }

                        var next = (ny * _width) + nx;
                        if (!_blocked[next] && label[next] == 0)
                        {
                            label[next] = id;
                            queue.Enqueue(next);
                        }
                    }
                }
            }

            var votes = new Dictionary<int, int>();
            foreach (var seed in seeds)
            {
                var cell = NearestFree(seed);
                if (cell >= 0)
                {
                    votes[label[cell]] = votes.GetValueOrDefault(label[cell]) + 1;
                }
            }

            if (votes.Count == 0)
            {
                return;
            }

            var keep = votes.OrderByDescending(v => v.Value).First().Key;
            for (var i = 0; i < _blocked.Length; i++)
            {
                if (label[i] != keep)
                {
                    _blocked[i] = true;
                }
            }
        }

        private static string? Find(string ship)
        {
            var name = ship + ".json";
            foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
            {
                var directory = new DirectoryInfo(start);
                for (var depth = 0; depth < 8 && directory != null; depth++, directory = directory.Parent)
                {
                    var candidate = Path.Combine(directory.FullName, DirectoryName, name);
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
            }

            return null;
        }

        private static float DistanceToSegment(Vector2 p, Vector2 a, Vector2 b)
        {
            var ab = b - a;
            var lengthSquared = ab.LengthSquared();
            if (lengthSquared < 1e-9f)
            {
                return Vector2.Distance(p, a);
            }

            var t = Math.Clamp(Vector2.Dot(p - a, ab) / lengthSquared, 0f, 1f);
            return Vector2.Distance(p, a + (ab * t));
        }

        private (int X, int Y) ToCell(Vector2 p) => ((int)Math.Floor((p.X - _origin.X) / CellSize), (int)Math.Floor((p.Y - _origin.Y) / CellSize));

        private Vector2 CellCenter(int x, int y) => _origin + new Vector2((x + 0.5f) * CellSize, (y + 0.5f) * CellSize);

        private bool InBounds(int x, int y) => x >= 0 && y >= 0 && x < _width && y < _height;

        /// <summary>
        ///     Gets the cell index of the closest free cell (the point itself when it is free), or -1.
        /// </summary>
        private int NearestFree(Vector2 p)
        {
            var (cx, cy) = ToCell(p);
            if (InBounds(cx, cy) && !_blocked[(cy * _width) + cx])
            {
                return (cy * _width) + cx;
            }

            var best = -1;
            var bestDistance = float.MaxValue;
            const int Search = 25;
            for (var y = cy - Search; y <= cy + Search; y++)
            {
                for (var x = cx - Search; x <= cx + Search; x++)
                {
                    if (!InBounds(x, y) || _blocked[(y * _width) + x])
                    {
                        continue;
                    }

                    var d = Vector2.DistanceSquared(CellCenter(x, y), p);
                    if (d < bestDistance)
                    {
                        bestDistance = d;
                        best = (y * _width) + x;
                    }
                }
            }

            return best;
        }

        private List<int>? AStar(int start, int goal)
        {
            var goalX = goal % _width;
            var goalY = goal / _width;
            var cost = new float[_width * _height];
            Array.Fill(cost, float.MaxValue);
            var cameFrom = new int[_width * _height];
            Array.Fill(cameFrom, -1);
            var open = new PriorityQueue<int, float>();
            cost[start] = 0;
            open.Enqueue(start, 0);

            const float Diagonal = 1.41421356f;
            while (open.TryDequeue(out var current, out _))
            {
                if (current == goal)
                {
                    var path = new List<int> { current };
                    while (cameFrom[current] >= 0)
                    {
                        current = cameFrom[current];
                        path.Add(current);
                    }

                    path.Reverse();
                    return path;
                }

                var cx = current % _width;
                var cy = current / _width;
                for (var dy = -1; dy <= 1; dy++)
                {
                    for (var dx = -1; dx <= 1; dx++)
                    {
                        if (dx == 0 && dy == 0)
                        {
                            continue;
                        }

                        var nx = cx + dx;
                        var ny = cy + dy;
                        if (!InBounds(nx, ny))
                        {
                            continue;
                        }

                        var next = (ny * _width) + nx;
                        if (_blocked[next])
                        {
                            continue;
                        }

                        // Do not squeeze diagonally between two blocked cells.
                        if (dx != 0 && dy != 0 && (_blocked[(cy * _width) + nx] || _blocked[(ny * _width) + cx]))
                        {
                            continue;
                        }

                        var newCost = cost[current] + (dx != 0 && dy != 0 ? Diagonal : 1f);
                        if (newCost < cost[next])
                        {
                            cost[next] = newCost;
                            cameFrom[next] = current;
                            var ex = Math.Abs(nx - goalX);
                            var ey = Math.Abs(ny - goalY);
                            var heuristic = Math.Max(ex, ey) + ((Diagonal - 1f) * Math.Min(ex, ey));
                            open.Enqueue(next, newCost + heuristic);
                        }
                    }
                }
            }

            return null;
        }

        /// <summary>
        ///     Straightens a grid route: from each point, jump to the farthest point that can be walked to in a straight line.
        /// </summary>
        private List<Vector2> Smooth(List<Vector2> points)
        {
            if (points.Count <= 2)
            {
                return points;
            }

            var result = new List<Vector2> { points[0] };
            var anchor = 0;
            while (anchor < points.Count - 1)
            {
                var next = anchor + 1;
                for (var candidate = points.Count - 1; candidate > anchor + 1; candidate--)
                {
                    if (HasClearLine(points[anchor], points[candidate]))
                    {
                        next = candidate;
                        break;
                    }
                }

                result.Add(points[next]);
                anchor = next;
            }

            return result;
        }
    }
}
