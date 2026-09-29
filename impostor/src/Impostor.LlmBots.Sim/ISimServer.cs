using System;
using Impostor.Server.LlmBots;

namespace Impostor.LlmBots.Sim
{
    /// <summary>
    ///     A server that scripted hosts and bots can connect to in-process.
    /// </summary>
    internal interface ISimServer : IAsyncDisposable
    {
        BotEnvironment Env { get; }
    }
}
