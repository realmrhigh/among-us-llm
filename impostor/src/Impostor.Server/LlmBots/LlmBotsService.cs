using System;
using System.Threading;
using System.Threading.Tasks;
using Impostor.Api.Events.Managers;
using Impostor.Api.Utils;
using Impostor.Hazel;
using Impostor.Server.Net.Manager;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.ObjectPool;
using Microsoft.Extensions.Options;

namespace Impostor.Server.LlmBots
{
    internal static class LlmBotsServiceExtensions
    {
        public static void AddLlmBots(this IServiceCollection services)
        {
            services.AddSingleton<GameEventHub>();
            services.AddSingleton(sp => new BotEnvironment(
                sp.GetRequiredService<ClientManager>(),
                sp.GetRequiredService<GameManager>(),
                sp.GetRequiredService<IEventManager>(),
                sp.GetRequiredService<IDateTimeProvider>(),
                sp.GetRequiredService<ObjectPool<MessageReader>>(),
                sp.GetRequiredService<ILoggerFactory>(),
                sp.GetRequiredService<IOptions<LlmBotsConfig>>().Value,
                sp.GetRequiredService<GameEventHub>()));
            services.AddSingleton<BotManager>();
            services.AddHostedService<LlmBotsHostedService>();
        }
    }

    /// <summary>
    ///     Starts the bot subsystem together with the server.
    /// </summary>
    internal sealed class LlmBotsHostedService : IHostedService
    {
        private readonly LlmBotsConfig _config;
        private readonly GameEventHub _hub;
        private readonly BotManager _manager;
        private readonly IEventManager _events;
        private readonly ILogger<LlmBotsHostedService> _logger;
        private IDisposable? _registration;

        public LlmBotsHostedService(IOptions<LlmBotsConfig> config, GameEventHub hub, BotManager manager, IEventManager events, ILogger<LlmBotsHostedService> logger)
        {
            _config = config.Value;
            _hub = hub;
            _manager = manager;
            _events = events;
            _logger = logger;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            if (!_config.Enabled)
            {
                _logger.LogInformation("LLM bots are disabled (LlmBots:Enabled is false).");
                return Task.CompletedTask;
            }

            _registration = _events.RegisterListener(_hub);
            if (_config.ChatCommands)
            {
                _hub.ChatCommand = _manager.HandleChatCommandAsync;
            }

            _ = Task.Run(_manager.SelfCheckAsync);

            _logger.LogInformation(
                "LLM bots are ready. In a lobby you host, type \"!bots 4\" in the chat, or open http://localhost:22023/llmbots. Brain: {Brain}.",
                _manager.BrainDescription);
            return Task.CompletedTask;
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            await _manager.RemoveAsync(null);
            _manager.Dispose();
            _registration?.Dispose();
        }
    }
}
