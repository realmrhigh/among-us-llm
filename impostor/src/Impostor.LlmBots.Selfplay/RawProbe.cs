using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Impostor.Server.LlmBots.Brain;
using Impostor.Server.LlmBots.Llm;

// Sends one prompt to a model with a chosen request shape and prints what came back (status, tokens, finish reason, text).
// Meant for finding out how each free model behaves so the client can ask for answers in a form it returns.
internal static class RawProbe
{
    public static readonly string[] Variants = { "base", "noreason", "disabled", "minimal", "big", "json", "prefill", "hint" };

    public static async Task<int> RunAsync(string[] models, string[] variants)
    {
        var key = ApiKey.Find("OPENROUTER_API_KEY");
        if (key == null)
        {
            Console.WriteLine("No OPENROUTER_API_KEY found.");
            return 2;
        }

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
        http.DefaultRequestHeaders.Add("HTTP-Referer", "https://github.com/Impostor/Impostor");
        http.DefaultRequestHeaders.Add("X-Title", "Impostor LLM bots (probe)");

        var user = PromptBuilder.User(ProbeScenarios.Crew());
        foreach (var model in models)
        {
            foreach (var variant in variants)
            {
                await OneAsync(http, model, variant, PromptBuilder.SystemPrompt, user);
                await Task.Delay(3500);
            }
        }

        return 0;
    }

    private static async Task OneAsync(HttpClient http, string model, string variant, string system, string user)
    {
        var messages = new List<object>
        {
            new Dictionary<string, string> { ["role"] = "system", ["content"] = system },
            new Dictionary<string, string> { ["role"] = "user", ["content"] = user },
        };
        var body = new Dictionary<string, object?> { ["model"] = model, ["max_tokens"] = 350, ["temperature"] = 0.8, ["messages"] = messages };

        switch (variant)
        {
            case "base":
                body["reasoning"] = new Dictionary<string, object> { ["effort"] = "low", ["exclude"] = true };
                break;
            case "noreason":
                break;
            case "disabled":
                body["reasoning"] = new Dictionary<string, object> { ["enabled"] = false };
                break;
            case "minimal":
                body["reasoning"] = new Dictionary<string, object> { ["effort"] = "minimal" };
                break;
            case "big":
                body["max_tokens"] = 1600;
                break;
            case "json":
                body["max_tokens"] = 700;
                body["response_format"] = new Dictionary<string, string> { ["type"] = "json_object" };
                break;
            case "prefill":
                messages.Add(new Dictionary<string, string> { ["role"] = "assistant", ["content"] = "{\"say\": [\"" });
                break;
            case "hint":
                body["max_tokens"] = 900;
                messages[0] = new Dictionary<string, string> { ["role"] = "system", ["content"] = system + "\nDo not think step by step. Do not explain. Your whole reply is the JSON object." };
                break;
        }

        var watch = Stopwatch.StartNew();
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://openrouter.ai/api/v1/chat/completions")
            {
                Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
            };
            using var response = await http.SendAsync(request);
            var text = await response.Content.ReadAsStringAsync();
            Console.WriteLine($"--- {model} [{variant}] HTTP {(int)response.StatusCode} in {watch.ElapsedMilliseconds / 1000.0:0.0}s");
            Summarize(text);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"--- {model} [{variant}] EXCEPTION {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void Summarize(string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            if (root.TryGetProperty("error", out var error))
            {
                Console.WriteLine("  error: " + Short(error.ToString(), 400));
                return;
            }

            var choice = root.GetProperty("choices")[0];
            var message = choice.GetProperty("message");
            string Str(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? string.Empty : string.Empty;
            var content = Str(message, "content");
            var reasoning = Str(message, "reasoning");
            var finish = choice.TryGetProperty("finish_reason", out var f) ? f.ToString() : "?";
            var usage = root.TryGetProperty("usage", out var u) ? u.ToString() : "?";
            var provider = root.TryGetProperty("provider", out var p) ? p.ToString() : "?";
            Console.WriteLine($"  finish={finish} provider={provider}");
            Console.WriteLine($"  usage={Short(usage, 300)}");
            Console.WriteLine($"  content({content.Length}): {Short(content, 500)}");
            Console.WriteLine($"  reasoning({reasoning.Length}): {Short(reasoning, 200)}");
        }
        catch (Exception ex)
        {
            Console.WriteLine("  unparsable body: " + ex.Message + " :: " + Short(text, 400));
        }
    }

    private static string Short(string s, int max) => (s.Length <= max ? s : s.Substring(0, max) + "...").Replace("\n", "\\n");
}
