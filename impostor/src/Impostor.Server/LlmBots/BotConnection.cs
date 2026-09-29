using System;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Impostor.Api.Net;
using Impostor.Hazel.Abstractions;

namespace Impostor.Server.LlmBots
{
    /// <summary>
    ///     An in-process connection. Everything the server "sends over the network" to a bot ends up in <see cref="Inbox"/>.
    /// </summary>
    internal sealed class BotConnection : IHazelConnection
    {
        private static int _addressCounter;

        private readonly Channel<byte[]> _inbox = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions
        {
            SingleReader = true,
        });

        public BotConnection()
        {
            // Every bot gets its own private address so an anti cheat ban of a bot never hits a human on 127.0.0.1.
            var n = Interlocked.Increment(ref _addressCounter);
            EndPoint = new IPEndPoint(new IPAddress(new byte[] { 10, 254, (byte)((n >> 8) & 0xFF), (byte)(n & 0xFF) }), 22023);
            IsConnected = true;
        }

        public ChannelReader<byte[]> Inbox => _inbox.Reader;

        public IPEndPoint EndPoint { get; }

        public bool IsConnected { get; private set; }

        public IClient? Client { get; set; }

        public float AveragePing => 0;

        /// <summary>
        ///     Gets or sets a value indicating whether the server drops (instead of punishing) messages the anti cheat rejects.
        ///     Bots are exempt; the scripted test host is not, so tests stay strict about its behaviour.
        /// </summary>
        public bool ExemptFromAntiCheat { get; set; } = true;

        public string? DisconnectReason { get; private set; }

        public ValueTask SendAsync(IMessageWriter writer)
        {
            // The writer is pooled by the caller, copy the payload right now.
            if (IsConnected)
            {
                _inbox.Writer.TryWrite(writer.ToByteArray(false));
            }

            return default;
        }

        public ValueTask DisconnectAsync(string? reason, IMessageWriter? writer = null)
        {
            if (IsConnected)
            {
                IsConnected = false;
                DisconnectReason = reason ?? "disconnected";
                if (writer != null)
                {
                    // The server explains a disconnect in a text inside the message, keep it for the logs.
                    var bytes = writer.ToByteArray(false);
                    var text = new string(bytes.Select(b => b >= 32 && b < 127 ? (char)b : ' ').ToArray());
                    DisconnectReason += ": " + string.Join(' ', text.Split(' ', StringSplitOptions.RemoveEmptyEntries));
                }

                _inbox.Writer.TryComplete();
            }

            return default;
        }

        public void Close()
        {
            IsConnected = false;
            _inbox.Writer.TryComplete();
        }
    }
}
