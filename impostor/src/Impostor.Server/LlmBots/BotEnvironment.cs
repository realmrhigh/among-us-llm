using Impostor.Api.Events.Managers;
using Impostor.Api.Utils;
using Impostor.Hazel;
using Impostor.Server.Net.Manager;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.ObjectPool;

namespace Impostor.Server.LlmBots
{
    /// <summary>
    ///     Shared services every bot needs to talk to the server.
    /// </summary>
    internal sealed class BotEnvironment
    {
        public BotEnvironment(
            ClientManager clientManager,
            GameManager gameManager,
            IEventManager eventManager,
            IDateTimeProvider clock,
            ObjectPool<MessageReader> readerPool,
            ILoggerFactory loggerFactory,
            LlmBotsConfig config,
            GameEventHub hub)
        {
            ClientManager = clientManager;
            GameManager = gameManager;
            EventManager = eventManager;
            Clock = clock;
            ReaderPool = readerPool;
            LoggerFactory = loggerFactory;
            Config = config;
            Hub = hub;
        }

        public ClientManager ClientManager { get; }

        public GameManager GameManager { get; }

        public IEventManager EventManager { get; }

        public IDateTimeProvider Clock { get; }

        public ObjectPool<MessageReader> ReaderPool { get; }

        public ILoggerFactory LoggerFactory { get; }

        public LlmBotsConfig Config { get; }

        public GameEventHub Hub { get; }

        /// <summary>
        ///     Gets the gate that serializes the scene change handshake of bots. Impostor assigns player ids while
        ///     handling that message and is not safe against several clients doing so in the same instant.
        /// </summary>
        public System.Threading.SemaphoreSlim JoinGate { get; } = new(1, 1);
    }
}
