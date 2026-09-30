using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Impostor.Api.Innersloth;

namespace Impostor.Server.LlmBots.Maps
{
    internal sealed record TaskConsole(int Id, string Room, Vector2 Position);

    internal sealed record TaskSpec(int Id, string TaskType, string Length, IReadOnlyList<TaskConsole> Consoles);

    internal sealed record VentSpec(int Id, string Name, Vector2 Position, int? Left, int? Center, int? Right);

    internal sealed record DoorSpec(int Id, string Room, Vector2 Position);

    /// <summary>
    ///     Everything a bot knows about a map: where things are and how to walk between them.
    /// </summary>
    internal sealed class BotMap
    {
        private static readonly ConcurrentDictionary<MapTypes, BotMap> Cache = new();
        private static readonly Regex CamelCase = new("(?<=[a-z])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])", RegexOptions.Compiled);

        private static readonly Dictionary<string, string> RoomNames = new()
        {
            ["LifeSupp"] = "O2",
            ["Nav"] = "Navigation",
            ["Comms"] = "Communications",
            ["MedBay"] = "MedBay",
            ["Hallway"] = "Hallway",
        };

        private readonly Dictionary<string, Vector2> _hubs = new();

        private BotMap(MapTypes type)
        {
            Type = type;
            Nav = new NavGraph();
            Tasks = new Dictionary<int, TaskSpec>();
            Vents = Array.Empty<VentSpec>();
            Doors = Array.Empty<DoorSpec>();
        }

        public MapTypes Type { get; }

        public string Name => Type switch
        {
            MapTypes.Skeld => "The Skeld",
            MapTypes.MiraHQ => "MIRA HQ",
            MapTypes.Polus => "Polus",
            MapTypes.Dleks => "Dleks (a mirrored Skeld)",
            MapTypes.Airship => "The Airship",
            MapTypes.Fungle => "The Fungle",
            _ => Type.ToString(),
        };

        public NavGraph Nav { get; private set; }

        public IReadOnlyDictionary<int, TaskSpec> Tasks { get; private set; }

        public IReadOnlyList<VentSpec> Vents { get; private set; }

        public IReadOnlyList<DoorSpec> Doors { get; private set; }

        public Vector2 SpawnCenter { get; private set; }

        public Vector2 MeetingCenter { get; private set; }

        public float SpawnRadius { get; private set; }

        public Vector2 EmergencyButton { get; private set; }

        public bool HasHandmadeGraph { get; private set; }

        public IEnumerable<string> Rooms => Nav.Nodes.Select(n => n.Room).Where(r => !IsHallway(r)).Distinct();

        public static BotMap Get(MapTypes type) => Cache.GetOrAdd(type, Load);

        public static string DisplayName(string room)
        {
            if (RoomNames.TryGetValue(room, out var name))
            {
                return name;
            }

            return CamelCase.Replace(room, " ");
        }

        public static bool IsHallway(string room) => room == "Hallway" || room.EndsWith("Hall", StringComparison.Ordinal);

        /// <summary>
        ///     Gets the name of the room a position is in (the room of the closest waypoint).
        /// </summary>
        public string RoomAt(Vector2 position) => DisplayName(Nav.Nearest(position).Room);

        public string RawRoomAt(Vector2 position) => Nav.Nearest(position).Room;

        public Vector2 RoomHub(string room)
        {
            if (_hubs.TryGetValue(room, out var hub))
            {
                return hub;
            }

            var nodes = Nav.Nodes.Where(n => n.Room == room).ToList();
            if (nodes.Count == 0)
            {
                return SpawnCenter;
            }

            var centroid = new Vector2(nodes.Average(n => n.Position.X), nodes.Average(n => n.Position.Y));
            return nodes.OrderBy(n => Vector2.DistanceSquared(n.Position, centroid)).First().Position;
        }

        public bool HasRoom(string room) => Nav.Nodes.Any(n => n.Room == room);

