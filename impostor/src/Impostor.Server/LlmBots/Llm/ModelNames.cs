using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Impostor.Server.LlmBots.Llm
{
    /// <summary>
    ///     Turns model ids like "nvidia/nemotron-3-super-120b-a12b:free" into short player names (at most 10 characters)
    ///     such as "Nemotron", so a bot's name tells which language model is behind it.
    /// </summary>
    internal static class ModelNames
    {
        private const int MaxLength = 10;

        private static readonly HashSet<string> Filler = new(StringComparer.OrdinalIgnoreCase)
        {
            "it", "instruct", "chat", "preview", "free", "base", "exp", "latest", "omni", "reasoning", "thinking",
        };

        private static readonly Regex SizeToken = new(@"^\d+(\.\d+)?b$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>
        ///     Picks a name for each model. Models of the same family get names that tell the variants apart, and no name is
        ///     used twice or matches one in <paramref name="taken"/>.
        /// </summary>
        public static IReadOnlyList<string> Assign(IReadOnlyList<string> models, ISet<string> taken)
        {
            var families = models.Select(Family).ToList();
            var names = new List<string>();
            var used = new HashSet<string>(taken, StringComparer.OrdinalIgnoreCase);

            for (var i = 0; i < models.Count; i++)
            {
                var sharedFamily = families.Count(f => f == families[i]) > 1;
                var name = sharedFamily ? WithVariant(models[i], families[i]) : families[i];
                names.Add(Unique(name, used));
            }

            return names;
        }

        public static string Family(string model)
        {
            var token = Tokens(model).FirstOrDefault() ?? "bot";
            var letters = Regex.Match(token, "^[A-Za-z]+").Value;
            var family = letters.Length >= 2 ? letters : token;
            return Capitalize(family.Length > MaxLength ? family[..MaxLength] : family);
        }

        private static string WithVariant(string model, string family)
        {
            var rest = Tokens(model).Skip(1).ToList();
            var word = rest.FirstOrDefault(t => t.Any(char.IsLetter) && !Filler.Contains(t) && !SizeToken.IsMatch(t) && !Regex.IsMatch(t, @"^a\d+b$", RegexOptions.IgnoreCase));
            if (word != null)
            {
                var head = family.Length > 4 ? family[..4] : family;
                return Clip(head + Capitalize(word));
            }

            var size = rest.FirstOrDefault(t => SizeToken.IsMatch(t));
            if (size != null)
            {
                var room = Math.Max(2, MaxLength - size.Length);
                return Clip((family.Length > room ? family[..room] : family) + size.ToLowerInvariant());
            }

            return family;
        }

        private static IEnumerable<string> Tokens(string model)
        {
            var name = model;
            var slash = name.LastIndexOf('/');
            if (slash >= 0)
            {
                name = name[(slash + 1)..];
            }

            var colon = name.IndexOf(':');
            if (colon >= 0)
            {
                name = name[..colon];
            }

            return name.Split(new[] { '-', '_', ' ' }, StringSplitOptions.RemoveEmptyEntries);
        }

        private static string Capitalize(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

        private static string Clip(string text) => text.Length > MaxLength ? text[..MaxLength] : text;

        private static string Unique(string name, ISet<string> used)
        {
            var candidate = Clip(name);
            for (var n = 2; used.Contains(candidate); n++)
            {
                var suffix = n.ToString();
                candidate = Clip(name).Length + suffix.Length > MaxLength ? Clip(name)[..(MaxLength - suffix.Length)] + suffix : Clip(name) + suffix;
            }

            used.Add(candidate);
            return candidate;
        }
    }
}
