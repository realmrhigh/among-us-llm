using System;
using System.Numerics;
using System.Threading.Tasks;
using Impostor.Api.Innersloth;
using Impostor.Server.Net.State;

namespace Impostor.Server.LlmBots
{
    /// <summary>
    ///     Waiting in the lobby: a bit of shuffling around the spot the host put the character on, so bots do not
    ///     stand frozen next to the humans.
    /// </summary>
    internal sealed partial class BotAgent
    {
        private const float LobbyRadius = 0.8f;

        private Vector2? _lobbyHome;
        private Vector2 _lobbyPos;
        private Vector2? _lobbyTarget;
        private DateTime _lobbyNextMove;
        private DateTime _lobbyLastStep = DateTime.UtcNow;

        private async ValueTask TickLobbyAsync(Game game, DateTime now)
        {
            var me = Client.Me;
            if (game.GameState != GameStates.NotStarted || me == null || ManagedByHost || !_env.Config.LobbyIdleMovement)
            {
                _lobbyHome = null;
                _lobbyTarget = null;
                return;
            }

            if (_lobbyHome == null)
            {
                _lobbyHome = me.NetworkTransform.Position;
                _lobbyPos = _lobbyHome.Value;
                _lobbyNextMove = now + TimeSpan.FromSeconds(2 + (_rng.NextDouble() * 6));
                _lobbyLastStep = now;
                return;
            }

            var dt = (float)Math.Min(0.5, (now - _lobbyLastStep).TotalSeconds);
            _lobbyLastStep = now;

            if (_lobbyTarget == null)
            {
                if (now >= _lobbyNextMove)
                {
                    var angle = _rng.NextDouble() * Math.PI * 2;
                    var radius = _rng.NextDouble() * LobbyRadius;
                    _lobbyTarget = _lobbyHome.Value + new Vector2((float)(Math.Cos(angle) * radius), (float)(Math.Sin(angle) * radius));
                }

                return;
            }

            var to = _lobbyTarget.Value - _lobbyPos;
            var step = 2.0f * dt;
            if (to.Length() <= step)
            {
                _lobbyPos = _lobbyTarget.Value;
                _lobbyTarget = null;
                _lobbyNextMove = now + TimeSpan.FromSeconds(3 + (_rng.NextDouble() * 9));
            }
            else
            {
                _lobbyPos += Vector2.Normalize(to) * step;
            }

            await Client.SendPositionAsync(_lobbyPos);
        }
    }
}
