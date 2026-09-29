using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace Impostor.Server.LlmBots.Maps
{
    /// <summary>
    ///     One place a bot has to stand for a while to do (a part of) a task.
    /// </summary>
    internal sealed record TaskStop(Vector2 Position, string Room, string Label, double Seconds);

    /// <summary>
    ///     Turns a task from the map data into the walk a crewmate has to make to finish it.
    /// </summary>
    internal static class TaskPlanner
    {
        private static readonly Dictionary<string, double> Seconds = new()
        {
            ["SwipeCard"] = 4,
            ["FixWiring"] = 4,
            ["ClearAsteroids"] = 14,
            ["AlignEngineOutput"] = 7,
            ["SubmitScan"] = 11,
            ["InspectSample"] = 8,
            ["FuelEngines"] = 5,
            ["StartReactor"] = 8,
            ["UploadData"] = 8,
            ["CalibrateDistributor"] = 7,
            ["ChartCourse"] = 6,
            ["CleanO2Filter"] = 6,
            ["UnlockManifolds"] = 7,
            ["StabilizeSteering"] = 4,
            ["PrimeShields"] = 4,
            ["DivertPower"] = 5,
            ["VentCleaning"] = 5,
            ["EmptyGarbage"] = 5,
            ["EmptyChute"] = 5,
        };

        private static readonly string[] DownloadRooms = { "Cafeteria", "Electrical", "Weapons", "Nav", "Comms", "Security", "MedBay" };

        private static readonly string[] PowerTargets = { "Weapons", "LifeSupp", "Nav", "Shields", "UpperEngine", "LowerEngine", "Security", "Comms" };

        public static string Name(string taskType) => BotMap.DisplayName(taskType);

        public static List<TaskStop> Plan(BotMap map, TaskSpec spec, Random rng)
        {
            var stops = new List<TaskStop>();
            var name = Name(spec.TaskType);

            TaskStop AtConsole(TaskConsole c, string label, double seconds) => new(c.Position, c.Room, label, seconds);

            TaskStop AtRoom(string room, string label, double seconds) => new(map.RoomHub(room), room, label, seconds);

            var seconds = Seconds.GetValueOrDefault(spec.TaskType, 6);

            switch (spec.TaskType)
            {
                case "FixWiring" when spec.Consoles.Count >= 3:
                {
                    var chosen = spec.Consoles.OrderBy(_ => rng.Next()).Take(3).ToList();
                    var ordered = new List<TaskConsole> { chosen[0] };
                    chosen.RemoveAt(0);
                    while (chosen.Count > 0)
                    {
                        var next = chosen.OrderBy(c => Vector2.DistanceSquared(c.Position, ordered[^1].Position)).First();
                        chosen.Remove(next);
                        ordered.Add(next);
                    }

                    stops.AddRange(ordered.Select(c => AtConsole(c, name, seconds)));
                    break;
                }

                case "UploadData":
                {
                    var rooms = DownloadRooms.Where(map.HasRoom).ToList();
                    if (rooms.Count > 0)
                    {
                        stops.Add(AtRoom(rooms[rng.Next(rooms.Count)], "Download Data", seconds));
                    }

                    stops.AddRange(spec.Consoles.Take(1).Select(c => AtConsole(c, "Upload Data", seconds)));
                    break;
                }

                case "DivertPower":
                {
                    stops.AddRange(spec.Consoles.Take(1).Select(c => AtConsole(c, "Divert Power (source)", seconds)));
                    var targets = PowerTargets.Where(map.HasRoom).ToList();
                    if (targets.Count > 0)
                    {
                        stops.Add(AtRoom(targets[rng.Next(targets.Count)], "Divert Power (accept)", seconds));
                    }

                    break;
                }

                case "FuelEngines" when spec.Consoles.Count == 0:
                {
                    var engines = new[] { "UpperEngine", "LowerEngine", "Engine" }.Where(map.HasRoom).ToList();
                    var storage = map.HasRoom("Storage") ? "Storage" : (map.Rooms.FirstOrDefault() ?? "Cafeteria");
                    foreach (var engine in engines.Take(2))
                    {
                        stops.Add(AtRoom(storage, "Fuel Engines (grab fuel)", seconds));
                        stops.Add(AtRoom(engine, "Fuel Engines (fill engine)", seconds));
                    }

                    if (stops.Count == 0)
                    {
                        stops.Add(AtRoom(storage, name, seconds));
                    }

                    break;
                }

                default:
                {
                    if (spec.Consoles.Count > 0)
                    {
                        // Several consoles of one task are alternatives, the game picks one of them.
                        var console = spec.Consoles[rng.Next(spec.Consoles.Count)];
                        stops.Add(AtConsole(console, name, seconds));
                    }
                    else
                    {
                        var rooms = PreferredRooms(map, spec.TaskType).ToList();
                        var room = rooms.Count > 0 ? rooms[rng.Next(rooms.Count)] : map.Rooms.OrderBy(_ => rng.Next()).First();
                        stops.Add(AtRoom(room, name, seconds));
                    }

                    break;
                }
            }

            if (stops.Count == 0)
            {
                stops.Add(new TaskStop(map.SpawnCenter, "Cafeteria", name, seconds));
            }

            return stops;
        }

        private static IEnumerable<string> PreferredRooms(BotMap map, string taskType)
        {
            var wanted = taskType switch
            {
                "ChartCourse" => new[] { "Nav", "Cockpit" },
                "EmptyGarbage" => new[] { "Cafeteria", "Storage", "LifeSupp", "Kitchen", "CargoBay" },
                "EmptyChute" => new[] { "LifeSupp", "Storage", "Cafeteria" },
                "WaterPlants" => new[] { "Greenhouse", "Storage" },
                "ReplaceWaterJug" => new[] { "Office", "BoilerRoom", "Laboratory" },
                "StartFans" => new[] { "Engine", "Electrical" },
                _ => Array.Empty<string>(),
            };

            return wanted.Where(map.HasRoom);
        }
    }
}
