using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Impostor.Api.Innersloth;
using Impostor.LlmBots.Sim;
using Impostor.Server.LlmBots;
using Impostor.Server.LlmBots.Brain;
using Impostor.Server.LlmBots.Llm;
using Microsoft.Extensions.Logging;

// Watch a complete game of bots play each other without any Among Us client.
//
//   dotnet run --project src/Impostor.LlmBots.Selfplay -- --players 8 --games 2 --llm mock
//
// Options: --players N (3-10)  --games N  --speed X (1 = human pace)  --map skeld|mira|polus|dleks|airship|fungle
//          --llm off|mock|openrouter   --impostors N   --seed N   --verbose
var options = Args.Parse(args);
if (options.Help)
{
    Console.WriteLine("usage: Selfplay [--players 8] [--games 2] [--speed 4] [--map skeld] [--llm off|mock|openrouter] [--impostors 1] [--seed 1] [--verbose] [--web 22123] | --probe [--models a,b] [--top 6] [--rounds 1] | --checkmaps");
    return 0;
}

if (options.CheckMaps)
{
    return CheckMaps.Run();
}

if (options.Raw)
{
    return await RawProbe.RunAsync(options.ProbeModels, options.RawVariants);
}

if (options.Probe)
{
    return await ProbeModels.RunAsync(options.ProbeModels, options.ProbeTop, options.ProbeRounds, options.Llm == "mock");
}

if (options.WebPort > 0)
{
    return await RunWebAsync(options);
}

Console.WriteLine($"Selfplay: {options.Players} players, {options.Games} game(s), map {options.Map}, speed x{options.Speed}, brain {options.Llm}");

MockOpenRouter? mock = null;
OpenRouterClient? client = null;
Func<BotAgent, IBrain>? brains = null;

if (options.Llm == "mock")
{
    mock = new MockOpenRouter();
    client = new OpenRouterClient(new LlmBotsConfig { OpenRouterBaseUrl = mock.BaseUrl, MaxRequestsPerMinute = 600, RequestTimeoutSeconds = 5 }, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, "mock-key");
}
else if (options.Llm == "openrouter")
{
    var key = ApiKey.Find("OPENROUTER_API_KEY");
    if (key == null)
    {
        Console.WriteLine("No OPENROUTER_API_KEY found in the environment or a .env file. Use --llm mock or --llm off.");
        return 2;
    }

    using var factory = LoggerFactory.Create(b => b.AddSimpleConsole().SetMinimumLevel(LogLevel.Warning));
    client = new OpenRouterClient(new LlmBotsConfig { MaxRequestsPerMinute = 16, RequestTimeoutSeconds = 30 }, factory.CreateLogger("OpenRouter"), key);
    Console.WriteLine("Checking which free models answer right now...");
    Console.WriteLine("  " + (await client.CalibrateAsync(CancellationToken.None)).Summary);
}

var seedCounter = 0;
if (client != null)
{
    var c = client;
    brains = agent => new LlmBrain(c, new HeuristicBrain(options.Seed + (++seedCounter)), Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, options.Verbose ? Console.WriteLine : null);
}

await using var sim = await SimGame.CreateAsync(
    options.Players - 1,
    o =>
    {
        o.Map = options.Map;
        o.NumImpostors = options.Impostors;
        o.KillCooldown = 15f / options.Speed < 2 ? 2 : 15f / options.Speed;
        o.DiscussionTime = 15;
        o.VotingTime = 45;
    },
    c =>
    {
        c.TimeScale = options.Speed;
        c.MeetingAnimationSeconds = 9.9 / options.Speed;
        c.EndScreenSeconds = 6 / options.Speed;
        c.PostMeetingFreezeSeconds = 5 / options.Speed;
    },
    options.Seed,
    Console.WriteLine,
    options.Verbose ? LogLevel.Debug : LogLevel.Information,
    brains);

if (!await sim.WaitForLobbyAsync(TimeSpan.FromSeconds(30)))
{
    Console.WriteLine("Lobby did not fill up.");
    return 1;
}

var results = new System.Collections.Generic.List<string>();
for (var game = 1; game <= options.Games; game++)
{
    await Task.Delay(1000);
    Console.WriteLine($"\n=========== GAME {game} ===========");
    sim.Host.RequestStart();
    var finished = await sim.WaitUntilAsync(() => sim.Host.GamesFinished == game, TimeSpan.FromMinutes(20));
    if (!finished)
    {
        Console.WriteLine("The game did not finish in 20 minutes.");
        return 1;
    }

    results.Add($"game {game}: {sim.Host.LastResult}");
    await sim.WaitUntilAsync(() => sim.Game.GameState == GameStates.NotStarted && sim.Game.Players.All(p => p.Character?.PlayerInfo != null), TimeSpan.FromSeconds(30));
}

Console.WriteLine("\n=========== SUMMARY ===========");
results.ForEach(Console.WriteLine);
if (client != null)
{
    Console.WriteLine($"LLM calls: {client.Calls}, failures: {client.Failures}, last model: {client.LastModel ?? "-"}");
    foreach (var line in client.DescribeModels())
    {
        Console.WriteLine("  " + line);
    }
}

mock?.Dispose();
client?.Dispose();
return 0;

