using System;
using System.Linq;
using System.Threading.Tasks;
using Impostor.Server.LlmBots;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Impostor.LlmBots.Sim
{
    /// <summary>
    ///     The real server host (same wiring as the production executable, including HTTP), reachable in-process.
    /// </summary>
    internal sealed class RealServer : ISimServer
    {
        private RealServer(IHost host, int port)
        {
            Host = host;
            Port = port;
            Env = host.Services.GetRequiredService<BotEnvironment>();
        }

        public IHost Host { get; }

        public int Port { get; }

        public BotEnvironment Env { get; }

        internal BotManager Manager => Host.Services.GetRequiredService<BotManager>();

        /// <summary>
        ///     Asks the operating system for a TCP port nobody uses at the moment (tests may run side by side).
        /// </summary>
        /// <returns>A free port.</returns>
        public static int FreePort()
        {
            var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            listener.Start();
            var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }

        public static Task<RealServer> StartAsync(params string[] extraArgs) => StartAsync(FreePort(), extraArgs);

        public static async Task<RealServer> StartAsync(int port, params string[] extraArgs)
        {
            var args = new[]
            {
                $"--HttpServer:ListenIp=127.0.0.1",
                $"--HttpServer:ListenPort={port}",
                $"--Server:ListenIp=127.0.0.1",
                $"--Server:ListenPort={port}",
                $"--Server:PublicIp=127.0.0.1",
                $"--Server:PublicPort={port}",
                "--LlmBots:BrainMode=Heuristic",
                "--LlmBots:WriteTranscripts=false",
            }.Concat(extraArgs).Append("--errors-only").ToArray();

            var host = Impostor.Server.Program.CreateHostBuilder(args).Build();
            await host.StartAsync();
            return new RealServer(host, port);
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await Host.StopAsync(TimeSpan.FromSeconds(5));
            }
            catch
            {
                // Best effort.
            }

            Host.Dispose();
        }
    }
}
