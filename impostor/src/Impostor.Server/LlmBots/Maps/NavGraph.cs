using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace Impostor.Server.LlmBots.Maps
{
    internal sealed class NavNode
    {
        public NavNode(int index, string id, string room, Vector2 position)
        {
            Index = index;
            Id = id;
            Room = room;
            Position = position;
        }

        public int Index { get; }

        public string Id { get; }

        public string Room { get; }

        public Vector2 Position { get; }

        public List<int> Edges { get; } = new();
    }

    /// <summary>
    ///     A coarse walkable graph: bots walk from waypoint to waypoint in straight lines.
    /// </summary>
    internal sealed class NavGraph
    {
        private readonly Dictionary<string, NavNode> _byId = new();

        public List<NavNode> Nodes { get; } = new();

        /// <summary>
        ///     Gets or sets the wall data of the ship. When present, routes are found on it instead of on the waypoints.
        /// </summary>
        public CollisionGrid? Collision { get; set; }

        public NavNode Add(string id, string room, Vector2 position)
        {
            var node = new NavNode(Nodes.Count, id, room, position);
            Nodes.Add(node);
            _byId[id] = node;
            return node;
        }

        public NavNode this[string id] => _byId[id];

        public bool TryGet(string id, out NavNode node) => _byId.TryGetValue(id, out node!);

        public void Link(string a, string b) => Link(this[a], this[b]);

        public void Link(NavNode a, NavNode b)
        {
            if (a == b || a.Edges.Contains(b.Index))
            {
                return;
            }

            a.Edges.Add(b.Index);
            b.Edges.Add(a.Index);
        }

        public NavNode Nearest(Vector2 position)
        {
            NavNode? best = null;
            var bestDistance = float.MaxValue;
            foreach (var node in Nodes)
            {
                var d = Vector2.DistanceSquared(node.Position, position);
                if (d < bestDistance)
                {
                    bestDistance = d;
                    best = node;
                }
            }

            return best!;
        }

        /// <summary>
        ///     Finds a walkable route. The result starts at <paramref name="from"/> and ends at <paramref name="to"/>.
        /// </summary>
        public List<Vector2> FindPath(Vector2 from, Vector2 to)
        {
            var walled = Collision?.FindPath(from, to);
            if (walled != null)
            {
                return walled;
            }

            var start = Nearest(from);
            var goal = Nearest(to);
            var route = new List<Vector2> { from };

            if (start != goal)
            {
                var nodes = AStar(start, goal);
                if (nodes != null)
                {
                    route.AddRange(nodes.Select(n => n.Position));
                }
            }
            else
            {
                route.Add(start.Position);
            }

            route.Add(to);
            return Simplify(route);
        }

        public double PathLength(Vector2 from, Vector2 to)
        {
            var path = FindPath(from, to);
            double total = 0;
            for (var i = 1; i < path.Count; i++)
            {
                total += Vector2.Distance(path[i - 1], path[i]);
            }

            return total;
        }

        /// <summary>
        ///     Checks that every node can reach every other node.
        /// </summary>
        public bool IsConnected()
        {
            if (Nodes.Count == 0)
            {
                return true;
            }

            var seen = new HashSet<int> { 0 };
            var queue = new Queue<int>();
            queue.Enqueue(0);
            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                foreach (var next in Nodes[current].Edges)
                {
                    if (seen.Add(next))
                    {
                        queue.Enqueue(next);
                    }
                }
            }

            return seen.Count == Nodes.Count;
        }

        /// <summary>
        ///     Connects separate islands by linking the closest pair of nodes between them.
        /// </summary>
        public void ConnectIslands()
        {
            while (true)
            {
                var seen = new HashSet<int> { 0 };
                var queue = new Queue<int>();
                queue.Enqueue(0);
                while (queue.Count > 0)
                {
                    var current = queue.Dequeue();
                    foreach (var next in Nodes[current].Edges)
                    {
                        if (seen.Add(next))
                        {
                            queue.Enqueue(next);
                        }
                    }
                }

                if (seen.Count == Nodes.Count)
                {
                    return;
                }

                NavNode? bestA = null;
                NavNode? bestB = null;
                var best = float.MaxValue;
                foreach (var a in Nodes.Where(n => seen.Contains(n.Index)))
                {
                    foreach (var b in Nodes.Where(n => !seen.Contains(n.Index)))
                    {
                        var d = Vector2.DistanceSquared(a.Position, b.Position);
                        if (d < best)
                        {
                            best = d;
                            bestA = a;
                            bestB = b;
                        }
                    }
                }

                Link(bestA!, bestB!);
            }
        }

        private static List<Vector2> Simplify(List<Vector2> route)
        {
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

        private List<NavNode>? AStar(NavNode start, NavNode goal)
        {
            var open = new PriorityQueue<int, float>();
            var cameFrom = new Dictionary<int, int>();
            var cost = new Dictionary<int, float> { [start.Index] = 0 };
            open.Enqueue(start.Index, 0);

            while (open.Count > 0)
            {
                var current = open.Dequeue();
                if (current == goal.Index)
                {
                    var path = new List<NavNode> { Nodes[current] };
                    while (cameFrom.TryGetValue(current, out var previous))
                    {
                        current = previous;
                        path.Add(Nodes[current]);
                    }

                    path.Reverse();
                    return path;
                }

                foreach (var next in Nodes[current].Edges)
                {
                    var newCost = cost[current] + Vector2.Distance(Nodes[current].Position, Nodes[next].Position);
                    if (!cost.TryGetValue(next, out var known) || newCost < known)
                    {
                        cost[next] = newCost;
                        cameFrom[next] = current;
                        open.Enqueue(next, newCost + Vector2.Distance(Nodes[next].Position, goal.Position));
                    }
                }
            }

            return null;
        }
    }
}