static async Task<int> RunWebAsync(Args options)
{
    // The real server (HTTP page included) with a pretend human host in the lobby who types "!bots N".
    var extra = new System.Collections.Generic.List<string>();
    MockOpenRouter? mock = null;
    if (options.Llm == "mock")
    {
        mock = new MockOpenRouter();
        Environment.SetEnvironmentVariable("OPENROUTER_API_KEY", "mock-key");
        extra.Add("--LlmBots:BrainMode=Auto");
        extra.Add($"--LlmBots:OpenRouterBaseUrl={mock.BaseUrl}");
        extra.Add("--LlmBots:MaxRequestsPerMinute=600");
    }
    else if (options.Llm == "openrouter")
    {
        extra.Add("--LlmBots:BrainMode=Auto");
    }

    await using var server = await RealServer.StartAsync(options.WebPort, extra.ToArray());
    server.Env.Config.TimeScale = options.Speed;
    server.Env.Config.MeetingAnimationSeconds = 9.9 / options.Speed;
    server.Env.Config.EndScreenSeconds = 6.0 / options.Speed;
    server.Env.Config.PostMeetingFreezeSeconds = 5.0 / options.Speed;

    Console.WriteLine($"\nOpen  http://localhost:{options.WebPort}/llmbots   (tick 'show roles' to see the impostors)");
    Console.WriteLine("Ctrl+C stops everything.\n");

    await using var sim = await SimGame.CreateAsync(
        0,
        o =>
        {
            o.Map = options.Map;
            o.NumImpostors = options.Impostors;
            o.KillCooldown = Math.Max(2f, 15f / options.Speed);
            o.DiscussionTime = 15;
            o.VotingTime = 45;
        },
        seed: options.Seed,
        log: Console.WriteLine,
        level: LogLevel.Information,
        existingServer: server);

    await Task.Delay(500);
    await sim.Host.Client.SendChatAsync($"!bots {options.Players - 1}");
    if (!await sim.WaitUntilAsync(() => sim.Host.Game?.PlayerCount == options.Players && sim.Host.Game.Players.All(p => p.Character?.PlayerInfo != null), TimeSpan.FromSeconds(60)))
    {
        Console.WriteLine("The bots did not all join.");
        return 1;
    }

    for (var game = 1; game <= options.Games; game++)
    {
        await Task.Delay(1500);
        Console.WriteLine($"\n=========== GAME {game} ===========");
        sim.Host.RequestStart();
        if (!await sim.WaitUntilAsync(() => sim.Host.GamesFinished == game, TimeSpan.FromMinutes(30)))
        {
            Console.WriteLine("The game did not finish in 30 minutes.");
            return 1;
        }

        Console.WriteLine($"game {game}: {sim.Host.LastResult}");
        await sim.WaitUntilAsync(() => sim.Game.GameState == GameStates.NotStarted && sim.Game.Players.All(p => p.Character?.PlayerInfo != null), TimeSpan.FromSeconds(30));
    }

    Console.WriteLine("\nDone. The page stays up, press Ctrl+C to quit.");
    var quit = new TaskCompletionSource();
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        quit.TrySetResult();
    };
    await quit.Task;
    mock?.Dispose();
    return 0;
}

internal sealed class Args
{
    public int Players { get; private set; } = 7;

    public int Games { get; private set; } = 1;

    public int Speed { get; private set; } = 4;

    public int Impostors { get; private set; } = 1;

    public int Seed { get; private set; } = 1;

    public MapTypes Map { get; private set; } = MapTypes.Skeld;

    public string Llm { get; private set; } = "off";

    public bool Verbose { get; private set; }

    public bool Help { get; private set; }

    public int WebPort { get; private set; }

    public bool Probe { get; private set; }

    public bool Raw { get; private set; }

    public string[] RawVariants { get; private set; } = new[] { "base" };

    public bool CheckMaps { get; private set; }

    public string[] ProbeModels { get; private set; } = System.Array.Empty<string>();

    public int ProbeTop { get; private set; } = 6;

    public int ProbeRounds { get; private set; } = 1;

    public static Args Parse(string[] args)
    {
        var a = new Args();
        for (var i = 0; i < args.Length; i++)
        {
            string Next() => i + 1 < args.Length ? args[++i] : string.Empty;
            switch (args[i])
            {
                case "--players": a.Players = Math.Clamp(int.Parse(Next()), 3, 10); break;
                case "--games": a.Games = Math.Max(1, int.Parse(Next())); break;
                case "--speed": a.Speed = Math.Clamp(int.Parse(Next()), 1, 20); break;
                case "--impostors": a.Impostors = Math.Clamp(int.Parse(Next()), 1, 3); break;
                case "--seed": a.Seed = int.Parse(Next()); break;
                case "--llm": a.Llm = Next().ToLowerInvariant(); break;
                case "--verbose": a.Verbose = true; break;
                case "--help": a.Help = true; break;
                case "--probe": a.Probe = true; break;
                case "--checkmaps": a.CheckMaps = true; break;
                case "--raw": a.Raw = true; break;
                case "--variants": a.RawVariants = Next().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries); break;
                case "--models": a.ProbeModels = Next().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries); break;
                case "--top": a.ProbeTop = Math.Clamp(int.Parse(Next()), 1, 20); break;
                case "--rounds": a.ProbeRounds = Math.Clamp(int.Parse(Next()), 1, 3); break;
                case "--web": a.WebPort = i + 1 < args.Length && int.TryParse(args[i + 1], out var wp) ? wp : 22123; if (i + 1 < args.Length && int.TryParse(args[i + 1], out _)) { i++; } break;
                case "--map":
                    a.Map = Next().ToLowerInvariant() switch
                    {
                        "mira" => MapTypes.MiraHQ,
                        "polus" => MapTypes.Polus,
                        "dleks" => MapTypes.Dleks,
                        "airship" => MapTypes.Airship,
                        "fungle" => MapTypes.Fungle,
                        _ => MapTypes.Skeld,
                    };
                    break;
            }
        }

        return a;
    }
}
