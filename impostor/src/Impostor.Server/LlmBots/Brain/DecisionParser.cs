using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Impostor.Server.LlmBots.Brain
{
    /// <summary>
    ///     Turns whatever a language model answered into a <see cref="MeetingDecision"/> that is safe to act on.
    /// </summary>
    internal static class DecisionParser
    {
        private static readonly Regex Confession = new(@"\b(i am|i'm|im|as an?|i was) (the |an? )?(impostor|imposter)\b|\bmy (impostor|imposter) (team|partner|mate)|\bwe('re| are) (both )?(the )?(impostors|imposters)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex SayKey = new("\"(?:say|lines|messages|message|chat|text)\"\\s*:\\s*", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex VoteKey = new("\"(?:vote|vote_for|final_vote|target)\"\\s*:\\s*", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex MetaTalk = new(@"\b(language model|as an ai|i am an ai|i'm an ai|artificial intelligence|chatgpt|openai|anthropic|system prompt|json)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static MeetingDecision? Parse(string text, MeetingContext ctx, string source)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            var json = ExtractJson(text);
            List<string> say = new();
            string? vote = null;

            if (json != null)
            {
                try
                {
                    using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
                    var root = doc.RootElement;
                    if (root.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var name in new[] { "say", "lines", "messages", "message", "chat", "text" })
                        {
                            if (TryGet(root, name, out var value))
                            {
                                say.AddRange(ReadLines(value));
                                break;
                            }
                        }

                        foreach (var name in new[] { "vote", "vote_for", "target", "final_vote" })
                        {
                            if (TryGet(root, name, out var value) && value.ValueKind == JsonValueKind.String)
                            {
                                vote = value.GetString();
                                break;
                            }
                        }
                    }
                }
                catch (JsonException)
                {
                    json = null;
                }
            }

            if (json == null)
            {
                var salvaged = Salvage(text);
                if (salvaged != null)
                {
                    say.AddRange(salvaged.Value.Say);
                    vote = salvaged.Value.Vote;
                }
                else
                {
                    // No usable JSON: treat a short plain answer as one chat line.
                    var plain = text.Trim();
                    if (plain.Length is > 0 and <= 160 && !plain.Contains('{') && !plain.Contains('['))
                    {
                        say.Add(plain);
                    }
                    else
                    {
                        return null;
                    }
                }
            }

            var cleaned = say
                .Select(l => Clean(l, ctx))
                .Where(l => l.Length > 0 && !MetaTalk.IsMatch(l) && !(ctx.IAmImpostor && Confession.IsMatch(l)))
                .Take(ctx.MaxLines)
                .ToList();

            return new MeetingDecision(cleaned, NormalizeVote(vote, ctx), source);
        }

        /// <summary>
        ///     Tells whether an answer has the shape that was asked for: a JSON object, or at least recognisable
        ///     "say" and "vote" fields. Plain prose does not count, it may come from a model that ignored the task.
        /// </summary>
        /// <param name="text">The answer.</param>
        /// <returns>True when <see cref="Parse"/> will find the fields in it.</returns>
        internal static bool LooksUsable(string text)
        {
            return !string.IsNullOrWhiteSpace(text) && (ExtractJson(text) != null || Salvage(text) != null);
        }

        /// <summary>
        ///     Pulls the chat lines and the vote out of an answer that is not valid JSON (cut off, stray quotes,
        ///     missing brackets), which small models produce now and then.
        /// </summary>
        /// <param name="text">The answer.</param>
        /// <returns>What was found, or null when there is nothing that looks like a "say" or "vote" field.</returns>
        internal static (List<string> Say, string? Vote)? Salvage(string text)
        {
            var say = new List<string>();
            string? vote = null;
            var found = false;

            var key = SayKey.Match(text);
            if (key.Success)
            {
                found = true;
                var i = key.Index + key.Length;
                if (i < text.Length && text[i] == '[')
                {
                    i++;
                    while (i < text.Length && text[i] != ']')
                    {
                        if (text[i] == '"')
                        {
                            var line = ReadString(text, ref i, out var closed);
                            if (closed && line.Length > 0)
                            {
                                say.Add(line);
                            }

                            continue;
                        }

                        i++;
                    }
                }
                else if (i < text.Length && text[i] == '"')
                {
                    var line = ReadString(text, ref i, out var closed);
                    if (closed && line.Length > 0)
                    {
                        say.Add(line);
                    }
                }
            }

            var voteMatch = VoteKey.Match(text);
            if (voteMatch.Success)
            {
                found = true;
                var i = voteMatch.Index + voteMatch.Length;
                if (i < text.Length && text[i] == '"')
                {
                    vote = ReadString(text, ref i, out _);
                }
            }

            return found ? (say, vote) : null;
        }

        // Reads a string literal starting at the opening quote. A quote only ends the string when what follows looks
        // like the end of a JSON value, so an unescaped quote inside a sentence does not cut the line short.
        private static string ReadString(string text, ref int i, out bool closed)
        {
            var sb = new StringBuilder();
            closed = false;
            for (i++; i < text.Length; i++)
            {
                var c = text[i];
                if (c == '\\' && i + 1 < text.Length)
                {
                    i++;
                    switch (text[i])
                    {
                        case 'n':
                        case 't':
                        case 'r':
                            sb.Append(' ');
                            break;
                        case 'u' when i + 4 < text.Length && int.TryParse(text.AsSpan(i + 1, 4), System.Globalization.NumberStyles.HexNumber, null, out var code):
                            sb.Append((char)code);
                            i += 4;
                            break;
                        default:
                            sb.Append(text[i]);
                            break;
                    }

                    continue;
                }

                if (c == '"')
                {
                    var next = i + 1;
                    while (next < text.Length && char.IsWhiteSpace(text[next]))
                    {
                        next++;
                    }

                    if (next >= text.Length || text[next] is ',' or ']' or '}' or ':')
                    {
                        closed = true;
                        i++;
                        return sb.ToString().Trim();
                    }
                }

                sb.Append(c);
            }

            return sb.ToString().Trim();
        }

        internal static string? ExtractJson(string text)
        {
            var start = text.IndexOf('{');
            while (start >= 0)
            {
                var depth = 0;
                var inString = false;
                var escaped = false;
                for (var i = start; i < text.Length; i++)
                {
                    var c = text[i];
                    if (inString)
                    {
                        if (escaped)
                        {
                            escaped = false;
                        }
                        else if (c == '\\')
                        {
                            escaped = true;
                        }
                        else if (c == '"')
                        {
                            inString = false;
                        }

                        continue;
                    }

                    if (c == '"')
                    {
                        inString = true;
                    }
                    else if (c == '{')
                    {
                        depth++;
                    }
                    else if (c == '}')
                    {
                        depth--;
                        if (depth == 0)
                        {
                            var candidate = text.Substring(start, i - start + 1);
                            if (candidate.Contains("\"say\"", StringComparison.Ordinal) || candidate.Contains("\"vote\"", StringComparison.Ordinal))
                            {
                                return candidate;
                            }

                            break;
                        }
                    }
                }

                start = text.IndexOf('{', start + 1);
            }

            return null;
        }

        private static bool TryGet(JsonElement root, string name, out JsonElement value)
        {
            foreach (var property in root.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }

            value = default;
            return false;
        }

        private static IEnumerable<string> ReadLines(JsonElement value)
        {
            if (value.ValueKind == JsonValueKind.String)
            {
                yield return value.GetString() ?? string.Empty;
            }
            else if (value.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in value.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String)
                    {
                        yield return item.GetString() ?? string.Empty;
                    }
                }
            }
        }

        private static string Clean(string line, MeetingContext ctx)
        {
            var text = line.Replace("\r", " ").Replace("\n", " ").Trim().Trim('"', '\'', '`');

            // Models like to prefix their own name.
            var prefix = ctx.Me.Name + ":";
            if (text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                text = text.Substring(prefix.Length).Trim();
            }

            var builder = new StringBuilder();
            foreach (var c in text)
            {
                if (c >= ' ' && c != '\u007f')
                {
                    builder.Append(c);
                }
            }

            text = builder.ToString().Trim();
            if (text.Length > 100)
            {
                var cut = text.LastIndexOf(' ', 99);
                text = text.Substring(0, cut > 40 ? cut : 100);
            }

            return text;
        }

        private static string? NormalizeVote(string? vote, MeetingContext ctx)
        {
            if (string.IsNullOrWhiteSpace(vote))
            {
                return null;
            }

            var t = vote.Trim().Trim('"', '\'', '.', '!');
            if (t.Equals("skip", StringComparison.OrdinalIgnoreCase) || t.Equals("none", StringComparison.OrdinalIgnoreCase) || t.StartsWith("skip", StringComparison.OrdinalIgnoreCase))
            {
                return "skip";
            }

            var player = ctx.FindByName(t);
            if (player == null || player.IsMe || !player.Alive || player.IsTeammate)
            {
                return null;
            }

            return player.Name;
        }
    }
}
