using System;
using System.IO;

namespace Impostor.Server.LlmBots.Llm
{
    /// <summary>
    ///     Finds the API key: an environment variable first, then a .env file next to the server or in one of the
    ///     folders above it. Only the one configured variable is ever read.
    /// </summary>
    internal static class ApiKey
    {
        public static string? Find(string variable, string? startDirectory = null)
        {
            var fromEnvironment = Environment.GetEnvironmentVariable(variable);
            if (!string.IsNullOrWhiteSpace(fromEnvironment))
            {
                return fromEnvironment.Trim();
            }

            var starts = startDirectory != null
                ? new[] { startDirectory }
                : new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory };

            foreach (var start in starts)
            {
                var directory = new DirectoryInfo(start);
                for (var depth = 0; depth < 8 && directory != null; depth++, directory = directory.Parent)
                {
                    var file = Path.Combine(directory.FullName, ".env");
                    if (!File.Exists(file))
                    {
                        continue;
                    }

                    var value = ReadVariable(file, variable);
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        return value;
                    }
                }
            }

            return null;
        }

        public static string? ReadVariable(string file, string variable)
        {
            try
            {
                foreach (var raw in File.ReadLines(file))
                {
                    var line = raw.Trim();
                    if (line.Length == 0 || line[0] == '#')
                    {
                        continue;
                    }

                    if (line.StartsWith("export ", StringComparison.Ordinal))
                    {
                        line = line.Substring(7).TrimStart();
                    }

                    var eq = line.IndexOf('=');
                    if (eq <= 0 || !string.Equals(line.Substring(0, eq).Trim(), variable, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    var value = line.Substring(eq + 1).Trim();
                    if (value.Length >= 2 && (value[0] == '"' || value[0] == '\'') && value[^1] == value[0])
                    {
                        value = value.Substring(1, value.Length - 2);
                    }

                    return value;
                }
            }
            catch (IOException)
            {
                // Unreadable file: behave as if the key is not there.
            }

            return null;
        }
    }
}
