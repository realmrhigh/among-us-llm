using System;
using System.Threading;
using System.Threading.Tasks;
using Impostor.Server.LlmBots.Llm;
using Microsoft.Extensions.Logging;

namespace Impostor.Server.LlmBots.Brain
{
    /// <summary>
    ///     Lets a language model play the meetings. Anything that goes wrong (no key, limits, bad answers)
    ///     falls back to <see cref="HeuristicBrain"/> so a bot never freezes.
    /// </summary>
    internal sealed class LlmBrain : IBrain
    {
        private readonly OpenRouterClient _client;
        private readonly HeuristicBrain _fallback;
        private readonly ILogger _logger;
        private readonly Action<string>? _rawLog;

        public LlmBrain(OpenRouterClient client, HeuristicBrain fallback, ILogger logger, Action<string>? rawLog = null)
        {
            _client = client;
            _fallback = fallback;
            _logger = logger;
            _rawLog = rawLog;
        }

        public string Name => "llm";

        public async ValueTask<MeetingDecision> DecideAsync(MeetingContext ctx, CancellationToken cancellationToken)
        {
            if (!_client.Available)
            {
                return _fallback.Decide(ctx) with { Source = "heuristic (llm unavailable)" };
            }

            var user = PromptBuilder.User(ctx);
            var important = ctx.Stage == MeetingStage.FinalVote;
            var maxWait = ctx.Stage switch
            {
                MeetingStage.Opening => TimeSpan.FromSeconds(6),
                MeetingStage.Reply => TimeSpan.FromSeconds(4),
                _ => TimeSpan.FromSeconds(12),
            };

            try
            {
                var reply = await _client.CompleteAsync(PromptBuilder.SystemPrompt, user, important, maxWait, cancellationToken, accept: DecisionParser.LooksUsable, preferredModel: ctx.PreferredModel);
                _rawLog?.Invoke($"=== {ctx.Me.Name} {ctx.Stage} via {reply.Model} ({reply.LatencyMs} ms)\n--- prompt\n{user}\n--- answer\n{reply.Text}\n");

                var parsed = DecisionParser.Parse(reply.Text, ctx, "llm:" + reply.Model);
                if (parsed == null)
                {
                    _logger.LogWarning("Could not understand the answer of {Model}, using heuristics for this step", reply.Model);
                    return _fallback.Decide(ctx) with { Source = "heuristic (unparsable llm answer)" };
                }

                if (ctx.Stage == MeetingStage.FinalVote && parsed.Vote == null)
                {
                    parsed = parsed with { Vote = _fallback.Decide(ctx).Vote };
                }

                return parsed;
            }
            catch (LlmUnavailableException ex)
            {
                _logger.LogDebug("LLM unavailable: {Reason}", ex.Message);
                return _fallback.Decide(ctx) with { Source = "heuristic (" + ex.Message + ")" };
            }
        }
    }
}