        /// <summary>
        ///     Gets the rooms a walker can step into directly from <paramref name="room"/>.
        /// </summary>
        public IReadOnlyList<string> Neighbors(string room)
        {
            var result = new HashSet<string>();
            foreach (var node in Nav.Nodes.Where(n => n.Room == room))
            {
                foreach (var next in node.Edges)
                {
                    var other = Nav.Nodes[next].Room;
                    if (other != room)
                    {
                        result.Add(other);
                    }
                }
            }

            return result.OrderBy(r => r, StringComparer.Ordinal).ToList();
        }

        private static BotMap Load(MapTypes type)
        {
            var map = new BotMap(type);
            var folder = type switch
            {
                MapTypes.Skeld => "Skeld",
                MapTypes.MiraHQ => "Mira",
                MapTypes.Polus => "Polus",
                MapTypes.Dleks => "April",
                MapTypes.Airship => "Airship",
                MapTypes.Fungle => "Fungle",
                _ => "Skeld",
            };

            var tasks = new Dictionary<int, TaskSpec>();
            using (var doc = ReadJson(folder, "tasks"))
            {
                foreach (var entry in doc.RootElement.EnumerateObject())
                {
                    var id = int.Parse(entry.Name);
                    var consoles = new List<TaskConsole>();
                    foreach (var c in entry.Value.GetProperty("consoles").EnumerateArray())
                    {
                        consoles.Add(new TaskConsole(c.GetProperty("id").GetInt32(), c.GetProperty("room").GetString()!, ReadVector(c.GetProperty("position"))));
                    }

                    var taskType = entry.Value.TryGetProperty("taskType", out var tt) ? tt.GetString() ?? "None" : "None";
                    var length = entry.Value.TryGetProperty("length", out var len) ? len.GetString() ?? "Short" : "Short";
                    tasks[id] = new TaskSpec(id, taskType, length, consoles);
                }
            }

            map.Tasks = tasks;

            var vents = new List<VentSpec>();
            using (var doc = ReadJson(folder, "vents"))
            {
                foreach (var entry in doc.RootElement.EnumerateObject())
                {
                    vents.Add(new VentSpec(
                        int.Parse(entry.Name),
                        entry.Value.GetProperty("name").GetString() ?? entry.Name,
                        ReadVector(entry.Value.GetProperty("position")),
                        ReadLink(entry.Value, "left"),
                        ReadLink(entry.Value, "center"),
                        ReadLink(entry.Value, "right")));
                }
            }

            map.Vents = vents;

            var doors = new List<DoorSpec>();
            using (var doc = ReadJson(folder, "doors"))
            {
                foreach (var entry in doc.RootElement.EnumerateObject())
                {
                    doors.Add(new DoorSpec(int.Parse(entry.Name), entry.Value.GetProperty("room").GetString() ?? "Hallway", ReadVector(entry.Value.GetProperty("position"))));
                }
            }

            map.Doors = doors;

            using (var doc = ReadJson(folder, "spawn"))
            {
                map.SpawnCenter = ReadVector(doc.RootElement.GetProperty("initialSpawnCenter"));
                map.MeetingCenter = ReadVector(doc.RootElement.GetProperty("meetingSpawnCenter"));
                map.SpawnRadius = doc.RootElement.GetProperty("spawnRadius").GetSingle();
            }

            if (type is MapTypes.Skeld or MapTypes.Dleks)
            {
                map.Nav = SkeldGraph.Build(type == MapTypes.Dleks, out var hubs);
                map.HasHandmadeGraph = true;
                foreach (var (room, node) in hubs)
                {
                    map._hubs[room] = map.Nav[node].Position;
                }

                map.EmergencyButton = map.Nav["CAF_BUTTON"].Position;
                AttachVents(map);
            }
            else
            {
                map.Nav = AutoGraph(map);
                map.EmergencyButton = map.Nav.Nearest(map.MeetingCenter).Position;
            }

            // Wall data is only used where it has been checked against the map's consoles, doors and vents: the Skeld
            // and Dleks. The other ships have several floors or a different origin and still need work.
            if (type is MapTypes.Skeld or MapTypes.Dleks)
            {
                map.Nav.Collision = CollisionGrid.TryLoad(
                    type == MapTypes.Dleks ? "april" : "skeld",
                    map.Tasks.Values.SelectMany(t => t.Consoles.Select(c => c.Position))
                        .Concat(map.Doors.Select(d => d.Position))
                        .Concat(map.Vents.Select(v => v.Position))
                        .Append(map.SpawnCenter)
                        .Append(map.MeetingCenter)
                        .ToList());
            }

            if (type is MapTypes.Skeld or MapTypes.Dleks)
            {
                Console.WriteLine(map.Nav.Collision != null
                    ? $"[LlmBots] {map.Name}: bots avoid walls (wall data loaded)"
                    : $"[LlmBots] {map.Name}: no wall data (run tools/extract_collision.py), bots use plain waypoints");
            }

            return map;
        }

