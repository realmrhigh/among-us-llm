using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Impostor.Api.Innersloth;
using Impostor.Api.Innersloth.GameOptions;
using Impostor.Server.LlmBots;
using Impostor.Server.Net.State;
using Microsoft.Extensions.Logging;

namespace Impostor.LlmBots.Sim
{
    /// <summary>
    ///     A whole game in one process: a scripted host plus a number of bots, all connected to an in-process server.
    /// </summary>
    internal sealed class SimGame : IAsyncDisposable
    {
        private readonly CancellationTokenSource _cts = new();
        private readonly List<Task> _tasks = new();

        private SimGame(ISimServer server, ScriptedHost host, BotAgent hostAgent)
        {
            Server = server;
            Host = host;
            HostAgent = hostAgent;
        }

        public ISimServer Server { get; }

        public ScriptedHost Host { get; }

        public BotAgent HostAgent { get; }

        public List<BotAgent> Bots { get; } = new();

        public IEnumerable<BotAgent> AllAgents => new[] { HostAgent }.Concat(Bots);

        public Game Game => Host.Game!;

        public static async Task<SimGame> CreateAsync(
            int botCount,
            Action<NormalGameOptions>? configureOptions = null,
            Action<LlmBotsConfig>? configureBots = null,
            int seed = 1,
            Action<string>? log = null,
            LogLevel level = LogLevel.Information,
            Func<BotAgent, Impostor.Server.LlmBots.Brain.IBrain>? brainFactory = null,
            ISimServer? existingServer = null,
            Action<ScriptedHost>? configureHost = null)
        {
            var server = existingServer ?? TestServer.Create(log, level, c =>
            {
                c.TimeScale = 6;
                c.MeetingAnimationSeconds = 0.3;
                c.EndScreenSeconds = 0.5;
                c.PostMeetingFreezeSeconds = 0.3;
                configureBots?.Invoke(c);
            });

            var options = new NormalGameOptions
            {
                Map = MapTypes.Skeld,
                NumImpostors = 1,
                DiscussionTime = 1,
                VotingTime = 8,
                NumCommonTasks = 1,
                NumLongTasks = 1,
                NumShortTasks = 2,
                MaxPlayers = 10,
            };
            configureOptions?.Invoke(options);

            var host = new ScriptedHost(server.Env, options, "Host", seed);
            host.Client.DumpInbound = Environment.GetEnvironmentVariable("SIM_DUMP") == "1";
            var hostAgent = new BotAgent(server.Env, host.Client, seed * 1000);
            if (brainFactory != null)
            {
                hostAgent.Brain = brainFactory(hostAgent);
            }

            configureHost?.Invoke(host);
            hostAgent.ManagedByHost = true;
            hostAgent.Transcript = log;
            host.AgentEvent = hostAgent.HandleEventAsync;
            host.AgentTick = hostAgent.TickAsync;

            var sim = new SimGame(server, host, hostAgent);

            await host.CreateLobbyAsync();
            sim._tasks.Add(Task.Run(() => host.RunAsync(sim._cts.Token)));

            var names = new[] { "Ada", "Bolt", "Cleo", "Dax", "Echo", "Fable", "Gizmo", "Hex", "Iris" };
            for (var i = 0; i < botCount; i++)
            {
                var client = new BotClient(server.Env, names[i % names.Length]);
                var agent = new BotAgent(server.Env, client, seed * 1000 + i + 1);
                if (brainFactory != null)
                {
                    agent.Brain = brainFactory(agent);
                }

                agent.Transcript = log;
                sim.Bots.Add(agent);
                if (!await client.ConnectAsync(host.Game!.Host!.Client.GameVersion))
                {
                    throw new InvalidOperationException("Bot could not connect");
                }

                await client.JoinAsync(host.Code);
                sim._tasks.Add(Task.Run(() => agent.RunAsync(sim._cts.Token)));
            }

            return sim;
        }

        public async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
        {
            var until = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < until)
            {
                if (condition())
                {
                    return true;
                }

                await Task.Delay(50);
            }

            return condition();
        }

        public Task<bool> WaitForLobbyAsync(TimeSpan? timeout = null)
        {
            var total = Bots.Count + 1;
            return WaitUntilAsync(
                () => Host.Game != null && Host.Game.PlayerCount == total && Host.Game.Players.All(p => p.Character?.PlayerInfo != null),
                timeout ?? TimeSpan.FromSeconds(10));
        }

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            try
            {
                await Task.WhenAll(_tasks).WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch
            {
                // Shutting down.
            }

            await Server.DisposeAsync();
        }
    }
}
