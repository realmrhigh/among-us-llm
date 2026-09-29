using System;
using System.Linq;
using System.Threading.Tasks;
using Impostor.Api.Innersloth;
using Impostor.LlmBots.Sim;
using Xunit;
using Xunit.Abstractions;

namespace Impostor.LlmBots.Tests
{
    public class LobbyFlowTests
    {
        private readonly ITestOutputHelper _output;

        public LobbyFlowTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        public async Task BotsJoinSpawnAndGetIdentity()
        {
            await using var sim = await SimGame.CreateAsync(3, log: _output.WriteLine, level: Microsoft.Extensions.Logging.LogLevel.Debug);

            Assert.True(await sim.WaitForLobbyAsync(), "everyone should join and spawn");

            var infos = sim.Game.GameNet.GameData.Players.Values.ToList();
            Assert.Equal(4, infos.Count);

            var ok = await sim.WaitUntilAsync(() => infos.All(i => i.PlayerName.Length > 0 && (byte)i.CurrentOutfit.Color < 18), TimeSpan.FromSeconds(5));
            foreach (var i in infos)
            {
                _output.WriteLine($"info {i.PlayerId} '{i.PlayerName}' color {(int)i.CurrentOutfit.Color}");
            }

            Assert.True(ok);
            Assert.Equal(4, infos.Select(i => i.CurrentOutfit.Color).Distinct().Count());
            Assert.Equal(4, infos.Select(i => i.PlayerName).Distinct().Count());
        }

        [Fact]
        public async Task BotsShuffleAroundSlightlyInTheLobby()
        {
            await using var sim = await SimGame.CreateAsync(3, log: _output.WriteLine);
            Assert.True(await sim.WaitForLobbyAsync());

            System.Numerics.Vector2 Pos(Impostor.Server.LlmBots.BotAgent b) => b.Client.Me!.NetworkTransform.Position;
            var start = sim.Bots.ToDictionary(b => b, Pos);
            var moved = await sim.WaitUntilAsync(() => sim.Bots.Any(b => System.Numerics.Vector2.Distance(Pos(b), start[b]) > 0.05f), TimeSpan.FromSeconds(20));
            Assert.True(moved, "at least one bot should move a little");
            Assert.All(sim.Bots, b => Assert.True(System.Numerics.Vector2.Distance(Pos(b), start[b]) < 1.6f, "bots must stay close to where they spawned"));
        }
    }
}