        private static void AttachVents(BotMap map)
        {
            // Every vent must be reachable on foot: link it to the closest waypoint.
            foreach (var vent in map.Vents)
            {
                var near = map.Nav.Nearest(vent.Position);
                if (Vector2.Distance(near.Position, vent.Position) > 0.05f)
                {
                    var node = map.Nav.Add("VENT_" + vent.Id, near.Room, vent.Position);
                    map.Nav.Link(node, near);
                }
            }
        }

        private static NavGraph AutoGraph(BotMap map)
        {
            var graph = new NavGraph();

            foreach (var task in map.Tasks.Values)
            {
                foreach (var console in task.Consoles)
                {
                    var id = $"T{task.Id}C{console.Id}";
                    if (!graph.TryGet(id, out _))
                    {
                        graph.Add(id, console.Room, console.Position);
                    }
                }
            }

            foreach (var door in map.Doors)
            {
                graph.Add("D" + door.Id, door.Room, door.Position);
            }

            var labeled = graph.Nodes.ToList();
            foreach (var vent in map.Vents)
            {
                var room = labeled.Count == 0 ? "Hallway" : labeled.OrderBy(n => Vector2.DistanceSquared(n.Position, vent.Position)).First().Room;
                graph.Add("V" + vent.Id, room, vent.Position);
            }

            graph.Add("SPAWN", "Cafeteria", map.SpawnCenter);
            if (Vector2.Distance(map.SpawnCenter, map.MeetingCenter) > 1)
            {
                graph.Add("MEET", "MeetingRoom", map.MeetingCenter);
            }

            // Link every waypoint to its three closest neighbours, then join whatever is left over.
            foreach (var node in graph.Nodes)
            {
                foreach (var other in graph.Nodes.Where(o => o != node).OrderBy(o => Vector2.DistanceSquared(o.Position, node.Position)).Take(3))
                {
                    if (Vector2.Distance(other.Position, node.Position) <= 14)
                    {
                        graph.Link(node, other);
                    }
                }
            }

            graph.ConnectIslands();
            return graph;
        }

        private static JsonDocument ReadJson(string folder, string name)
        {
            var assembly = typeof(BotMap).Assembly;
            var resource = assembly.GetManifestResourceNames().Single(n => n.EndsWith($".MapData.{folder}.{name}.json", StringComparison.Ordinal));
            using var stream = assembly.GetManifestResourceStream(resource)!;
            return JsonDocument.Parse(stream);
        }

        private static Vector2 ReadVector(JsonElement element) => new(element.GetProperty("x").GetSingle(), element.GetProperty("y").GetSingle());

        private static int? ReadLink(JsonElement element, string name)
        {
            return element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetInt32() : null;
        }
    }
}
