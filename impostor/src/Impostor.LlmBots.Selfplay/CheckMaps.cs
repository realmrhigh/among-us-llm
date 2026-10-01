using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Impostor.Api.Innersloth;
using Impostor.Server.LlmBots.Maps;

// Checks the wall data of every ship (collision-data/*.json, from tools/extract_collision.py) against the map's own
// consoles, doors, vents and spawn point, and tries routes between all task consoles. Run it after extracting:
//   ./selfplay.sh --checkmaps        (Windows: selfplay.cmd --checkmaps)
internal static class CheckMaps
{
    public static int Run()
    {
        var anyData = false;
        foreach (var type in Enum.GetValues<MapTypes>())
        {
            var map = BotMap.Get(type);
            Console.WriteLine($"\n=== {map.Name} ===");
            Console.WriteLine(map.CollisionStatus);

            var points = Labelled(map);
            var grid = CollisionGrid.TryLoad(BotMap.ShipFile(type), points.Select(p => p.Position).ToList());
            if (grid == null)
            {
                continue;
            }

            anyData = true;
            Console.WriteLine("  " + grid.Measure(points.Select(p => p.Position).ToList()));
            foreach (var (label, position) in points.Where(p => !grid.IsWalkable(p.Position)).Take(20))
            {
                Console.WriteLine($"  in a wall or outside the ship: {label} at ({position.X:0.0}, {position.Y:0.0})");
            }

            var consoles = map.Tasks.Values.SelectMany(t => t.Consoles).GroupBy(c => c.Position).Select(g => g.First()).ToList();
            var pairs = new List<(TaskConsole A, TaskConsole B)>();
            for (var i = 0; i < consoles.Count; i++)
            {
                for (var j = i + 1; j < consoles.Count; j++)
                {
                    pairs.Add((consoles[i], consoles[j]));
                }
            }

            var step = Math.Max(1, pairs.Count / 400);
            var tested = 0;
            var failed = new List<string>();
            var detours = new List<double>();
            for (var i = 0; i < pairs.Count; i += step)
            {
                var (a, b) = pairs[i];
                tested++;
                var route = grid.FindPath(a.Position, b.Position);
                if (route == null)
                {
                    failed.Add($"{a.Room} ({a.Position.X:0.0}, {a.Position.Y:0.0}) -> {b.Room} ({b.Position.X:0.0}, {b.Position.Y:0.0})");
                    continue;
                }

                var length = 0f;
                for (var k = 1; k < route.Count; k++)
                {
                    length += Vector2.Distance(route[k - 1], route[k]);
                }

                detours.Add(length / Math.Max(0.1f, Vector2.Distance(a.Position, b.Position)));
            }

            Console.WriteLine($"  routes between consoles: {tested - failed.Count}/{tested} found" + (detours.Count > 0 ? $", average detour x{detours.Average():0.00}, longest x{detours.Max():0.0}" : string.Empty));
            foreach (var line in failed.Take(10))
            {
                Console.WriteLine("  no route: " + line);
            }
        }

        if (!anyData)
        {
            Console.WriteLine("\nNo wall data found. Run tools/extract_collision.py against your Among Us install first (see README).");
            return 1;
        }

        return 0;
    }

    private static List<(string Label, Vector2 Position)> Labelled(BotMap map)
    {
        var list = new List<(string, Vector2)>();
        foreach (var task in map.Tasks.Values)
        {
            foreach (var console in task.Consoles)
            {
                list.Add(($"task {task.TaskType} console in {console.Room}", console.Position));
            }
        }

        list.AddRange(map.Doors.Select(d => ($"door {d.Room}", d.Position)));
        list.AddRange(map.Vents.Select(v => ($"vent {v.Name}", v.Position)));
        list.Add(("spawn", map.SpawnCenter));
        list.Add(("meeting spot", map.MeetingCenter));
        return list;
    }
}
