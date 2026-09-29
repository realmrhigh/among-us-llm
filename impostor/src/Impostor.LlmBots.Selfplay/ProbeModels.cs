using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Impostor.Server.LlmBots;
using Impostor.Server.LlmBots.Brain;
using Impostor.Server.LlmBots.Llm;
using Microsoft.Extensions.Logging;

// Sends the same two realistic meeting prompts to several free OpenRouter models and reports which of them answer
// fast, keep to the JSON format and stay in character. Uses a handful of requests (2 per model per round).
internal static class ProbeModels
{
    public static async Task<int> RunAsync(string[] requestedModels, int top, int rounds, bool useMock = false)
    {
        using var mock = useMock ? new Impostor.LlmBots.Sim.MockOpenRouter() : null;
        var key = useMock ? "mock-key" : ApiKey.Find("OPENROUTER_API_KEY");
        if (key == null)
        {
            Console.WriteLine("No OPENROUTER_API_KEY found (checked the environment and .env files in the current folder and its parents).");
            Console.WriteLine($"Current folder: {Directory.GetCurrentDirectory()}");
            return 2;
        }

        Console.WriteLine("OpenRouter key found (not shown).");

        using var factory = LoggerFactory.Create(b => b.AddSimpleConsole().SetMinimumLevel(LogLevel.Warning));
        var config = new LlmBotsConfig { MaxRequestsPerMinute = useMock ? 600 : 15, RequestTimeoutSeconds = 60 };
        if (mock != null)
        {
            config.OpenRouterBaseUrl = mock.BaseUrl;
        }

        using var client = new OpenRouterClient(config, factory.CreateLogger("OpenRouter"), key);

        Console.WriteLine("Key: " + await client.GetKeyInfoAsync(CancellationToken.None));

        var models = new List<string>(requestedModels);
        if (models.Count == 0)
        {
            var all = await client.GetModelsAsync(CancellationToken.None);
            models.AddRange(all.Take(top));
        }

        Console.WriteLine($"Testing {models.Count} model(s), {rounds} round(s), {models.Count * rounds * 2} requests in total.\n");

        var scenarios = new[] { ("crew opening", ProbeScenarios.Crew()), ("impostor reply", ProbeScenarios.Impostor()) };
        var report = new StringBuilder();
        var rows = new List<Row>();

        foreach (var model in models)
        {
            foreach (var (name, context) in scenarios)
            {
                for (var round = 1; round <= rounds; round++)
                {
                    var row = await ProbeAsync(client, model, name, context);
                    rows.Add(row);
                    Console.WriteLine(row.OneLine());
                    report.AppendLine(row.Detail());
                }
            }
        }

        Console.WriteLine("\n=========== SUMMARY (best first) ===========");
        foreach (var group in rows.GroupBy(r => r.Model).OrderByDescending(g => g.Count(r => r.Valid)).ThenBy(g => g.Where(r => r.Valid).Select(r => r.Ms).DefaultIfEmpty(long.MaxValue).Average()))
        {
            var ok = group.Where(r => r.Valid).ToList();
            var avg = ok.Count == 0 ? "-" : $"{ok.Average(r => r.Ms) / 1000.0:0.0}s";
            Console.WriteLine($"{ok.Count}/{group.Count()} valid, avg {avg,-6} {group.Key}");
        }

        Directory.CreateDirectory("llmbots-logs");
        var file = Path.Combine("llmbots-logs", $"probe-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
        await File.WriteAllTextAsync(file, report.ToString());
        Console.WriteLine($"\nFull answers: {file}");
        Console.WriteLine($"Requests used: {client.Budget.Total}");
        return 0;
    }

    private static async Task<Row> ProbeAsync(OpenRouterClient client, string model, string scenario, MeetingContext context)
    {
        var user = PromptBuilder.User(context);
        var watch = Stopwatch.StartNew();
        try
        {
            var reply = await client.CompleteWithModelAsync(model, PromptBuilder.SystemPrompt, user, TimeSpan.FromSeconds(90), CancellationToken.None);
            var decision = DecisionParser.Parse(reply.Text, context, "probe");
            var json = DecisionParser.ExtractJson(reply.Text);
            var rawLines = CountSayLines(json);
            var usable = DecisionParser.LooksUsable(reply.Text);
            var valid = decision != null && usable && decision.Say.Count > 0;
            var note = new List<string>();
            if (!usable)
            {
                note.Add("no JSON");
            }
            else if (json == null)
            {
                note.Add("JSON was broken, repaired");
            }

            if (decision != null && decision.Vote == null)
            {
                note.Add("vote missing/invalid");
            }

            if (rawLines > (decision?.Say.Count ?? 0))
            {
                note.Add($"{rawLines - (decision?.Say.Count ?? 0)} line(s) dropped");
            }

            return new Row(model, scenario, valid, reply.LatencyMs, decision?.Say ?? new List<string>(), decision?.Vote, string.Join(", ", note), reply.Text, null);
        }
        catch (Exception ex)
        {
            return new Row(model, scenario, false, watch.ElapsedMilliseconds, new List<string>(), null, string.Empty, string.Empty, ex.Message);
        }
    }

    private static int CountSayLines(string? json)
    {
        if (json == null)
        {
            return 0;
        }

        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true });
            foreach (var p in doc.RootElement.EnumerateObject())
            {
                if (p.Name.Equals("say", StringComparison.OrdinalIgnoreCase))
                {
                    return p.Value.ValueKind == JsonValueKind.Array ? p.Value.GetArrayLength() : 1;
                }
            }
        }
        catch (JsonException)
        {
        }

        return 0;
    }

    private sealed record Row(string Model, string Scenario, bool Valid, long Ms, IReadOnlyList<string> Say, string? Vote, string Note, string Raw, string? Error)
    {
        public string OneLine()
        {
            if (Error != null)
            {
                return $"FAIL  {Model,-42} {Scenario,-15} {Ms / 1000.0,5:0.0}s  {Error}";
            }

            var say = Say.Count == 0 ? "(nothing)" : "\"" + string.Join("\" / \"", Say) + "\"";
            return $"{(Valid ? "ok   " : "WEAK ")} {Model,-42} {Scenario,-15} {Ms / 1000.0,5:0.0}s  vote={Vote ?? "?"}  {say}{(Note.Length > 0 ? "  [" + Note + "]" : string.Empty)}";
        }

        public string Detail()
        {
            var builder = new StringBuilder();
            builder.AppendLine($"=== {Model} / {Scenario} / {Ms} ms / {(Error != null ? "ERROR: " + Error : (Valid ? "valid" : "weak"))}");
            if (Error == null)
            {
                builder.AppendLine(Raw.Trim());
            }

            return builder.ToString();
        }
    }
}
