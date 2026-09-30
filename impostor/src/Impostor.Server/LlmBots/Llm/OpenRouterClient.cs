using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Impostor.Server.LlmBots.Brain;
using Microsoft.Extensions.Logging;

namespace Impostor.Server.LlmBots.Llm
{
    internal sealed class LlmUnavailableException : Exception
    {
        public LlmUnavailableException(string message)
            : base(message)
        {
        }
    }

    internal sealed record LlmReply(string Text, string Model, long LatencyMs);

    /// <summary>
    ///     What the client has learned about one model during this run.
    /// </summary>
    internal sealed class ModelState
    {
        public int Ok { get; set; }

        public int Fail { get; set; }

        public int Streak { get; set; }

        public double AverageMs { get; set; }

        public DateTime Until { get; set; } = DateTime.MinValue;

        public bool Dead { get; set; }

        public bool NoReasoningParam { get; set; }

        public string? LastError { get; set; }

        public string? LastProblem { get; set; }
    }

    /// <summary>
    ///     A small client for OpenRouter's OpenAI compatible chat endpoint that only ever uses free models,
    ///     learns which of them answer quickly and in the right format, fails over between them and respects the
    ///     request budget.
    /// </summary>
    internal sealed class OpenRouterClient : IDisposable
    {
        private static readonly string[] FallbackModels =
        {
            "google/gemma-4-26b-a4b-it:free",
            "google/gemma-4-31b-it:free",
            "inclusionai/ling-3.0-flash-sante:free",
            "nvidia/nemotron-3.5-lightning:free",
            "dots-studio/dots-3-note-preview:free",
            "poolside/laguna-s-2.1:free",
            "qwen/qwen3.8-27b:free",
        };

        private static readonly string[] Unwanted =
        {
            "safety", "guard", "embed", "moderation", "lyria", "tts", "whisper", "code", "-r1", "stealth", "inkling", "image", "audio",
        };

