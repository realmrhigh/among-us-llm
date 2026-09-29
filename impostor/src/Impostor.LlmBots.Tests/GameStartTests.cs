using System;
using System.Linq;
using System.Threading.Tasks;
using Impostor.Api.Innersloth;
using Impostor.LlmBots.Sim;
using Xunit;
using Xunit.Abstractions;

namespace Impostor.LlmBots.Tests
{
    public class GameStartTests
    {
        private readonly ITestOutputHelper _output;

        public GameStartTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        public async Task GameStartsWithRolesAndTasks()
        {
            await using var sim = await SimGame.CreateAsync(4, log: _output.WriteLine);
            Assert.True(await sim.WaitForLobbyAsync());
            await Task.Delay(500);

            sim.Host.RequestStart();
            Assert.True(await sim.WaitUntilAsync(() => sim.Game.GameState == GameStates.Started, TimeSpan.FromSeconds(10)), "game should start");

            var infos = sim.Game.GameNet.GameData.Players.Values.ToList();
            Assert.Equal(5, infos.Count);
            Assert.True(await sim.WaitUntilAsync(() => infos.All(i => i.Tasks.Count == 4), TimeSpan.FromSeconds(5)), "every player should get 4 tasks");
            Assert.Single(infos, i => i.IsImpostor);
            Assert.All(infos, i => Assert.Equal(4, i.Tasks.Count));
            Assert.All(infos, i => Assert.All(i.Tasks, t => Assert.NotNull(t.Task)));
        }
    }
}
