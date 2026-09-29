using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Impostor.Server.LlmBots.Llm
{
    /// <summary>
    ///     A sliding one minute window shared by every bot, so a lobby full of bots never goes over the request
    ///     limit of the provider. Important requests (votes) may use the last few slots, chatter may not.
    /// </summary>
    internal sealed class RequestBudget
    {
        private readonly object _lock = new();
        private readonly Queue<DateTime> _stamps = new();
        private readonly int _perMinute;
        private readonly int _perRun;
        private readonly Func<DateTime> _now;
        private int _total;
        private DateTime _blockedUntil = DateTime.MinValue;

        public RequestBudget(int perMinute, int perRun, Func<DateTime>? now = null)
        {
            _perMinute = Math.Max(1, perMinute);
            _perRun = perRun;
            _now = now ?? (() => DateTime.UtcNow);
        }

        public int Total
        {
            get
            {
                lock (_lock)
                {
                    return _total;
                }
            }
        }

        public bool IsBlocked
        {
            get
            {
                lock (_lock)
                {
                    return _blockedUntil > _now();
                }
            }
        }

        /// <summary>
        ///     Refuses any request until the given moment, used when the provider says the limit was hit.
        /// </summary>
        /// <param name="until">Moment the block ends.</param>
        public void BlockUntil(DateTime until)
        {
            lock (_lock)
            {
                if (until > _blockedUntil)
                {
                    _blockedUntil = until;
                }
            }
        }

        /// <summary>
        ///     Waits for a free slot for at most <paramref name="maxWait"/>.
        /// </summary>
        /// <param name="important">Whether the request may use the slots reserved for votes.</param>
        /// <param name="maxWait">The longest time to wait.</param>
        /// <param name="ct">Cancellation.</param>
        /// <returns>True when a slot was taken.</returns>
        public async Task<bool> TryAcquireAsync(bool important, TimeSpan maxWait, CancellationToken ct)
        {
            var give = _now() + maxWait;
            while (true)
            {
                TimeSpan wait;
                lock (_lock)
                {
                    var now = _now();
                    if (_blockedUntil > now)
                    {
                        if (_blockedUntil > give)
                        {
                            return false;
                        }

                        wait = _blockedUntil - now;
                    }
                    else if (_perRun > 0 && _total >= _perRun)
                    {
                        return false;
                    }
                    else
                    {
                        while (_stamps.Count > 0 && now - _stamps.Peek() >= TimeSpan.FromMinutes(1))
                        {
                            _stamps.Dequeue();
                        }

                        var reserve = important ? 0 : Math.Min(2, _perMinute / 4);
                        if (_stamps.Count < _perMinute - reserve)
                        {
                            _stamps.Enqueue(now);
                            _total++;
                            return true;
                        }

                        wait = _stamps.Peek() + TimeSpan.FromMinutes(1) - now;
                        if (now + wait > give)
                        {
                            return false;
                        }
                    }
                }

                await Task.Delay(wait > TimeSpan.FromMilliseconds(250) ? TimeSpan.FromMilliseconds(250) : (wait < TimeSpan.FromMilliseconds(20) ? TimeSpan.FromMilliseconds(20) : wait), ct);
            }
        }
    }
}
