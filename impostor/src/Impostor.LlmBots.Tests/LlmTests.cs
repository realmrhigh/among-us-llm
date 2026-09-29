using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Impostor.LlmBots.Sim;
using Impostor.Server.LlmBots;
using Impostor.Server.LlmBots.Brain;
using Impostor.Server.LlmBots.Llm;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace Impostor.LlmBots.Tests
{
    public class LlmTests
    {
        private readonly ITestOutputHelper _output;

        public LlmTests(ITestOutputHelper output)
        {
            _output = output;
        }

        private static MeetingContext Context(bool impostor = false, MeetingStage stage = MeetingStage.Opening)
        {
            var players = new List<PlayerBrief>
            {
                new(0, "Ada", "Red", true, true, false),
                new(1, "Bolt", "Blue", true, false, impostor),
                new(2, "Cleo", "Green", true, false, false),
                new(3, "Dax", "Pink", false, false, false),
            };
            return new MeetingContext
            {
                Me = players[0],
                IAmImpostor = impostor,
                Players = players,
                Stage = stage,
                Reason = "Cleo found Dax's body in Admin.",
                MaxLines = 2,
                SecondsLeft = 30,
            };
        }

        [Fact]
        public void ParserHandlesFencedJsonAndProse()
        {
            var text = "Sure! Here you go:\n```json\n{\"say\": [\"i was in admin\", \"Cleo where were you?\"], \"vote\": \"cleo\", \"note\": \"x\"}\n```";
            var d = DecisionParser.Parse(text, Context(), "t")!;
            Assert.Equal(2, d.Say.Count);
            Assert.Equal("Cleo", d.Vote);
        }

        [Fact]
        public void ParserAcceptsSingleStringAndSkip()
        {
            var d = DecisionParser.Parse("{\"say\": \"no idea\", \"vote\": \"Skip\"}", Context(), "t")!;
            Assert.Single(d.Say);
            Assert.Equal("skip", d.Vote);
        }

        [Fact]
        public void ParserRejectsInvalidVotes()
        {
            Assert.Null(DecisionParser.Parse("{\"say\": [], \"vote\": \"Dax\"}", Context(), "t")!.Vote);
            Assert.Null(DecisionParser.Parse("{\"say\": [], \"vote\": \"Ada\"}", Context(), "t")!.Vote);
            Assert.Null(DecisionParser.Parse("{\"say\": [], \"vote\": \"Nobody Here\"}", Context(), "t")!.Vote);
            Assert.Null(DecisionParser.Parse("{\"say\": [], \"vote\": \"Bolt\"}", Context(impostor: true), "t")!.Vote);
        }

        [Fact]
        public void ParserFiltersConfessionsAndMetaTalk()
        {
            var d = DecisionParser.Parse("{\"say\": [\"I am the impostor haha\", \"as an AI i cannot\", \"cleo is sus\"], \"vote\": \"Cleo\"}", Context(impostor: true), "t")!;
            Assert.Equal(new[] { "cleo is sus" }, d.Say);
        }

        [Fact]
        public void ParserTrimsLongLinesAndSelfPrefix()
        {
            var long_ = "Ada: " + string.Join(" ", Enumerable.Repeat("word", 60));
            var d = DecisionParser.Parse("{\"say\": [" + JsonSerializer.Serialize(long_) + "]}", Context(), "t")!;
            Assert.True(d.Say[0].Length <= 100);
            Assert.False(d.Say[0].StartsWith("Ada:", StringComparison.Ordinal));
        }

        [Fact]
        public void ParserFallsBackToPlainText()
        {
            var d = DecisionParser.Parse("i saw bolt near admin", Context(), "t")!;
            Assert.Single(d.Say);
            Assert.Null(DecisionParser.Parse("{ this is not json at all and it is very long " + new string('x', 400), Context(), "t"));
        }

        [Fact]
        public void PromptMentionsEverythingTheModelNeeds()
        {
            var prompt = PromptBuilder.User(Context(impostor: true));
            Assert.Contains("IMPOSTOR", prompt);
            Assert.Contains("Cleo found Dax's body in Admin.", prompt);
            Assert.Contains("Dax (Pink) [DEAD]", prompt);
        }

        [Fact]
        public void ModelRankingPrefersInstructChatModelsAndSkipsJunk()
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", "models.json")));
            var ranked = OpenRouterClient.RankFreeModels(doc.RootElement);
            _output.WriteLine(string.Join("\n", ranked));
            Assert.NotEmpty(ranked);
            Assert.DoesNotContain("openrouter/free", ranked);
            Assert.DoesNotContain(ranked, id => id.Contains("safety") || id.Contains("lyria") || id.Contains("code") || id.Contains("inkling"));

            // A model that thinks first can be told not to, but not all of them listen: it only comes after plain chat models.
            var list = ranked.ToList();
            var thinking = list.FindIndex(id => id.Contains("reasoning"));
            Assert.True(thinking < 0 || thinking > list.FindIndex(id => id.Contains("gemma-4-31b")));
        }

        [Fact]
        public async Task BudgetSpreadsRequestsOverTheWindow()
        {
            var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
            var budget = new RequestBudget(4, 0, () => now);
            for (var i = 0; i < 4; i++)
            {
                Assert.True(await budget.TryAcquireAsync(true, TimeSpan.Zero, CancellationToken.None));
            }

            Assert.False(await budget.TryAcquireAsync(true, TimeSpan.Zero, CancellationToken.None));
            now += TimeSpan.FromSeconds(61);
            Assert.True(await budget.TryAcquireAsync(true, TimeSpan.Zero, CancellationToken.None));
        }

        [Fact]
        public async Task BudgetKeepsTheLastSlotsForVotes()
        {
            var now = DateTime.UtcNow;
            var budget = new RequestBudget(8, 0, () => now);
            var chatter = 0;
            while (await budget.TryAcquireAsync(false, TimeSpan.Zero, CancellationToken.None))
            {
                chatter++;
            }

            Assert.Equal(6, chatter);
            Assert.True(await budget.TryAcquireAsync(true, TimeSpan.Zero, CancellationToken.None));
        }

        [Fact]
        public async Task BudgetHonoursTheRunCapAndBlocks()
        {
            var budget = new RequestBudget(100, 2);
            Assert.True(await budget.TryAcquireAsync(true, TimeSpan.Zero, CancellationToken.None));
            Assert.True(await budget.TryAcquireAsync(true, TimeSpan.Zero, CancellationToken.None));
            Assert.False(await budget.TryAcquireAsync(true, TimeSpan.Zero, CancellationToken.None));

            var blocked = new RequestBudget(100, 0);
            blocked.BlockUntil(DateTime.UtcNow.AddHours(1));
            Assert.False(await blocked.TryAcquireAsync(true, TimeSpan.FromSeconds(1), CancellationToken.None));
        }

        [Fact]
        public void EnvFileParsing()
        {
            var file = Path.GetTempFileName();
            try
            {
                File.WriteAllText(file, "# comment\nOTHER=1\nexport OPENROUTER_API_KEY=\"sk-test-123\"\n");
                Assert.Equal("sk-test-123", ApiKey.ReadVariable(file, "OPENROUTER_API_KEY"));
                File.WriteAllText(file, "OPENROUTER_API_KEY=\n");
                Assert.Equal(string.Empty, ApiKey.ReadVariable(file, "OPENROUTER_API_KEY"));
                Assert.Null(ApiKey.ReadVariable(file, "MISSING"));
            }
            finally
            {
                File.Delete(file);
            }
        }

        [Fact]
        public void KeyIsFoundInAParentFolder()
        {
            var root = Path.Combine(Path.GetTempPath(), "envtest-" + Guid.NewGuid().ToString("N"));
            var nested = Path.Combine(root, "a", "b");
            Directory.CreateDirectory(nested);
            try
            {
                File.WriteAllText(Path.Combine(root, ".env"), "SOME_UNIQUE_KEY_VAR=from-parent\n");
                Assert.Equal("from-parent", ApiKey.Find("SOME_UNIQUE_KEY_VAR", nested));
                Assert.Null(ApiKey.Find("NOT_THERE_VAR", nested));
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }

        private static OpenRouterClient NewClient(MockOpenRouter mock, Action<LlmBotsConfig>? configure = null)
        {
            var config = new LlmBotsConfig { OpenRouterBaseUrl = mock.BaseUrl, RequestTimeoutSeconds = 5, MaxRequestsPerMinute = 100 };
            configure?.Invoke(config);
            return new OpenRouterClient(config, NullLogger.Instance, "test-key");
        }

        [Fact]
        public async Task ClientTalksToTheApiAndDiscoversFreeModels()
        {
            using var mock = new MockOpenRouter();
            using var client = NewClient(mock);
            var reply = await client.CompleteAsync("sys", "user", true, TimeSpan.FromSeconds(1), CancellationToken.None);
            Assert.Equal("mock/free-model:free", reply.Model);
            Assert.Contains("say", reply.Text);
        }

        [Fact]
        public async Task ClientMovesToTheNextModelWhenRateLimited()
        {
            using var mock = new MockOpenRouter();
            mock.ModelsJson = "{\"data\":[{\"id\":\"a/one:free\",\"context_length\":32000,\"architecture\":{\"modality\":\"text->text\"},\"pricing\":{\"prompt\":\"0\",\"completion\":\"0\"}},{\"id\":\"b/two:free\",\"context_length\":32000,\"architecture\":{\"modality\":\"text->text\"},\"pricing\":{\"prompt\":\"0\",\"completion\":\"0\"}}]}";
            mock.Failure = (n, model) => model.StartsWith("a/", StringComparison.Ordinal) ? (429, "{\"error\":{\"message\":\"slow down\"}}") : null;
            using var client = NewClient(mock);
            var reply = await client.CompleteAsync("s", "u", true, TimeSpan.FromSeconds(1), CancellationToken.None);
            Assert.StartsWith("b/", reply.Model);
            Assert.Contains(mock.RequestedModels, m => m.StartsWith("a/", StringComparison.Ordinal));
        }

        [Fact]
        public async Task ClientStopsForTheDayWhenTheDailyLimitIsHit()
        {
            using var mock = new MockOpenRouter();
            mock.Failure = (n, m) => (429, "{\"error\":{\"message\":\"Rate limit exceeded: free-models-per-day\"}}");
            using var client = NewClient(mock);
            await Assert.ThrowsAsync<LlmUnavailableException>(() => client.CompleteAsync("s", "u", true, TimeSpan.FromSeconds(1), CancellationToken.None));
            Assert.False(client.Available);
        }

        [Fact]
        public async Task ClientGivesUpOnARejectedKey()
        {
            using var mock = new MockOpenRouter();
            mock.Failure = (n, m) => (401, "{\"error\":{\"message\":\"No auth\"}}");
            using var client = NewClient(mock);
            await Assert.ThrowsAsync<LlmUnavailableException>(() => client.CompleteAsync("s", "u", true, TimeSpan.FromSeconds(1), CancellationToken.None));
            Assert.False(client.Available);
        }

        [Fact]
        public async Task ClientWithoutKeyIsUnavailable()
        {
            var client = new OpenRouterClient(new LlmBotsConfig(), NullLogger.Instance, null);
            Assert.False(client.Available);
            await Assert.ThrowsAsync<LlmUnavailableException>(() => client.CompleteAsync("s", "u", true, TimeSpan.Zero, CancellationToken.None));
        }

        [Fact]
        public async Task ClientRetriesWithoutTheReasoningParameter()
        {
            using var mock = new MockOpenRouter();
            var seen = 0;
            mock.Failure = (n, m) => Interlocked.Increment(ref seen) == 1 ? (400, "{\"error\":{\"message\":\"unknown parameter: reasoning\"}}") : null;
            using var client = NewClient(mock);
            var reply = await client.CompleteAsync("s", "u", true, TimeSpan.FromSeconds(1), CancellationToken.None);
            Assert.NotNull(reply.Text);
            Assert.Equal(2, mock.Calls);
        }

        [Fact]
        public async Task BrainFallsBackToHeuristicsWhenTheModelAnswersNonsense()
        {
            using var mock = new MockOpenRouter { Responder = (m, s, u) => "lol no" + new string('!', 300) };
            using var client = NewClient(mock);
            var brain = new LlmBrain(client, new HeuristicBrain(1), NullLogger.Instance);
            var decision = await brain.DecideAsync(Context(stage: MeetingStage.FinalVote), CancellationToken.None);
            Assert.NotNull(decision.Vote);
            Assert.Contains("heuristic", decision.Source);
        }

        [Fact]
        public async Task BrainUsesTheModelAnswer()
        {
            using var mock = new MockOpenRouter { Responder = (m, s, u) => "{\"say\":[\"cleo was near admin\"],\"vote\":\"Cleo\"}" };
            using var client = NewClient(mock);
            var brain = new LlmBrain(client, new HeuristicBrain(1), NullLogger.Instance);
            var decision = await brain.DecideAsync(Context(), CancellationToken.None);
            Assert.Equal("cleo was near admin", decision.Say.Single());
            Assert.Equal("Cleo", decision.Vote);
            Assert.StartsWith("llm:", decision.Source);
        }

        private const string TwoModels = "{\"data\":[{\"id\":\"a/one-it:free\",\"context_length\":32000,\"architecture\":{\"modality\":\"text->text\"},\"pricing\":{\"prompt\":\"0\",\"completion\":\"0\"}},{\"id\":\"b/two:free\",\"context_length\":32000,\"architecture\":{\"modality\":\"text->text\"},\"pricing\":{\"prompt\":\"0\",\"completion\":\"0\"}}]}";

        private const string GoodJson = "{\"say\":[\"cleo was near admin\"],\"vote\":\"Cleo\"}";

        [Fact]
        public void ParserSalvagesAnswersThatAreNotValidJson()
        {
            // Missing closing brace and an unescaped quote inside a sentence.
            var d = DecisionParser.Parse("{\"say\": [\"i saw bolt in admin\", \"he said \"trust me\" which is weird\"], \"vote\": \"Bolt\", \"note\": \"x\"", Context(), "t")!;
            Assert.Equal(2, d.Say.Count);
            Assert.Equal("he said \"trust me\" which is weird", d.Say[1]);
            Assert.Equal("Bolt", d.Vote);

            // Cut off in the middle of the second line: only the finished line is kept.
            var cut = DecisionParser.Parse("{\"say\": [\"first line is complete\", \"second line was cut of", Context(), "t")!;
            Assert.Equal(new[] { "first line is complete" }, cut.Say);

            // Single quotes are not JSON either, but the fields are recognisable when quoted properly.
            Assert.True(DecisionParser.LooksUsable("noise {\"vote\": \"skip\" ..."));
        }

        [Fact]
        public void ParserOnlyCallsShapedAnswersUsable()
        {
            Assert.True(DecisionParser.LooksUsable(GoodJson));
            Assert.True(DecisionParser.LooksUsable("```json\n" + GoodJson + "\n```"));
            Assert.False(DecisionParser.LooksUsable("User Safety: safe"));
            Assert.False(DecisionParser.LooksUsable("i think it is bolt"));
            Assert.False(DecisionParser.LooksUsable("   "));
        }

        [Fact]
        public async Task RequestsSwitchThinkingOff()
        {
            using var mock = new MockOpenRouter();
            using var client = NewClient(mock);
            await client.CompleteAsync("s", "u", true, TimeSpan.FromSeconds(1), CancellationToken.None);
            using var body = JsonDocument.Parse(mock.Bodies.First());
            Assert.False(body.RootElement.GetProperty("reasoning").GetProperty("enabled").GetBoolean());
        }

        [Fact]
        public async Task ModelRateLimitedUpstreamRestsAloneAndTheNextOneAnswers()
        {
            using var mock = new MockOpenRouter { ModelsJson = TwoModels, Responder = (m, s, u) => GoodJson };
            mock.Failure = (n, model) => model.StartsWith("a/", StringComparison.Ordinal) ? (429, "{\"error\":{\"message\":\"a/one-it:free is temporarily rate-limited upstream\",\"code\":429,\"metadata\":{\"provider_name\":\"X\"}}}") : null;
            using var client = NewClient(mock);

            var first = await client.CompleteAsync("s", "u", true, TimeSpan.FromSeconds(1), CancellationToken.None);
            Assert.StartsWith("b/", first.Model);
            Assert.True(client.Available);

            // The limited model is left alone for a while: the second call goes straight to the other one.
            var second = await client.CompleteAsync("s", "u", true, TimeSpan.FromSeconds(1), CancellationToken.None);
            Assert.StartsWith("b/", second.Model);
            Assert.Single(mock.RequestedModels, m => m.StartsWith("a/", StringComparison.Ordinal));
        }

        [Fact]
        public async Task AccountMinuteLimitPausesEveryModel()
        {
            using var mock = new MockOpenRouter { ModelsJson = TwoModels };
            mock.Failure = (n, m) => (429, "{\"error\":{\"message\":\"Rate limit exceeded: free-models-per-min.\"}}");
            using var client = NewClient(mock);
            await Assert.ThrowsAsync<LlmUnavailableException>(() => client.CompleteAsync("s", "u", true, TimeSpan.FromMilliseconds(200), CancellationToken.None));
            Assert.Equal(1, mock.Calls);
            Assert.False(client.Available);
        }

        [Fact]
        public async Task ModelThatCannotSkipThinkingIsDroppedForGood()
        {
            using var mock = new MockOpenRouter { ModelsJson = TwoModels, Responder = (m, s, u) => GoodJson };
            mock.Failure = (n, model) => model.StartsWith("a/", StringComparison.Ordinal) ? (400, "{\"error\":{\"message\":\"Reasoning is mandatory for this endpoint and cannot be disabled.\"}}") : null;
            using var client = NewClient(mock);

            for (var i = 0; i < 3; i++)
            {
                var reply = await client.CompleteAsync("s", "u", true, TimeSpan.FromSeconds(1), CancellationToken.None);
                Assert.StartsWith("b/", reply.Model);
            }

            Assert.Single(mock.RequestedModels, m => m.StartsWith("a/", StringComparison.Ordinal));
        }

        [Fact]
        public async Task ModelsThatIgnoreTheFormatAreSkippedAndRested()
        {
            using var mock = new MockOpenRouter { ModelsJson = TwoModels };
            mock.Responder = (model, s, u) => model.StartsWith("a/", StringComparison.Ordinal) ? "User Safety: safe" : GoodJson;
            using var client = NewClient(mock);

            var reply = await client.CompleteAsync("s", "u", true, TimeSpan.FromSeconds(1), CancellationToken.None, accept: DecisionParser.LooksUsable);
            Assert.StartsWith("b/", reply.Model);
            var again = await client.CompleteAsync("s", "u", true, TimeSpan.FromSeconds(1), CancellationToken.None, accept: DecisionParser.LooksUsable);
            Assert.StartsWith("b/", again.Model);
            Assert.Single(mock.RequestedModels, m => m.StartsWith("a/", StringComparison.Ordinal));
        }

        [Fact]
        public async Task TheFastestProvenModelIsAskedFirst()
        {
            using var mock = new MockOpenRouter { ModelsJson = TwoModels, Responder = (m, s, u) => GoodJson };
            mock.ModelDelay["a/one-it:free"] = TimeSpan.FromMilliseconds(400);
            using var client = NewClient(mock);

            // Both get measured once; from then on the quicker one takes the traffic.
            await client.CompleteWithModelAsync("a/one-it:free", "s", "u", TimeSpan.FromSeconds(1), CancellationToken.None);
            await client.CompleteWithModelAsync("b/two:free", "s", "u", TimeSpan.FromSeconds(1), CancellationToken.None);
            for (var i = 0; i < 3; i++)
            {
                Assert.StartsWith("b/", (await client.CompleteAsync("s", "u", true, TimeSpan.FromSeconds(1), CancellationToken.None)).Model);
            }
        }

        [Fact]
        public async Task CalibrationFindsWorkingModelsAndReportsThem()
        {
            using var mock = new MockOpenRouter { ModelsJson = TwoModels };
            mock.Responder = (model, s, u) => model.StartsWith("a/", StringComparison.Ordinal) ? "no idea" : GoodJson;
            using var client = NewClient(mock);

            var (summary, usable) = await client.CalibrateAsync(CancellationToken.None);
            _output.WriteLine(summary);
            Assert.True(usable);
            Assert.Contains("1 of 2", summary);
            Assert.Contains("b/two:free", summary);
            Assert.Contains(client.DescribeModels(), line => line.StartsWith("b/two:free", StringComparison.Ordinal) && line.Contains("1 ok"));
        }

        [Fact]
        public async Task CalibrationSaysSoWhenTheKeyIsRejected()
        {
            using var mock = new MockOpenRouter();
            mock.Failure = (n, m) => (401, "{\"error\":{\"message\":\"No auth\"}}");
            using var client = NewClient(mock);
            var (summary, usable) = await client.CalibrateAsync(CancellationToken.None);
            Assert.False(usable);
            Assert.Contains("rejected", summary);
        }

        [Fact]
        public async Task NoNetworkStopsAfterOneRequest()
        {
            var config = new LlmBotsConfig { OpenRouterBaseUrl = "http://127.0.0.1:1/api/v1", RequestTimeoutSeconds = 5, MaxRequestsPerMinute = 100 };
            using var client = new OpenRouterClient(config, NullLogger.Instance, "test-key");
            await Assert.ThrowsAsync<LlmUnavailableException>(() => client.CompleteAsync("s", "u", true, TimeSpan.FromSeconds(1), CancellationToken.None));

            // Every other model would fail the same way, so the call gave up after the first one.
            Assert.Equal(1, client.Budget.Total);
        }

        [Fact]
        public async Task UnreadableResponseMovesOnToTheNextModel()
        {
            using var mock = new MockOpenRouter { ModelsJson = TwoModels, Responder = (m, s, u) => GoodJson };
            mock.Failure = (n, model) => model.StartsWith("a/", StringComparison.Ordinal) ? (200, "<html>Bad gateway</html>") : null;
            using var client = NewClient(mock);
            var reply = await client.CompleteAsync("s", "u", true, TimeSpan.FromSeconds(1), CancellationToken.None);
            Assert.StartsWith("b/", reply.Model);
        }

        [Fact]
        public async Task BrainGoesToAnotherModelWhenOneIgnoresTheFormat()
        {
            using var mock = new MockOpenRouter { ModelsJson = TwoModels };
            mock.Responder = (model, s, u) => model.StartsWith("a/", StringComparison.Ordinal) ? "Sure, happy to help with that!" : GoodJson;
            using var client = NewClient(mock);
            var brain = new LlmBrain(client, new HeuristicBrain(1), NullLogger.Instance);
            var decision = await brain.DecideAsync(Context(), CancellationToken.None);
            Assert.StartsWith("llm:b/", decision.Source);
            Assert.Equal("Cleo", decision.Vote);
        }
    }
}
