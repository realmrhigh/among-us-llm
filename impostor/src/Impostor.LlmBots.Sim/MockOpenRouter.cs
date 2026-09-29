using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Impostor.LlmBots.Sim
{
    /// <summary>
    ///     A tiny stand-in for OpenRouter (OpenAI compatible chat API) for tests and offline demos.
    /// </summary>
    internal sealed class MockOpenRouter : IDisposable
    {
        // A minimal HTTP/1.1 server on a plain TCP socket: HttpListener needs URL reservations (admin rights) on Windows.
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _loop;
        private int _calls;

        public MockOpenRouter()
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            BaseUrl = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/api/v1";

            _loop = Task.Run(LoopAsync);
        }

        public string BaseUrl { get; }

        public ConcurrentQueue<string> Models { get; } = new();

        public ConcurrentBag<string> RequestedModels { get; } = new();

        public ConcurrentBag<string> Prompts { get; } = new();

        /// <summary>Gets the raw JSON bodies of the chat requests, in arrival order.</summary>
        public ConcurrentQueue<string> Bodies { get; } = new();

        /// <summary>Gets extra delays per model id, added to <see cref="Delay"/>.</summary>
        public ConcurrentDictionary<string, TimeSpan> ModelDelay { get; } = new();

        public int Calls => _calls;

        /// <summary>Gets or sets the answer generator: (model, system, user) to content, or null to fail.</summary>
        public Func<string, string, string, string?>? Responder { get; set; }

        /// <summary>Gets or sets a hook that may return an HTTP status to fail a request with.</summary>
        public Func<int, string, (int Status, string Body)?>? Failure { get; set; }

        public TimeSpan Delay { get; set; } = TimeSpan.Zero;

        public string? ModelsJson { get; set; }

        public static string DefaultAnswer(string model, string system, string user)
        {
            // Pick the first alive player that is not us and blame nobody in particular.
            var lines = user.Split('\n');
            var me = lines.FirstOrDefault(l => l.StartsWith("YOU ARE:", StringComparison.Ordinal)) ?? string.Empty;
            var names = lines.Where(l => l.StartsWith("- ", StringComparison.Ordinal) && l.Contains(" (") && !l.Contains("[DEAD]") && !l.Contains("[you]") && l.Contains(')') && !l.Contains(" ago"))
                .Select(l => l.Substring(2, l.IndexOf(" (", StringComparison.Ordinal) - 2))
                .ToList();
            var target = names.Count > 0 ? names[user.Length % names.Count] : "skip";
            var say = user.Contains("nobody has spoken yet", StringComparison.Ordinal) ? "hey, anyone see anything?" : $"i think {target} is acting off";
            return JsonSerializer.Serialize(new { say = new[] { say }, vote = user.Length % 3 == 0 ? "skip" : target, note = "mock " + me.Length });
        }

        public void Dispose()
        {
            _cts.Cancel();
            try
            {
                _listener.Stop();
            }
            catch (SocketException)
            {
            }
        }

        private static string Reason(int status) => status switch
        {
            200 => "OK",
            400 => "Bad Request",
            401 => "Unauthorized",
            402 => "Payment Required",
            403 => "Forbidden",
            404 => "Not Found",
            429 => "Too Many Requests",
            500 => "Internal Server Error",
            503 => "Service Unavailable",
            _ => "Status",
        };

        private async Task LoopAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_cts.Token);
                }
                catch
                {
                    break;
                }

                _ = Task.Run(() => ServeAsync(client));
            }
        }

        private async Task ServeAsync(TcpClient client)
        {
            using (client)
            {
                try
                {
                    var stream = client.GetStream();
                    var request = await ReadRequestAsync(stream);
                    if (request == null)
                    {
                        return;
                    }

                    var (status, body) = await RouteAsync(request.Value.Method, request.Value.Path, request.Value.Body);
                    var bytes = Encoding.UTF8.GetBytes(body);
                    var head = $"HTTP/1.1 {status} {Reason(status)}\r\nContent-Type: application/json\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n";
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(head));
                    await stream.WriteAsync(bytes);
                    await stream.FlushAsync();
                    client.Client.Shutdown(SocketShutdown.Send);
                }
                catch
                {
                    // The client is gone.
                }
            }
        }

        private static async Task<(string Method, string Path, string Body)?> ReadRequestAsync(NetworkStream stream)
        {
            var data = new List<byte>(4096);
            var chunk = new byte[4096];
            var headerEnd = -1;
            while (headerEnd < 0)
            {
                var read = await stream.ReadAsync(chunk);
                if (read == 0)
                {
                    return null;
                }

                data.AddRange(chunk.AsSpan(0, read).ToArray());
                headerEnd = IndexOfHeaderEnd(data);
            }

            var lines = Encoding.ASCII.GetString(data.GetRange(0, headerEnd).ToArray()).Split("\r\n");
            var start = lines[0].Split(' ');
            var length = 0;
            foreach (var line in lines.Skip(1))
            {
                var colon = line.IndexOf(':');
                if (colon > 0 && line.Substring(0, colon).Trim().Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                {
                    int.TryParse(line.Substring(colon + 1).Trim(), out length);
                }
            }

            var bodyStart = headerEnd + 4;
            while (data.Count - bodyStart < length)
            {
                var read = await stream.ReadAsync(chunk);
                if (read == 0)
                {
                    break;
                }

                data.AddRange(chunk.AsSpan(0, read).ToArray());
            }

            var body = Encoding.UTF8.GetString(data.GetRange(bodyStart, Math.Min(length, data.Count - bodyStart)).ToArray());
            return (start[0], start.Length > 1 ? start[1] : "/", body);
        }

        private static int IndexOfHeaderEnd(List<byte> data)
        {
            for (var i = 0; i + 3 < data.Count; i++)
            {
                if (data[i] == '\r' && data[i + 1] == '\n' && data[i + 2] == '\r' && data[i + 3] == '\n')
                {
                    return i;
                }
            }

            return -1;
        }

        private async Task<(int Status, string Body)> RouteAsync(string method, string rawPath, string body)
        {
            var path = rawPath.Split('?')[0];
            if (method == "GET" && path.EndsWith("/models", StringComparison.Ordinal))
            {
                return (200, ModelsJson ?? "{\"data\":[{\"id\":\"mock/free-model:free\",\"context_length\":32000,\"architecture\":{\"modality\":\"text->text\"},\"pricing\":{\"prompt\":\"0\",\"completion\":\"0\"}}]}");
            }

            if (method == "POST" && path.EndsWith("/chat/completions", StringComparison.Ordinal))
            {
                using var doc = JsonDocument.Parse(body);
                var model = doc.RootElement.GetProperty("model").GetString() ?? string.Empty;
                var messages = doc.RootElement.GetProperty("messages").EnumerateArray().ToList();
                var system = messages[0].GetProperty("content").GetString() ?? string.Empty;
                var user = messages[^1].GetProperty("content").GetString() ?? string.Empty;
                var call = Interlocked.Increment(ref _calls);
                RequestedModels.Add(model);
                Prompts.Add(user);
                Bodies.Enqueue(body);

                var delay = Delay + (ModelDelay.TryGetValue(model, out var extra) ? extra : TimeSpan.Zero);
                if (delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay);
                }

                var failure = Failure?.Invoke(call, model);
                if (failure != null)
                {
                    return (failure.Value.Status, failure.Value.Body);
                }

                var content = (Responder ?? DefaultAnswer).Invoke(model, system, user);
                if (content == null)
                {
                    return (500, "{\"error\":{\"message\":\"boom\"}}");
                }

                return (200, JsonSerializer.Serialize(new
                {
                    id = "mock",
                    model,
                    choices = new[] { new { index = 0, message = new { role = "assistant", content } } },
                }));
            }

            return (404, "{}");
        }
    }
}
