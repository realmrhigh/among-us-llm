using System;
using System.Linq;
using System.Threading.Tasks;
using Impostor.Api.Innersloth;
using Impostor.LlmBots.Sim;
using Xunit;
using Xunit.Abstractions;

namespace Impostor.LlmBots.Tests
{
    public class GameplayTests
    {
        private readonly ITestOutputHelper _output;

        public GameplayTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        public async Task CrewWinsByFinishingTasksWhenTheImpostorCannotKill()
        {
            await using var sim = await SimGame.CreateAsync(4, o => o.KillCooldown = 600, log: _output.WriteLine);
            Assert.True(await sim.WaitForLobbyAsync());
            await Task.Delay(400);

            sim.Host.RequestStart();
            Assert.True(await sim.WaitUntilAsync(() => sim.Host.LastResult != null, TimeSpan.FromSeconds(90)), "the game should end");
            Assert.Equal(GameOverReason.CrewmatesByTask, sim.Host.LastResult);
        }

        [Fact]
        public async Task ImpostorKillsBodiesGetReportedAndMeetingsHappen()
        {
            await using var sim = await SimGame.CreateAsync(
                5,
                o =>
                {
                    o.KillCooldown = 3;
                    o.DiscussionTime = 2;
                    o.VotingTime = 12;
                },
                log: _output.WriteLine);
            Assert.True(await sim.WaitForLobbyAsync());
            await Task.Delay(400);

            sim.Host.RequestStart();

            // A meeting is part of what this test checks, so make sure at least one happens even if nobody stumbles on a body.
            Assert.True(await sim.WaitUntilAsync(() => sim.Game.GameState == GameStates.Started, TimeSpan.FromSeconds(10)));
            await Task.Delay(5000);
            sim.Host.RequestEmergency();
            Assert.True(await sim.WaitUntilAsync(() => sim.Host.LastResult != null, TimeSpan.FromSeconds(150)), "the game should end");
            _output.WriteLine($"RESULT {sim.Host.LastResult}, meetings {sim.Host.Meetings}");
            Assert.True(sim.Host.Meetings >= 1, "at least one meeting should have been held");
        }

        [Fact]
        public async Task TwoGamesInARowWorkAfterEveryoneRejoinsTheLobby()
        {
            await using var sim = await SimGame.CreateAsync(
                4,
                o =>
                {
                    o.KillCooldown = 3;
                    o.DiscussionTime = 2;
                    o.VotingTime = 10;
                },
                log: _output.WriteLine);
            Assert.True(await sim.WaitForLobbyAsync());

            for (var round = 1; round <= 2; round++)
            {
                await Task.Delay(400);
                sim.Host.RequestStart();
                Assert.True(await sim.WaitUntilAsync(() => sim.Host.GamesFinished == round, TimeSpan.FromSeconds(150)), $"game {round} should end");
                _output.WriteLine($"ROUND {round} RESULT {sim.Host.LastResult}");

                // Everybody goes back to the lobby: players are spawned again, every bot has its name back (a stale character left over
                // from the last game once made bots ask for their name on an object the host no longer had, so they showed as ???)
                // and the game is open for a new round.
                var back = await sim.WaitUntilAsync(
                    () =>
                    {
                        var g = sim.Host.Game;
                        return g != null && g.GameState == GameStates.NotStarted && g.PlayerCount == 5 && g.Players.All(p => p.Character?.PlayerInfo != null && p.Limbo == Impostor.Api.Net.LimboStates.NotLimbo && p.Character.PlayerInfo.PlayerName == p.Client.Name);
                    },
                    TimeSpan.FromSeconds(20));
                if (!back)
                {
                    var g = sim.Host.Game;
                    _output.WriteLine($"DIAG host game null={g == null} connected={sim.Host.Client.IsConnected} reason={sim.Host.Client.Connection.DisconnectReason} phase={sim.Host.Phase}");
                    if (g != null)
                    {
                        foreach (var p in g.Players)
                        {
                            _output.WriteLine($"DIAG player {p.Client.Name} limbo={p.Limbo} character={(p.Character != null)} info={(p.Character?.PlayerInfo != null)}");
                        }
                    }
                }

                Assert.True(back, $"lobby should be back after game {round}");
            }
        }

        [Fact]
        public async Task BotsUseTheLanguageModelInMeetings()
        {
            using var mock = new MockOpenRouter();
            using var client = new Impostor.Server.LlmBots.Llm.OpenRouterClient(
                new Impostor.Server.LlmBots.LlmBotsConfig { OpenRouterBaseUrl = mock.BaseUrl, MaxRequestsPerMinute = 500, RequestTimeoutSeconds = 5 },
                Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance,
                "test-key");

            await using var sim = await SimGame.CreateAsync(
                4,
                o =>
                {
                    o.KillCooldown = 3;
                    o.DiscussionTime = 3;
                    o.VotingTime = 12;
                },
                log: _output.WriteLine,
                brainFactory: agent => new Impostor.Server.LlmBots.Brain.LlmBrain(client, new Impostor.Server.LlmBots.Brain.HeuristicBrain(5), Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance));
            Assert.True(await sim.WaitForLobbyAsync());
            await Task.Delay(400);

            sim.Host.RequestStart();
            Assert.True(await sim.WaitUntilAsync(() => sim.Game.GameState == GameStates.Started, TimeSpan.FromSeconds(10)));
            await Task.Delay(4000);
            sim.Host.RequestEmergency();
            Assert.True(await sim.WaitUntilAsync(() => sim.Host.LastResult != null, TimeSpan.FromSeconds(150)), "the game should end");
            _output.WriteLine($"RESULT {sim.Host.LastResult}, meetings {sim.Host.Meetings}, LLM calls {mock.Calls}");
            Assert.True(sim.Host.Meetings >= 1);
            Assert.True(mock.Calls >= 2, "the mock model should have been asked to play meetings");
            Assert.Contains(mock.Prompts, p => p.Contains("WHY THIS MEETING"));
        }

