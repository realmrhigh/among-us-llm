using System;
using System.Collections.Generic;
using Impostor.Server.LlmBots;
using Impostor.Server.LlmBots.Brain;

internal static class ProbeScenarios
{
    public static MeetingContext Crew()
    {
        var players = new List<PlayerBrief>
        {
            new(0, "Ada", "Red", true, true, false),
            new(1, "Bolt", "Blue", true, false, false),
            new(2, "Cleo", "Green", true, false, false),
            new(3, "Dax", "Pink", false, false, false),
            new(4, "Echo", "Orange", true, false, false),
        };
        return new MeetingContext
        {
            MapName = "The Skeld",
            MeetingNumber = 1,
            Stage = MeetingStage.Opening,
            Me = players[0],
            Players = players,
            Persona = "calm and analytical, thinks out loud about who was where",
            Reason = "Cleo found Dax's body in Admin.",
            BodyRoom = "Admin",
            Reporter = players[2],
            Body = players[3],
            MyTasks = new[] { "You completed 2 of 4 tasks.", "Fix Wiring in Cafeteria (done)", "Clear Asteroids in Weapons (done)", "Upload Data in Admin" },
            MyRoute = new[] { "Cafeteria (50s ago)", "Weapons (35s ago)", "East Hall (15s ago)", "Cafeteria (5s ago)" },
            Observations = new[] { "12s ago: Bolt was near where the body was found (20s ago)" },
            Sightings = new[] { "Bolt in Electrical (45s ago)", "Echo in Weapons (30s ago)", "Bolt in Admin (20s ago)" },
            Suspicion = new Dictionary<byte, double> { [1] = 3.0 },
            SecondsLeft = 50,
            MaxLines = 2,
        };
    }

    public static MeetingContext Impostor()
    {
        var players = new List<PlayerBrief>
        {
            new(0, "Ada", "Red", true, true, false),
            new(1, "Bolt", "Blue", true, false, false),
            new(2, "Cleo", "Green", true, false, false),
            new(3, "Dax", "Pink", false, false, false),
            new(4, "Echo", "Orange", true, false, true),
        };
        return new MeetingContext
        {
            MapName = "The Skeld",
            MeetingNumber = 1,
            Stage = MeetingStage.Reply,
            Me = players[0],
            IAmImpostor = true,
            Players = players,
            Persona = "confident and bossy, tells everybody who to vote for",
            Reason = "Cleo found Dax's body in Admin.",
            BodyRoom = "Admin",
            Reporter = players[2],
            Body = players[3],
            SecretFacts = new[] { "Your impostor teammates: Echo.", "You killed Dax in Admin (26s ago)." },
            MyTasks = new[] { "Fix Wiring in Cafeteria", "Clear Asteroids in Weapons", "Upload Data in Admin" },
            MyRoute = new[] { "Weapons (60s ago)", "East Hall (40s ago)", "Admin (28s ago)", "Storage (12s ago)" },
            Observations = new[] { "5s ago: I found the body of Dax in Admin" },
            Sightings = new[] { "Bolt in Cafeteria (55s ago)" },
            Chat = new[]
            {
                new ChatLine(DateTime.UtcNow, 2, "Cleo", "found Dax dead in Admin, who was near?"),
                new ChatLine(DateTime.UtcNow, 1, "Bolt", "Ada where were you? you came from Admin right?"),
                new ChatLine(DateTime.UtcNow, 4, "Echo", "i was in Weapons the whole time"),
            },
            SecondsLeft = 40,
            MaxLines = 2,
        };
    }
}
