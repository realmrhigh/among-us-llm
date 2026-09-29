using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading.Tasks;
using Impostor.Api;
using Impostor.Api.Games;
using Impostor.Api.Innersloth;
using Impostor.Api.Innersloth.Customization;
using Impostor.Api.Net;
using Impostor.Api.Net.Inner;
using Impostor.Api.Net.Messages;
using Impostor.Api.Net.Messages.Rpcs;
using Impostor.Hazel;
using Impostor.Hazel.Abstractions;
using Impostor.Server.Net;
using Impostor.Server.Net.Inner;
using Impostor.Server.Net.Inner.Objects;
using Impostor.Server.Net.State;
using Microsoft.Extensions.Logging;

namespace Impostor.Server.LlmBots
{
    internal enum BotEventKind
    {
        Joined,
        WaitForHost,
        GameStarted,
        GameEnded,
        Removed,
        Kicked,
        Disconnected,
        HostedGame,
        SceneChanged,
        ClientReady,
        Rpc,
        Spawned,
        Despawned,
    }

    internal readonly record struct BotEvent(BotEventKind Kind, int A = 0, int B = 0, string? Text = null, byte[]? Data = null);

    /// <summary>
    ///     The protocol side of a bot: an in-process fake game client. It sends the same messages a real client
    ///     would send, straight into <see cref="Client.HandleMessageAsync"/>, so every server side validation still runs.
    /// </summary>
    internal sealed class BotClient
    {
        private readonly BotEnvironment _env;
        private readonly ILogger _logger;
        private readonly BotConnection _connection = new();
        private readonly System.Threading.SemaphoreSlim _sendLock = new(1, 1);
        private ClientBase? _client;
        private bool _disconnectReported;
        private ushort _systemSequence;

        public BotClient(BotEnvironment env, string name)
        {
            _env = env;
            Name = name;
            _logger = env.LoggerFactory.CreateLogger($"Bot[{name}]");
        }

        public string Name { get; }

        public BotConnection Connection => _connection;

        public ClientBase? Client => _client;

        public int ClientId => _client?.Id ?? 0;

        public bool IsConnected => _connection.IsConnected;

        public GameCode? Code { get; private set; }

        public int HostId { get; private set; }

        public Game? Game => _client?.Player?.Game;

        public InnerPlayerControl? Me => _client?.Player?.Character;

        public InnerPlayerInfo? MyInfo => Me?.PlayerInfo;

        public bool AmHost => Game != null && Game.HostId == ClientId;

        public bool SceneSent { get; private set; }

        /// <summary>
        ///     Gets or sets a hook that sees every event, in addition to the list returned by <see cref="DrainInbox"/>.
        /// </summary>
        public Action<BotEvent>? OnEvent { get; set; }

        /// <summary>
        ///     Gets or sets a value indicating whether raw inbound packets are dumped to the logger.
        /// </summary>
        public bool DumpInbound { get; set; }

        public async ValueTask<bool> ConnectAsync(GameVersion version)
        {
            await _env.ClientManager.RegisterConnectionAsync(
                _connection,
                Name,
                version,
                Language.English,
                QuickChatModes.FreeChatOrQuickChat,
                new PlatformSpecificData(Platforms.StandaloneSteamPC, "LLM Bot"));

            _client = _connection.Client as ClientBase;
            return _client != null && _connection.IsConnected;
        }

        public async ValueTask JoinAsync(GameCode code)
        {
            Code = code;
            SceneSent = false;

            await SendRootAsync(writer =>
            {
                writer.StartMessage(MessageFlags.JoinGame);
                writer.Write(code.Value);
                writer.Write(false);
                writer.EndMessage();
            });
        }

        public async ValueTask LeaveAsync(string reason = "The remote sent a disconnect request")
        {
            if (_client != null)
            {
                await _client.HandleDisconnectAsync(reason);
            }

            _connection.Close();
        }