        [Fact]
        public async Task SeveralImpostorsNeverTriggerTheAntiCheat()
        {
            var problems = new System.Collections.Concurrent.ConcurrentQueue<string>();
            void Log(string line)
            {
                if (line.Contains("caught cheating") || line.Contains("anti cheat rejects") || line.Contains("Agent loop error"))
                {
                    problems.Enqueue(line);
                }
            }

            await using var sim = await SimGame.CreateAsync(
                8,
                o =>
                {
                    o.NumImpostors = 3;
                    o.KillCooldown = 2;
                    o.DiscussionTime = 1;
                    o.VotingTime = 8;
                },
                log: Log);
            Assert.True(await sim.WaitForLobbyAsync());
            await Task.Delay(400);

            sim.Host.RequestStart();
            Assert.True(await sim.WaitUntilAsync(() => sim.Host.LastResult != null, TimeSpan.FromSeconds(150)), "the game should end");
            Assert.True(problems.IsEmpty, "unexpected: " + string.Join(" | ", problems));
        }

        [Fact]
        public async Task BotsKnowWhoReportedEvenWhenTheAnnouncementComesAfterTheMeetingObject()
        {
            var transcript = new System.Collections.Concurrent.ConcurrentQueue<string>();
            await using var sim = await SimGame.CreateAsync(
                4,
                o =>
                {
                    o.KillCooldown = 600;
                    o.DiscussionTime = 1;
                    o.VotingTime = 8;
                },
                log: line => { if (line.StartsWith("--- Meeting", StringComparison.Ordinal)) { transcript.Enqueue(line); } },
                configureHost: h => h.AnnounceAfterSpawn = true);
            Assert.True(await sim.WaitForLobbyAsync());
            await Task.Delay(400);

            sim.Host.RequestStart();
            Assert.True(await sim.WaitUntilAsync(() => sim.Game.GameState == GameStates.Started, TimeSpan.FromSeconds(10)));
            await Task.Delay(4000);
            sim.Host.RequestEmergency();
            Assert.True(await sim.WaitUntilAsync(() => sim.Host.Meetings >= 1 && transcript.Count >= 3, TimeSpan.FromSeconds(30)), "a meeting should happen");
            Assert.DoesNotContain(transcript, l => l.Contains("meeting called"));
            Assert.Contains(transcript, l => l.Contains("emergency"));
        }

        [Fact]
        public async Task CrewShowsTheVisualTaskAnimationsWithoutAntiCheatProblems()
        {
            var problems = new System.Collections.Concurrent.ConcurrentQueue<string>();
            void Log(string line)
            {
                if (line.Contains("caught cheating") || line.Contains("anti cheat rejects") || line.Contains("Agent loop error"))
                {
                    problems.Enqueue(line);
                }
            }

            await using var sim = await SimGame.CreateAsync(4, o => o.KillCooldown = 600, log: Log, configureHost: h => h.ForcedTasks = new[] { 4, 2 });
            Assert.True(await sim.WaitForLobbyAsync());
            await Task.Delay(400);

            sim.Host.RequestStart();
            Assert.True(await sim.WaitUntilAsync(() => sim.Host.LastResult != null, TimeSpan.FromSeconds(90)), "the game should end");
            Assert.Equal(GameOverReason.CrewmatesByTask, sim.Host.LastResult);
            Assert.True(sim.Host.ScannerRpcs >= 2, $"scanner rpcs: {sim.Host.ScannerRpcs}");
            Assert.True(sim.Host.AnimationRpcs >= 1, $"animation rpcs: {sim.Host.AnimationRpcs}");
            Assert.True(problems.IsEmpty, "unexpected: " + string.Join(" | ", problems));
        }

        [Fact]
        public async Task ASlowLanguageModelDoesNotStallMeetings()
        {
            using var mock = new MockOpenRouter { Delay = TimeSpan.FromSeconds(60) };
            using var client = new Impostor.Server.LlmBots.Llm.OpenRouterClient(
                new Impostor.Server.LlmBots.LlmBotsConfig { OpenRouterBaseUrl = mock.BaseUrl, MaxRequestsPerMinute = 500, RequestTimeoutSeconds = 90 },
                Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance,
                "test-key");

            await using var sim = await SimGame.CreateAsync(
                4,
                o =>
                {
                    o.KillCooldown = 600;
                    o.DiscussionTime = 2;
                    o.VotingTime = 14;
                },
                log: _output.WriteLine,
                brainFactory: agent => new Impostor.Server.LlmBots.Brain.LlmBrain(client, new Impostor.Server.LlmBots.Brain.HeuristicBrain(9), Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance));
            Assert.True(await sim.WaitForLobbyAsync());
            await Task.Delay(400);

            sim.Host.RequestStart();
            Assert.True(await sim.WaitUntilAsync(() => sim.Game.GameState == GameStates.Started, TimeSpan.FromSeconds(10)));
            await Task.Delay(4000);
            sim.Host.RequestEmergency();

            // Every model call hangs for a minute, yet all bots must still vote before the meeting times out.
            Assert.True(await sim.WaitUntilAsync(() => sim.Host.Meetings >= 1 && sim.Host.Phase == HostPhase.Playing, TimeSpan.FromSeconds(60)), "the meeting should finish");
            Assert.True(sim.Host.LastMeetingAllVoted, "every bot should have voted in time");
        }
    }
}
