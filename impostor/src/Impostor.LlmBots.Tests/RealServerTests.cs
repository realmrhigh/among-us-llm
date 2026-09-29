using System;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Impostor.Api.Innersloth;
using Impostor.LlmBots.Sim;
using Xunit;
using Xunit.Abstractions;

namespace Impostor.LlmBots.Tests
{
    public class RealServerTests
    {
        private readonly ITestOutputHelper _output;

        public RealServerTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        public async Task ChatCommandAndHttpAddBotsAndAGameCanBePlayed()
        {
            var port = RealServer.FreePort();
            await using var server = await RealServer.StartAsync(port);
            server.Env.Config.TimeScale = 6;
            server.Env.Config.MeetingAnimationSeconds = 0.3;
            server.Env.Config.EndScreenSeconds = 0.5;
            server.Env.Config.PostMeetingFreezeSeconds = 0.3;

            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            var status = await http.GetStringAsync($"http://127.0.0.1:{port}/llmbots/status");
            _output.WriteLine(status);
            Assert.Contains("heuristic", status);
            Assert.Contains("<title>LLM bots</title>", await http.GetStringAsync($"http://127.0.0.1:{port}/llmbots"));

            await using var sim = await SimGame.CreateAsync(
                0,
                o =>
                {
                    o.KillCooldown = 3;
                    o.DiscussionTime = 2;
                    o.VotingTime = 12;
                },
                log: _output.WriteLine,
                existingServer: server);

            Assert.True(await sim.WaitUntilAsync(() => sim.Host.Game?.Players.All(p => p.Character != null) == true, TimeSpan.FromSeconds(5)));

            // The host types the command in the lobby chat, like a human would.
            await sim.Host.Client.SendChatAsync("!bots 3");
            Assert.True(await sim.WaitUntilAsync(() => sim.Game.PlayerCount == 4 && sim.Game.Players.All(p => p.Character?.PlayerInfo != null), TimeSpan.FromSeconds(15)), "3 bots should join through the chat command");

            // The host gets an answer in the chat that only it can see.
            Assert.True(await sim.WaitUntilAsync(() => sim.Host.ChatLog.Any(l => l.Contains("bots joining", StringComparison.OrdinalIgnoreCase) || l.Contains("bot joining", StringComparison.OrdinalIgnoreCase)), TimeSpan.FromSeconds(5)), "the host should be told how many bots are joining");

            // And one more through the HTTP API.
            var joined = await http.PostAsync($"http://127.0.0.1:{port}/llmbots/join?code={sim.Host.Code.Code}&count=1", null);
            _output.WriteLine(await joined.Content.ReadAsStringAsync());
            Assert.True(joined.IsSuccessStatusCode);
            Assert.True(await sim.WaitUntilAsync(() => sim.Game.PlayerCount == 5 && sim.Game.Players.All(p => p.Character?.PlayerInfo != null), TimeSpan.FromSeconds(15)), "the fifth player should join through HTTP");

            var denied = await http.GetAsync($"http://127.0.0.1:{port}/llmbots/join?code=NOPE&count=1");
            Assert.Equal(400, (int)denied.StatusCode);

            await Task.Delay(500);
            sim.Host.RequestStart();
            Assert.True(await sim.WaitUntilAsync(() => sim.Host.LastResult != null, TimeSpan.FromSeconds(120)), "the game should finish");
            _output.WriteLine("RESULT " + sim.Host.LastResult);

            var after = await http.GetStringAsync($"http://127.0.0.1:{port}/llmbots/status");
            _output.WriteLine(after);
            Assert.Contains("\"bots\"", after);
        }

        [Fact]
        public async Task CommandLineOverridesReachTheBotConfig()
        {
            await using var server = await RealServer.StartAsync("--LlmBots:BrainMode=Auto", "--LlmBots:MaxBots=3");
            Assert.Equal("Auto", server.Env.Config.BrainMode);
            Assert.Equal(3, server.Env.Config.MaxBots);
        }

        [Fact]
        public async Task ChatCommandsCanListAndRemoveBots()
        {
            await using var server = await RealServer.StartAsync();
            await using var sim = await SimGame.CreateAsync(0, log: _output.WriteLine, existingServer: server);
            Assert.True(await sim.WaitUntilAsync(() => sim.Host.Game?.Players.All(p => p.Character != null) == true, TimeSpan.FromSeconds(5)));

            await sim.Host.Client.SendChatAsync("!bots 2");
            Assert.True(await sim.WaitUntilAsync(() => sim.Game.PlayerCount == 3 && sim.Game.Players.All(p => p.Character?.PlayerInfo != null), TimeSpan.FromSeconds(15)));

            await sim.Host.Client.SendChatAsync("!bots status");
            Assert.True(await sim.WaitUntilAsync(() => sim.Host.ChatLog.Any(l => l.Contains("bots active")), TimeSpan.FromSeconds(5)), "status reply");

            await sim.Host.Client.SendChatAsync("!bots off");
            Assert.True(await sim.WaitUntilAsync(() => sim.Game.PlayerCount == 1, TimeSpan.FromSeconds(10)), "bots should leave");
            Assert.Equal(0, server.Manager.ActiveBots);

            // And they can come back.
            await sim.Host.Client.SendChatAsync("!bots 1");
            Assert.True(await sim.WaitUntilAsync(() => sim.Game.PlayerCount == 2 && sim.Game.Players.All(p => p.Character?.PlayerInfo != null), TimeSpan.FromSeconds(15)));
        }
    }
}