        /// <summary>
        ///     Reads everything the server sent to this bot since the last call.
        /// </summary>
        public List<BotEvent> DrainInbox()
        {
            var events = new List<BotEvent>();

            while (_connection.Inbox.TryRead(out var bytes))
            {
                ParseInbound(bytes, events);
            }

            if (!_connection.IsConnected && _connection.DisconnectReason != null && !_disconnectReported)
            {
                _disconnectReported = true;
                events.Add(new BotEvent(BotEventKind.Disconnected, Text: _connection.DisconnectReason));
            }

            if (OnEvent != null)
            {
                foreach (var evt in events)
                {
                    OnEvent(evt);
                }
            }

            return events;
        }

        public async ValueTask SendSceneChangeAsync()
        {
            SceneSent = true;

            // Impostor hands out player ids while handling this message; two of them at the same instant can collide.
            await _env.JoinGate.WaitAsync();
            try
            {
                await SendGameDataAsync(
                    w =>
                    {
                        w.StartMessage(GameDataTag.SceneChangeFlag);
                        w.WritePacked(ClientId);
                        w.Write("OnlineGame");
                        w.EndMessage();
                    });
            }
            finally
            {
                _env.JoinGate.Release();
            }
        }

        public ValueTask SendReadyAsync()
        {
            return SendGameDataAsync(
                w =>
                {
                    w.StartMessage(GameDataTag.ReadyFlag);
                    w.WritePacked(ClientId);
                    w.EndMessage();
                });
        }

        public ValueTask SendRpcAsync(uint netId, RpcCalls call, Action<IMessageWriter>? payload = null, bool toHost = false)
        {
            return SendGameDataAsync(
                w =>
                {
                    w.StartMessage(GameDataTag.RpcFlag);
                    w.WritePacked(netId);
                    w.Write((byte)call);
                    payload?.Invoke(w);
                    w.EndMessage();
                },
                toHost ? HostId : null);
        }

        public async ValueTask SetupIdentityAsync(ColorType color, uint level)
        {
            var me = Me;
            if (me == null)
            {
                return;
            }

            await SendRpcAsync(me.NetId, RpcCalls.CheckName, w => w.Write(Name), toHost: true);
            await SendRpcAsync(me.NetId, RpcCalls.CheckColor, w => w.Write((byte)color), toHost: true);
            await SendRpcAsync(me.NetId, RpcCalls.SetLevel, w => w.WritePacked(level));
            await SendRpcAsync(me.NetId, RpcCalls.SetHatStr, w => { w.Write("hat_NoHat"); w.Write((byte)5); });
            await SendRpcAsync(me.NetId, RpcCalls.SetSkinStr, w => { w.Write("skin_None"); w.Write((byte)5); });
            await SendRpcAsync(me.NetId, RpcCalls.SetVisorStr, w => { w.Write("visor_EmptyVisor"); w.Write((byte)5); });
            await SendRpcAsync(me.NetId, RpcCalls.SetPetStr, w => { w.Write("pet_EmptyPet"); w.Write((byte)5); });
            await SendRpcAsync(me.NetId, RpcCalls.SetNamePlateStr, w => { w.Write("nameplate_NoPlate"); w.Write((byte)5); });
        }

        /// <summary>
        ///     Sends a position update (or a batch of them) for our own character.
        /// </summary>
        public ValueTask SendPositionAsync(Vector2 position)
        {
            var me = Me;
            if (me == null)
            {
                return default;
            }

            var seq = (ushort)(me.NetworkTransform.LastSequenceId + 1);
            return SendGameDataAsync(
                w =>
                {
                    w.StartMessage(GameDataTag.DataFlag);
                    w.WritePacked(me.NetworkTransform.NetId);
                    w.Write(seq);
                    w.WritePacked(1);
                    w.Write(position);
                    w.EndMessage();
                });
        }

        public ValueTask SnapToAsync(Vector2 position)
        {
            var me = Me;
            if (me == null)
            {
                return default;
            }

            var minSid = (ushort)(me.NetworkTransform.LastSequenceId + 5);
            return SendRpcAsync(
                me.NetworkTransform.NetId,
                RpcCalls.SnapTo,
                w =>
                {
                    w.Write(position);
                    w.Write(minSid);
                });
        }

        public ValueTask SendChatAsync(string text)
        {
            var me = Me;
            return me == null ? default : SendRpcAsync(me.NetId, RpcCalls.SendChat, w => w.Write(text));
        }

