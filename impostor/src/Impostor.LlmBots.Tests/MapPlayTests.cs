using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Impostor.Api.Innersloth;
using Impostor.LlmBots.Sim;
using Xunit;
using Xunit.Abstractions;

namespace Impostor.LlmBots.Tests
{
    public class MapPlayTests
    {
        private readonly ITestOutputHelper _output;

        public MapPlayTests(ITestOutputHelper output)
        {
            _output = output;
        }

        public static IEnumerable<object[]> Maps => Enum.GetValues<MapTypes>().Where(m => m != MapTypes.Skeld).Select(m => new object[] { m });

        [Theory]
        [MemberData(nameof(Maps))]
        public async Task CrewCanFinishTheirTasksOnEveryMap(MapTypes map)
        {
            await using var sim = await SimGame.CreateAsync(
                4,
                o =>
                {
                    o.Map = map;
                    o.KillCooldown = 600;
                    o.NumCommonTasks = 1;
                    o.NumLongTasks = 1;
                    o.NumShortTasks = 1;
                },
                log: _output.WriteLine,
                configureBots: c => c.TimeScale = 12);
            Assert.True(await sim.WaitForLobbyAsync());
            await Task.Delay(400);

            sim.Host.RequestStart();
            Assert.True(await sim.WaitUntilAsync(() => sim.Host.LastResult != null, TimeSpan.FromSeconds(120)), $"{map}: the game should end");
            Assert.Equal(GameOverReason.CrewmatesByTask, sim.Host.LastResult);
        }
    }
}