        private static readonly Regex SizeInName = new(@"(\d+(?:\.\d+)?)b\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private readonly LlmBotsConfig _config;
        private readonly ILogger _logger;
        private readonly HttpClient _http;
        private readonly string? _apiKey;
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, ModelState> _state = new();
        private readonly SemaphoreSlim _concurrency = new(3, 3);
        private readonly SemaphoreSlim _discoveryLock = new(1, 1);
        private List<string>? _models;
        private bool _keyRejected;
        private int _calls;
        private int _failures;

        public OpenRouterClient(LlmBotsConfig config, ILogger logger, string? apiKey, HttpMessageHandler? handler = null)
        {
            _config = config;
            _logger = logger;
            _apiKey = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey;
            _http = handler == null ? new HttpClient() : new HttpClient(handler);
            _http.Timeout = TimeSpan.FromSeconds(Math.Max(5, config.RequestTimeoutSeconds) + 5);
            _http.DefaultRequestHeaders.Add("HTTP-Referer", "https://github.com/Impostor/Impostor");
            _http.DefaultRequestHeaders.Add("X-Title", "Impostor LLM bots");
            if (_apiKey != null)
            {
                _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
            }

            Budget = new RequestBudget(config.MaxRequestsPerMinute, config.MaxRequestsPerRun);
        }

        public RequestBudget Budget { get; }

        public bool HasKey => _apiKey != null;

        public bool Available => HasKey && !_keyRejected && !Budget.IsBlocked;

        public string? LastModel { get; private set; }

        public int Calls => _calls;

        public int Failures => _failures;

        private string BaseUrl => _config.OpenRouterBaseUrl.TrimEnd('/');

        /// <summary>
        ///     Asks for a completion. Models are tried best first; a model that is rate limited, overloaded, silent or that
        ///     answers in the wrong shape is set aside for a while and the next one is asked, up to four in one call.
        /// </summary>
        /// <param name="system">System prompt.</param>
        /// <param name="user">User prompt.</param>
        /// <param name="important">Whether the request may use the slots reserved for votes.</param>
        /// <param name="maxWait">How long the whole call may wait for a free slot in the request budget.</param>
        /// <param name="ct">Cancellation.</param>
        /// <param name="maxTokens">Answer size limit.</param>
        /// <param name="accept">Optional check of the answer text; a model whose answer fails it is skipped.</param>
        /// <param name="preferredModel">A model to ask first while it is usable (the one a bot is named after).</param>
        /// <returns>The reply and the model that produced it.</returns>
        public async Task<LlmReply> CompleteAsync(string system, string user, bool important, TimeSpan maxWait, CancellationToken ct, int maxTokens = 350, Func<string, bool>? accept = null, string? preferredModel = null)
        {
            if (!HasKey)
            {
                throw new LlmUnavailableException("no API key configured");
            }

            if (_keyRejected)
            {
                throw new LlmUnavailableException("API key was rejected");
            }

            var models = await GetModelsAsync(ct);
            var deadline = DateTime.UtcNow + maxWait;
            var tried = new HashSet<string>();
            Exception? last = null;

            for (var attempt = 0; attempt < 4; attempt++)
            {
                var usable = Ordered(models).Where(m => !tried.Contains(m)).ToList();
                var model = preferredModel != null && usable.Contains(preferredModel) ? preferredModel : usable.FirstOrDefault();
                if (model == null)
                {
                    break;
                }

                tried.Add(model);
                var remaining = deadline - DateTime.UtcNow;
                if (!await Budget.TryAcquireAsync(important, remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero, ct))
                {
                    throw new LlmUnavailableException(last == null ? "request budget exhausted" : last.Message);
                }

                await _concurrency.WaitAsync(ct);
                var watch = Stopwatch.StartNew();
                try
                {
                    var text = await RequestAsync(model, system, user, maxTokens, ct);
                    if (accept != null && !accept(text))
                    {
                        RecordFailure(model, "the answer did not follow the format", 90);
                        last = new InvalidOperationException($"{model}: the answer did not follow the format");
                        Interlocked.Increment(ref _failures);
                        continue;
                    }

                    RecordSuccess(model, watch.ElapsedMilliseconds);
                    Interlocked.Increment(ref _calls);
                    LastModel = model;
                    return new LlmReply(text, model, watch.ElapsedMilliseconds);
                }
                catch (LlmUnavailableException)
                {
                    throw;
                }
                catch (HttpRequestException ex)
                {
                    // Nothing reached OpenRouter (offline, DNS, TLS): the other models would fail the same way.
                    last = ex;
                    Interlocked.Increment(ref _failures);
                    _logger.LogDebug("LLM request to {Model} failed: {Message}", model, ex.Message);
                    break;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    // The caller ran out of time waiting: this model is too slow for a live game right now.
                    if (watch.ElapsedMilliseconds > 3000)
                    {
                        RecordFailure(model, "too slow for the game", 30);
                    }

                    throw;
                }
                catch (Exception ex)
                {
                    last = ex;
                    Interlocked.Increment(ref _failures);
                    _logger.LogDebug("LLM request to {Model} failed: {Message}", model, ex.Message);
                }
                finally
                {
                    _concurrency.Release();
                }
            }

            throw new LlmUnavailableException(last?.Message ?? "no model available");
        }

        /// <summary>
        ///     Sends one request to one specific model (no failover). Used to compare models.
        /// </summary>
        /// <param name="model">The model id.</param>
        /// <param name="system">System prompt.</param>
        /// <param name="user">User prompt.</param>
        /// <param name="maxWait">How long to wait for a free slot in the request budget.</param>
        /// <param name="ct">Cancellation.</param>
        /// <param name="maxTokens">Answer size limit.</param>
        /// <param name="accept">Optional check of the answer text; an answer that fails it counts as a failure of the model.</param>
        /// <returns>The reply with the time it took.</returns>
        public async Task<LlmReply> CompleteWithModelAsync(string model, string system, string user, TimeSpan maxWait, CancellationToken ct, int maxTokens = 350, Func<string, bool>? accept = null)
        {
            if (!HasKey)
            {
                throw new LlmUnavailableException("no API key configured");
            }

            if (!await Budget.TryAcquireAsync(true, maxWait, ct))
            {
                throw new LlmUnavailableException("request budget exhausted");
            }

            await _concurrency.WaitAsync(ct);
            try
            {
                var watch = Stopwatch.StartNew();
                var text = await RequestAsync(model, system, user, maxTokens, ct);
                if (accept != null && !accept(text))
                {
                    RecordFailure(model, "the answer did not follow the format", 90);
                    Interlocked.Increment(ref _failures);
                    throw new InvalidOperationException("the answer did not follow the format");
                }

                RecordSuccess(model, watch.ElapsedMilliseconds);
                Interlocked.Increment(ref _calls);
                LastModel = model;
                return new LlmReply(text, model, watch.ElapsedMilliseconds);
            }
            finally
            {
                _concurrency.Release();
            }
        }

        /// <summary>
        ///     Tries the best few models with a small realistic prompt, so the first real meeting already knows which
        ///     of them are up, fast and answer in the right format. Stops asking once enough of them proved to work.
        /// </summary>
        /// <param name="ct">Cancellation.</param>
        /// <param name="count">How many models to try at most.</param>
        /// <param name="enough">How many working models are enough.</param>
        /// <returns>A one line summary, and whether at least one model works.</returns>
        public async Task<(string Summary, bool Usable)> CalibrateAsync(CancellationToken ct, int count = 8, int enough = 3)
        {
            if (!HasKey)
            {
                return ("no key", false);
            }

            var models = (await GetModelsAsync(ct)).Take(count).ToList();
            var user = PromptBuilder.User(CalibrationContext());
            using var gate = new SemaphoreSlim(2, 2);
            var working = 0;

            async Task<(string Model, long Ms, string? Error, bool Skipped)> One(string model)
            {
                await gate.WaitAsync(ct);
                try
                {
                    if (Volatile.Read(ref working) >= enough)
                    {
                        return (model, 0, null, true);
                    }

                    var reply = await CompleteWithModelAsync(model, PromptBuilder.SystemPrompt, user, TimeSpan.FromSeconds(60), ct, accept: DecisionParser.LooksUsable);
                    Interlocked.Increment(ref working);
                    return (model, reply.LatencyMs, null, false);
                }
                catch (LlmUnavailableException ex)
                {
                    return (model, 0, ex.Message, false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    return (model, 0, ex.Message, false);
                }
                finally
                {
                    gate.Release();
                }
            }

            var results = await Task.WhenAll(models.Select(One));
            if (_keyRejected)
            {
                return ("OpenRouter rejected the API key", false);
            }

            if (Budget.IsBlocked)
            {
                return ("the free request limit is used up for now", false);
            }

            var tried = results.Where(r => !r.Skipped).ToList();
            var good = tried.Where(r => r.Error == null).OrderBy(r => r.Ms).ToList();
            foreach (var r in tried.Where(r => r.Error != null))
            {
                _logger.LogInformation("LLM check: {Model} is not usable right now ({Error})", r.Model, Trim(r.Error!));
            }

            foreach (var r in good)
            {
                _logger.LogInformation("LLM check: {Model} answered in {Seconds:0.0}s", r.Model, r.Ms / 1000.0);
            }

            var summary = good.Count == 0
                ? $"none of the {tried.Count} models tried is usable right now"
                : $"{good.Count} of {tried.Count} models tried usable, fastest {good[0].Model} ({good[0].Ms / 1000.0:0.0}s)";
            return (summary, good.Count > 0);
        }

        /// <summary>
        ///     Gets what the client knows about every model, best first, for status pages.
        /// </summary>
        /// <returns>One line per model.</returns>
        public IReadOnlyList<string> DescribeModels()
        {
            var list = _models ?? new List<string>();
            return Ordered(list, includeCooling: true)
                .Select(m =>
                {
                    var s = _state.GetValueOrDefault(m);
                    if (s == null)
                    {
                        return $"{m}: not tried yet";
                    }

                    var status = s.Dead ? "not usable (" + s.LastError + ")" : (s.Until > DateTime.UtcNow ? $"resting {(int)(s.Until - DateTime.UtcNow).TotalSeconds}s ({s.LastError})" : "ready");
                    var problem = s.Fail > 0 && s.LastProblem != null ? $", last problem: {s.LastProblem}" : string.Empty;
                    return $"{m}: {s.Ok} ok, {s.Fail} failed, avg {(s.Ok == 0 ? "-" : (s.AverageMs / 1000.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "s")}, {status}{problem}";
                })
                .ToList();
        }

        /// <summary>
        ///     Asks OpenRouter about the key in use: whether the account is on the free tier and how much of the
        ///     allowance is used. The key itself (and its label, which is a piece of it) is never returned.
        /// </summary>
        /// <param name="ct">Cancellation.</param>
        /// <returns>A one line description, or the reason it could not be read.</returns>
        public async Task<string> GetKeyInfoAsync(CancellationToken ct)
        {
            if (!HasKey)
            {
                return "no key";
            }

            foreach (var path in new[] { "/auth/key", "/key" })
            {
                try
                {
                    using var response = await _http.GetAsync(BaseUrl + path, ct);
                    if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                    {
                        return $"OpenRouter rejected the key (HTTP {(int)response.StatusCode})";
                    }

                    if (!response.IsSuccessStatusCode)
                    {
                        continue;
                    }

                    using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
                    var data = doc.RootElement.TryGetProperty("data", out var d) ? d : doc.RootElement;
                    string Read(string name) => data.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null ? v.ToString() : "-";
                    return $"free tier: {Read("is_free_tier")}, credits used: {Read("usage")}, credit limit: {Read("limit")}, remaining: {Read("limit_remaining")}";
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
                {
                    return "could not read key info: " + ex.Message;
                }
            }

            return "key info endpoint not available";
        }

        /// <summary>
        ///     Gets the models that can be asked right now (not dead, not resting), best first.
        /// </summary>
        public async Task<IReadOnlyList<string>> WorkingModelsAsync(CancellationToken ct) => Ordered(await GetModelsAsync(ct)).ToList();

        public async Task<IReadOnlyList<string>> GetModelsAsync(CancellationToken ct)
        {
            if (_models != null)
            {
                return _models;
            }

            await _discoveryLock.WaitAsync(ct);
            try
            {
                if (_models != null)
                {
                    return _models;
                }

                _models = await BuildModelListAsync(ct);
                _logger.LogInformation("LLM models in use: {Models}", string.Join(", ", _models));
                return _models;
            }
            finally
            {
                _discoveryLock.Release();
            }
        }

        public void Dispose()
        {
            _http.Dispose();
        }

        internal static IReadOnlyList<string> RankFreeModels(JsonElement modelsResponse)
        {
            var found = new List<(string Id, double Score)>();
            if (!modelsResponse.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<string>();
            }

            foreach (var m in data.EnumerateArray())
            {
                var id = m.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;
                if (id == null || id == "openrouter/free")
                {
                    continue;
                }

                var free = id.EndsWith(":free", StringComparison.Ordinal);
                if (!free && m.TryGetProperty("pricing", out var pricing))
                {
                    free = IsZero(pricing, "prompt") && IsZero(pricing, "completion");
                }

                if (!free || Unwanted.Any(u => id.Contains(u, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                if (m.TryGetProperty("architecture", out var arch) && arch.TryGetProperty("modality", out var modality))
                {
                    var text = modality.GetString() ?? string.Empty;
                    var output = text.Contains("->", StringComparison.Ordinal) ? text.Substring(text.IndexOf("->", StringComparison.Ordinal) + 2) : text;
                    if (output != "text")
                    {
                        continue;
                    }
                }

                var context = m.TryGetProperty("context_length", out var ctx) && ctx.ValueKind == JsonValueKind.Number ? ctx.GetInt64() : 0;
                if (context < 8000)
                {
                    continue;
                }

                double score = 0;
                // Chat tuned models answer fast and stay in format; giant "thinking" style models are too slow for a live meeting.
                if (id.Contains("instruct", StringComparison.OrdinalIgnoreCase) || id.Contains("-it", StringComparison.OrdinalIgnoreCase))
                {
                    score += 3;
                }

                // Models that think before they answer can be told not to, but not all of them listen.
                if (id.Contains("reasoning", StringComparison.OrdinalIgnoreCase) || id.Contains("thinking", StringComparison.OrdinalIgnoreCase))
                {
                    score -= 1.5;
                }

                foreach (var family in new[] { "gemma", "llama", "qwen", "mistral", "nemotron", "ling", "poolside", "dots" })
                {
                    if (id.Contains(family, StringComparison.OrdinalIgnoreCase))
                    {
                        score += 1;
                    }
                }

                var size = SizeInName.Match(id);
                if (size.Success && double.TryParse(size.Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var billions))
                {
                    score += Math.Min(billions, 40) / 20.0;
                    if (billions >= 200)
                    {
                        score -= 3;
                    }
                }

                found.Add((id, score));
            }

            return found.OrderByDescending(f => f.Score).ThenBy(f => f.Id, StringComparer.Ordinal).Select(f => f.Id).ToList();
        }

        internal static string? ExtractText(JsonElement response)
        {
            if (response.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0)
            {
                var message = choices[0].TryGetProperty("message", out var msg) ? msg : default;
                if (message.ValueKind == JsonValueKind.Object)
                {
                    var content = ReadContent(message, "content");
                    if (!string.IsNullOrWhiteSpace(content))
                    {
                        return content;
                    }

                    // Some reasoning models leave the answer in the reasoning field when they run out of tokens.
                    var reasoning = ReadContent(message, "reasoning");
                    if (!string.IsNullOrWhiteSpace(reasoning) && reasoning.Contains('{', StringComparison.Ordinal))
                    {
                        return reasoning;
                    }
                }
            }

            return null;
        }

        private static string? ReadContent(JsonElement message, string name)
        {
            if (!message.TryGetProperty(name, out var value))
            {
                return null;
            }

            if (value.ValueKind == JsonValueKind.String)
            {
                return value.GetString();
            }

            if (value.ValueKind == JsonValueKind.Array)
            {
                var builder = new StringBuilder();
                foreach (var part in value.EnumerateArray())
                {
                    if (part.ValueKind == JsonValueKind.String)
                    {
                        builder.Append(part.GetString());
                    }
                    else if (part.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
                    {
                        builder.Append(text.GetString());
                    }
                }

                return builder.ToString();
            }

            return null;
        }

        private static bool IsZero(JsonElement pricing, string name)
        {
            if (!pricing.TryGetProperty(name, out var value))
            {
                return false;
            }

            var text = value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
            return double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var number) && number == 0;
        }

        private async Task<List<string>> BuildModelListAsync(CancellationToken ct)
        {
            var models = new List<string>();

            foreach (var model in _config.Models.Where(m => !string.IsNullOrWhiteSpace(m)))
            {
                if (_config.AllowPaidModels || model.EndsWith(":free", StringComparison.Ordinal))
                {
                    models.Add(model);
                }
                else
                {
                    _logger.LogWarning("Model {Model} is not free and AllowPaidModels is off, skipping it", model);
                }
            }

            if (models.Count == 0)
            {
                try
                {
                    using var response = await _http.GetAsync($"{BaseUrl}/models", ct);
                    if (response.IsSuccessStatusCode)
                    {
                        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
                        models.AddRange(RankFreeModels(doc.RootElement).Take(8));
                    }
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
                {
                    _logger.LogWarning("Could not list OpenRouter models ({Message}), using the built-in list", ex.Message);
                }

                if (models.Count == 0)
                {
                    models.AddRange(FallbackModels);
                }
            }

            return models;
        }

        private ModelState StateOf(string model) => _state.GetOrAdd(model, _ => new ModelState());

        private void RecordSuccess(string model, long ms)
        {
            var s = StateOf(model);
            lock (s)
            {
                s.AverageMs = s.Ok == 0 ? ms : (0.7 * s.AverageMs) + (0.3 * ms);
                s.Ok++;
                s.Streak = 0;
                s.Until = DateTime.MinValue;
                s.LastError = null;
            }
        }

        private void RecordFailure(string model, string why, double baseSeconds = 30)
        {
            var s = StateOf(model);
            lock (s)
            {
                s.Fail++;
                s.Streak++;
                s.LastError = why;
                s.LastProblem = why;

                // Every failure in a row doubles the rest, so a model that keeps failing is asked less and less.
                var seconds = Math.Min(900, baseSeconds * Math.Pow(2, Math.Min(3, s.Streak - 1)));
                var until = DateTime.UtcNow.AddSeconds(seconds);
                if (until > s.Until)
                {
                    s.Until = until;
                }
            }
        }

        private void MarkDead(string model, string why)
        {
            var s = StateOf(model);
            lock (s)
            {
                s.Dead = true;
                s.Fail++;
                s.LastError = why;
                s.LastProblem = why;
            }
        }

        /// <summary>
        ///     Orders the models: the ones that proved fast first, then untested ones in ranking order.
        ///     Models that are resting or unusable are left out.
        /// </summary>
        /// <param name="models">All models.</param>
        /// <param name="includeCooling">Whether to keep the ones that are resting or dead (for reports).</param>
        /// <returns>The models to ask, in order.</returns>
        private IEnumerable<string> Ordered(IReadOnlyList<string> models, bool includeCooling = false)
        {
            var now = DateTime.UtcNow;
            return models
                .Select((m, i) => (Model: m, Index: i, State: _state.GetValueOrDefault(m)))
                .Where(x => includeCooling || (x.State == null || (!x.State.Dead && x.State.Until <= now)))
                .OrderBy(x => x.State == null || x.State.Ok == 0 ? 1 : (x.State.AverageMs <= 8000 ? 0 : 2))
                .ThenBy(x => x.State == null || x.State.Ok == 0 ? x.Index : (int)x.State.AverageMs)
                .Select(x => x.Model);
        }

        private async Task<string> RequestAsync(string model, string system, string user, int maxTokens, CancellationToken ct)
        {
            var state = StateOf(model);
            var withReasoningParam = !state.NoReasoningParam;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(5, _config.RequestTimeoutSeconds)));

            for (var attempt = 0; attempt < 2; attempt++)
            {
                var body = new Dictionary<string, object?>
                {
                    ["model"] = model,
                    ["messages"] = new object[]
                    {
                        new Dictionary<string, string> { ["role"] = "system", ["content"] = system },
                        new Dictionary<string, string> { ["role"] = "user", ["content"] = user },
                    },
                    ["max_tokens"] = maxTokens,
                    ["temperature"] = 0.8,
                };

                if (withReasoningParam)
                {
                    // Free models that think first spend the whole answer budget on it. Tell them not to.
                    body["reasoning"] = new Dictionary<string, object> { ["enabled"] = false };
                }

                using var request = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/chat/completions")
                {
                    Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
                };

                HttpResponseMessage response;
                try
                {
                    response = await _http.SendAsync(request, timeout.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    RecordFailure(model, "timeout", 45);
                    throw new TimeoutException($"{model} did not answer in time");
                }
                catch (HttpRequestException)
                {
                    RecordFailure(model, "network error", 30);
                    throw;
                }

                using (response)
                {
                    var text = await response.Content.ReadAsStringAsync(ct);

                    if (response.StatusCode == HttpStatusCode.BadRequest)
                    {
                        if (text.Contains("mandatory", StringComparison.OrdinalIgnoreCase) || text.Contains("cannot be disabled", StringComparison.OrdinalIgnoreCase))
                        {
                            MarkDead(model, "it insists on thinking first");
                            throw new InvalidOperationException($"{model} insists on thinking first");
                        }

                        if (withReasoningParam && text.Contains("reasoning", StringComparison.OrdinalIgnoreCase))
                        {
                            state.NoReasoningParam = true;
                            withReasoningParam = false;
                            continue;
                        }
                    }

                    if (response.StatusCode is HttpStatusCode.Unauthorized)
                    {
                        _keyRejected = true;
                        _logger.LogError("OpenRouter rejected the API key ({Status}). Bots will play with the built-in heuristics.", (int)response.StatusCode);
                        throw new LlmUnavailableException("API key rejected");
                    }

                    if (response.StatusCode == HttpStatusCode.Forbidden)
                    {
                        // Some free models are only for certain apps, others need a different account setup.
                        MarkDead(model, "not available to this account/app");
                        throw new InvalidOperationException($"{model} is not available to this account (HTTP 403)");
                    }

                    if (response.StatusCode == HttpStatusCode.PaymentRequired)
                    {
                        RecordFailure(model, "needs credits", 3600);
                        throw new InvalidOperationException("model requires credits");
                    }

                    if (response.StatusCode == (HttpStatusCode)429)
                    {
                        HandleRateLimit(model, response, text);
                        throw new InvalidOperationException("rate limited");
                    }

                    if (response.StatusCode == HttpStatusCode.NotFound)
                    {
                        MarkDead(model, "model not found");
                        throw new InvalidOperationException("model not found");
                    }

                    if (!response.IsSuccessStatusCode)
                    {
                        RecordFailure(model, $"HTTP {(int)response.StatusCode}", 45);
                        throw new InvalidOperationException($"HTTP {(int)response.StatusCode}: {Trim(text)}");
                    }

                    JsonDocument doc;
                    try
                    {
                        doc = JsonDocument.Parse(text);
                    }
                    catch (JsonException)
                    {
                        RecordFailure(model, "unreadable response", 45);
                        throw new InvalidOperationException($"{model} sent an unreadable response");
                    }

                    using var _ = doc;
                    if (doc.RootElement.TryGetProperty("error", out var error))
                    {
                        var code = error.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetInt32() : 0;
                        if (code == 429)
                        {
                            HandleRateLimit(model, response, text);
                            throw new InvalidOperationException("rate limited");
                        }

                        RecordFailure(model, code == 503 ? "overloaded" : "provider error", 45);
                        throw new InvalidOperationException("provider error: " + Trim(error.ToString()));
                    }

                    var content = ExtractText(doc.RootElement);
                    if (string.IsNullOrWhiteSpace(content))
                    {
                        RecordFailure(model, "empty answer", 60);
                        throw new InvalidOperationException("empty answer");
                    }

                    return content;
                }
            }

            throw new InvalidOperationException("request failed");
        }

        private void HandleRateLimit(string model, HttpResponseMessage response, string body)
        {
            if (body.Contains("per-day", StringComparison.OrdinalIgnoreCase) || body.Contains("per day", StringComparison.OrdinalIgnoreCase))
            {
                // The free daily allowance of the account is gone. Stop asking until the day rolls over.
                var tomorrow = DateTime.UtcNow.Date.AddDays(1);
                Budget.BlockUntil(tomorrow);
                _logger.LogWarning("OpenRouter's free daily request limit is used up. Bots use built-in heuristics until {Time:u}. Adding credits to your OpenRouter account raises the limit.", tomorrow);
                return;
            }

            if (body.Contains("per-min", StringComparison.OrdinalIgnoreCase) || body.Contains("per min", StringComparison.OrdinalIgnoreCase))
            {
                // Our own per minute allowance is used up: every model would say the same, so wait for the window.
                var wait = 30.0;
                if (response.Headers.RetryAfter?.Delta is { } delta)
                {
                    wait = delta.TotalSeconds;
                }
                else if (response.Headers.TryGetValues("X-RateLimit-Reset", out var values) && long.TryParse(values.FirstOrDefault(), out var reset))
                {
                    var at = reset > 100_000_000_000 ? DateTimeOffset.FromUnixTimeMilliseconds(reset) : DateTimeOffset.FromUnixTimeSeconds(reset);
                    wait = (at - DateTimeOffset.UtcNow).TotalSeconds;
                }

                Budget.BlockUntil(DateTime.UtcNow.AddSeconds(Math.Min(60, Math.Max(3, wait))));
                return;
            }

            // Anything else is, on the free tier, nearly always the shared pool behind that one model being used up
            // by everybody: rest the model and ask another one.
            RecordFailure(model, "rate limited upstream", 60);
        }

        private static string Trim(string text) => text.Length > 200 ? text.Substring(0, 200) : text;

        private static MeetingContext CalibrationContext()
        {
            var players = new List<PlayerBrief>
            {
                new(0, "Ada", "Red", true, true, false),
                new(1, "Bolt", "Blue", true, false, false),
                new(2, "Cleo", "Green", true, false, false),
                new(3, "Dax", "Pink", false, false, false),
            };
            return new MeetingContext
            {
                Me = players[0],
                Players = players,
                Stage = MeetingStage.Opening,
                Reason = "Cleo found Dax's body in Admin.",
                Sightings = new[] { "Bolt in Electrical (30s ago)" },
                MyRoute = new[] { "Cafeteria (20s ago)", "Weapons (5s ago)" },
                SecondsLeft = 40,
                MaxLines = 2,
            };
        }
    }
}
