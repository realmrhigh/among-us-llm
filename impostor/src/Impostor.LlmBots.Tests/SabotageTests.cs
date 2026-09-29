using System;
using System.Threading.Tasks;
using Impostor.Api.Innersloth;
using Impostor.LlmBots.Sim;
using Impostor.Server.Net.Inner.Objects.Systems.ShipStatus;
using Xunit;
using Xunit.Abstractions;

namespace Impostor.LlmBots.Tests
{
    public class SabotageTests
    {
        private readonly ITestOutputHelper _output;

        public SabotageTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Theory]
        [InlineData(SystemTypes.Reactor)]
        [InlineData(SystemTypes.LifeSupp)]
        public async Task CrewBotsFixTheSabotagesThatWouldEndTheGame(SystemTypes system)
        {
            await using var sim = await SimGame.CreateAsync(5, o => o.KillCooldown = 600, log: _output.WriteLine);
            Assert.True(await sim.WaitForLobbyAsync());
            await Task.Delay(400);

            sim.Host.RequestStart();
            Assert.True(await sim.WaitUntilAsync(() => sim.Game.GameState == GameStates.Started, TimeSpan.FromSeconds(10)));
            await Task.Delay(1500);

            sim.Host.StartSabotage(system, 90);
            bool Active()
            {
                var ship = sim.Game.GameNet.ShipStatus!;
                return ship.Systems[system] switch
                {
                    ReactorSystemType r => r.IsActive,
                    LifeSuppSystemType l => l.IsActive,
                    _ => false,
                };
            }

            Assert.True(await sim.WaitUntilAsync(Active, TimeSpan.FromSeconds(5)), "the host should have started the sabotage");
            Assert.True(await sim.WaitUntilAsync(() => sim.Host.SabotageFixed, TimeSpan.FromSeconds(60)), "bots should repair it");
            Assert.False(Active());
            Assert.NotEqual(GameOverReason.ImpostorsBySabotage, sim.Host.LastResult);
        }

        [Fact]
        public async Task ImpostorBotsStartSabotagesAndTheCrewFixesThem()
        {
            await using var sim = await SimGame.CreateAsync(
                5,
                o => o.KillCooldown = 600,
                c => c.ImpostorSabotageChance = 1,
                log: _output.WriteLine);
            Assert.True(await sim.WaitForLobbyAsync());
            await Task.Delay(400);

            sim.Host.RequestStart();

            // The first opportunity comes 45 game seconds (7.5 real seconds at this speed) after the start.
            Assert.True(await sim.WaitUntilAsync(() => sim.Host.SabotagesStarted >= 1, TimeSpan.FromSeconds(30)), "an impostor should sabotage");
            Assert.True(await sim.WaitUntilAsync(() => sim.Host.SabotageFixed, TimeSpan.FromSeconds(30)), "the crew should fix it");
            Assert.NotEqual(GameOverReason.ImpostorsBySabotage, sim.Host.LastResult);
        }
    }
}