        public ValueTask CompleteTaskAsync(uint taskIndex)
        {
            var me = Me;
            return me == null ? default : SendRpcAsync(me.NetId, RpcCalls.CompleteTask, w => w.WritePacked(taskIndex));
        }

        public ValueTask CheckMurderAsync(InnerPlayerControl target)
        {
            var me = Me;
            return me == null ? default : SendRpcAsync(me.NetId, RpcCalls.CheckMurder, w => w.WritePacked(target.NetId));
        }

        public ValueTask ReportBodyAsync(byte deadPlayerId)
        {
            var me = Me;
            return me == null ? default : SendRpcAsync(me.NetId, RpcCalls.ReportDeadBody, w => w.Write(deadPlayerId));
        }

        /// <summary>
        ///     Repairs (or presses a console of) a sabotaged system. The host handles the request.
        /// </summary>
        /// <param name="system">The system being repaired.</param>
        /// <param name="amount">System specific: for reactor and O2 the low bits are the console and 0x40 means "used".</param>
        /// <returns>A task that finishes when the message was sent.</returns>
        public ValueTask SendUpdateSystemAsync(SystemTypes system, byte amount)
        {
            var me = Me;
            var ship = Game?.GameNet.ShipStatus;
            if (me == null || ship == null)
            {
                return default;
            }

            var seq = ++_systemSequence;
            return SendRpcAsync(ship.NetId, RpcCalls.UpdateSystem, w => Rpc35UpdateSystem.Serialize(w, system, me, seq, amount, 0), toHost: true);
        }

        public ValueTask EnterVentAsync(int ventId)
        {
            var me = Me;
            return me == null ? default : SendRpcAsync(me.Physics.NetId, RpcCalls.EnterVent, w => w.WritePacked(ventId));
        }

        public ValueTask ExitVentAsync(int ventId)
        {
            var me = Me;
            return me == null ? default : SendRpcAsync(me.Physics.NetId, RpcCalls.ExitVent, w => w.WritePacked(ventId));
        }

        /// <summary>
        ///     Casts a vote in the running meeting. <paramref name="suspect"/> is a player id, or 253 to skip.
        /// </summary>
        public async ValueTask CastVoteAsync(byte suspect)
        {
            var me = Me;
            var meeting = Game?.ActiveMeeting;
            if (me == null || meeting == null)
            {
                return;
            }

            await SendRpcAsync(
                meeting.NetId,
                RpcCalls.CastVote,
                w =>
                {
                    w.Write(me.PlayerId);
                    w.Write(suspect);
                },
                toHost: !AmHost);

            if (AmHost)
            {
                // A host records its own vote by updating the meeting object, everybody else sends the RPC above.
                await SendGameDataAsync(w =>
                {
                    w.StartMessage(GameDataTag.DataFlag);
                    w.WritePacked(meeting.NetId);
                    w.WritePacked(1u);
                    w.StartMessage(me.PlayerId);
                    w.Write(suspect);
                    w.Write(false);
                    w.EndMessage();
                    w.WritePacked(0);
                    w.EndMessage();
                });
            }
        }

        public ValueTask SendGameDataAsync(Action<IMessageWriter> inner, int? toClient = null)
        {
            var code = Code;
            if (code == null)
            {
                return default;
            }

            return SendRootAsync(writer =>
            {
                if (toClient == null)
                {
                    writer.StartMessage(MessageFlags.GameData);
                    writer.Write(code.Value.Value);
                }
                else
                {
                    writer.StartMessage(MessageFlags.GameDataTo);
                    writer.Write(code.Value.Value);
                    writer.WritePacked(toClient.Value);
                }

                inner(writer);
                writer.EndMessage();
            });
        }

        public async ValueTask SendRootAsync(Action<IMessageWriter> build)
        {
            if (_client == null || !_connection.IsConnected)
            {
                return;
            }

            await _sendLock.WaitAsync();
            try
            {
                using var writer = MessageWriter.Get(MessageType.Reliable);
                build(writer);
                var bytes = writer.ToByteArray(false);

                using var reader = _env.ReaderPool.Get();
                reader.Update(bytes);

                while (reader.Position < reader.Length)
                {
                    using var message = reader.ReadMessage();
                    await _client.HandleMessageAsync(message, MessageType.Reliable);
                }
            }
            finally
            {
                _sendLock.Release();
            }
        }

