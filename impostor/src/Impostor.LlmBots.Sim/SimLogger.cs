using System;
using Microsoft.Extensions.Logging;

namespace Impostor.LlmBots.Sim
{
    public sealed class SimLoggerProvider : ILoggerProvider
    {
        private readonly Action<string> _sink;
        private readonly LogLevel _min;

        public SimLoggerProvider(Action<string> sink, LogLevel min)
        {
            _sink = sink;
            _min = min;
        }

        public ILogger CreateLogger(string categoryName) => new SimLogger(categoryName, _sink, _min);

        public void Dispose()
        {
        }

        private sealed class SimLogger : ILogger
        {
            private readonly string _category;
            private readonly Action<string> _sink;
            private readonly LogLevel _min;

            public SimLogger(string category, Action<string> sink, LogLevel min)
            {
                var idx = category.LastIndexOf('.');
                _category = idx >= 0 ? category[(idx + 1)..] : category;
                _sink = sink;
                _min = min;
            }

            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => logLevel >= _min;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (!IsEnabled(logLevel))
                {
                    return;
                }

                var text = $"{DateTime.Now:HH:mm:ss.fff} {logLevel.ToString()[..3].ToUpperInvariant()} {_category}: {formatter(state, exception)}";
                if (exception != null)
                {
                    text += Environment.NewLine + exception;
                }

                try
                {
                    _sink(text);
                }
                catch
                {
                    // Test output may already be closed.
                }
            }
        }
    }
}
