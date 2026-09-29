using System;
using System.Threading.Tasks;
using Impostor.Api.Config;
using Impostor.Api.Events.Managers;
using Impostor.Api.Games;
using Impostor.Api.Games.Managers;
using Impostor.Api.Net.Custom;
using Impostor.Api.Net.Manager;
using Impostor.Api.Utils;
using Impostor.Hazel;
using Impostor.Hazel.Abstractions;
using Impostor.Hazel.Extensions;
using Impostor.Server;
using Impostor.Server.Events;
using Impostor.Server.LlmBots;
using Impostor.Server.Net;
using Impostor.Server.Net.Custom;
using Impostor.Server.Net.Factories;
using Impostor.Server.Net.Manager;
using Impostor.Server.Net.Messages;
using Impostor.Server.Net.State;
using Impostor.Server.Utils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Impostor.LlmBots.Sim
{
    /// <summary>
    ///     A complete Impostor game server without a network: clients are in-process.
    /// </summary>
    internal sealed class TestServer : ISimServer
    {
        private TestServer(ServiceProvider services)
        {
            Services = services;
            ClientManager = services.GetRequiredService<ClientManager>();
            GameManager = services.GetRequiredService<GameManager>();
            var hub = new GameEventHub(services.GetRequiredService<ILoggerFactory>().CreateLogger<GameEventHub>());
            services.GetRequiredService<IEventManager>().RegisterListener(hub);
            Env = new BotEnvironment(
                ClientManager,
                GameManager,
                services.GetRequiredService<IEventManager>(),
                services.GetRequiredService<IDateTimeProvider>(),
                services.GetRequiredService<Microsoft.Extensions.ObjectPool.ObjectPool<MessageReader>>(),
                services.GetRequiredService<ILoggerFactory>(),
                services.GetRequiredService<IOptions<LlmBotsConfig>>().Value,
                hub);
        }

        internal ServiceProvider Services { get; }

        internal ClientManager ClientManager { get; }

        internal GameManager GameManager { get; }

        public BotEnvironment Env { get; }

        public static TestServer Create(Action<string>? sink = null, LogLevel level = LogLevel.Information, Action<LlmBotsConfig>? configureBots = null, Action<AntiCheatConfig>? configureAntiCheat = null)
        {
            var services = new ServiceCollection();

            services.AddSingleton<ServerEnvironment>();
            services.AddSingleton<IServerEnvironment>(p => p.GetRequiredService<ServerEnvironment>());
            services.AddSingleton<IDateTimeProvider, Impostor.Server.Utils.RealDateTimeProvider>();

            services.Configure<DebugConfig>(_ => { });
            services.Configure<AntiCheatConfig>(c => configureAntiCheat?.Invoke(c));
            services.Configure<CompatibilityConfig>(_ => { });
            services.Configure<ServerConfig>(_ => { });
            services.Configure<TimeoutConfig>(_ => { });
            services.Configure<LlmBotsConfig>(c =>
            {
                c.WriteTranscripts = false;
                c.BrainMode = "Heuristic";
                configureBots?.Invoke(c);
            });

            services.AddSingleton<ICompatibilityManager, CompatibilityManager>();
            services.AddSingleton<ClientManager>();
            services.AddSingleton<IClientManager>(p => p.GetRequiredService<ClientManager>());
            services.AddSingleton<IClientFactory, ClientFactory<Client>>();

            services.AddSingleton<GameManager>();
            services.AddSingleton<IGameManager>(p => p.GetRequiredService<GameManager>());

            services.AddEventPools();
            services.AddHazel();
            services.AddSingleton<ICustomMessageManager<ICustomRootMessage>, CustomMessageManager<ICustomRootMessage>>();
            services.AddSingleton<ICustomMessageManager<ICustomRpc>, CustomMessageManager<ICustomRpc>>();
            services.AddSingleton<IMessageWriterProvider, MessageWriterProvider>();
            services.AddSingleton<IGameCodeFactory, GameCodeFactory>();
            services.AddSingleton<IEventManager, EventManager>();

            services.AddLogging(b =>
            {
                b.ClearProviders();
                b.SetMinimumLevel(level);
                b.AddProvider(new SimLoggerProvider(sink ?? Console.WriteLine, level));
            });

            return new TestServer(services.BuildServiceProvider());
        }

        internal Game? FindGame(GameCode code) => GameManager.Find(code);

        public ValueTask DisposeAsync()
        {
            return Services.DisposeAsync();
        }
    }
}