        private void ParseInbound(byte[] bytes, List<BotEvent> events)
        {
            using var reader = _env.ReaderPool.Get();
            reader.Update(bytes);

            while (reader.Position < reader.Length)
            {
                using var message = reader.ReadMessage();
                try
                {
                    if (DumpInbound)
                    {
                        _logger.LogInformation("<< tag {Tag} len {Len}: {Hex}", message.Tag, message.Length, Convert.ToHexString(message.Buffer, message.Offset, Math.Min(message.Length, 64)));
                    }

                    switch (message.Tag)
                    {
                        case MessageFlags.HostGame:
                            events.Add(new BotEvent(BotEventKind.HostedGame, message.ReadInt32()));
                            break;

                        case MessageFlags.GameData:
                        case MessageFlags.GameDataTo:
                            ParseGameData(message, events);
                            break;

                        case MessageFlags.JoinedGame:
                        {
                            var code = message.ReadInt32();
                            var clientId = message.ReadInt32();
                            HostId = message.ReadInt32();
                            Code = GameCode.From(code);
                            events.Add(new BotEvent(BotEventKind.Joined, clientId, HostId));
                            break;
                        }

                        case MessageFlags.JoinGame:
                        {
                            // Another player joined; the host may have changed.
                            message.ReadInt32();
                            message.ReadInt32();
                            HostId = message.ReadInt32();
                            break;
                        }

                        case MessageFlags.WaitForHost:
                            events.Add(new BotEvent(BotEventKind.WaitForHost));
                            break;

                        case MessageFlags.StartGame:
                            events.Add(new BotEvent(BotEventKind.GameStarted));
                            break;

                        case MessageFlags.EndGame:
                        {
                            message.ReadInt32();
                            events.Add(new BotEvent(BotEventKind.GameEnded, message.ReadByte()));
                            break;
                        }

                        case MessageFlags.RemovePlayer:
                        {
                            message.ReadInt32();
                            var playerId = message.ReadInt32();
                            HostId = message.ReadInt32();
                            var reason = message.ReadByte();
                            events.Add(new BotEvent(BotEventKind.Removed, playerId, reason));
                            break;
                        }

                        case MessageFlags.KickPlayer:
                        {
                            message.ReadInt32();
                            var playerId = message.ReadPackedInt32();
                            events.Add(new BotEvent(BotEventKind.Kicked, playerId));
                            break;
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to parse inbound message {Tag}", message.Tag);
                }
            }
        }

        private void ParseGameData(IMessageReader message, List<BotEvent> events)
        {
            message.ReadInt32(); // game code
            if (message.Tag == MessageFlags.GameDataTo)
            {
                message.ReadPackedInt32(); // recipient, that is us
            }

            while (message.Position < message.Length)
            {
                using var sub = message.ReadMessage();
                switch (sub.Tag)
                {
                    case GameDataTag.SceneChangeFlag:
                    {
                        var clientId = sub.ReadPackedInt32();
                        events.Add(new BotEvent(BotEventKind.SceneChanged, clientId, Text: sub.ReadString()));
                        break;
                    }

                    case GameDataTag.ReadyFlag:
                        events.Add(new BotEvent(BotEventKind.ClientReady, sub.ReadPackedInt32()));
                        break;

                    case GameDataTag.RpcFlag:
                    {
                        var netId = sub.ReadPackedUInt32();
                        var call = sub.ReadByte();
                        var rest = sub.Length - sub.Position;
                        var data = new byte[rest];
                        Array.Copy(sub.Buffer, sub.Offset + sub.Position, data, 0, rest);
                        events.Add(new BotEvent(BotEventKind.Rpc, (int)netId, call, Data: data));
                        break;
                    }

                    case GameDataTag.SpawnFlag:
                        events.Add(new BotEvent(BotEventKind.Spawned, (int)sub.ReadPackedUInt32()));
                        break;

                    case GameDataTag.DespawnFlag:
                        events.Add(new BotEvent(BotEventKind.Despawned, (int)sub.ReadPackedUInt32()));
                        break;
                }
            }
        }
    }
}
