using System;

namespace Impostor.Server.LlmBots
{
    /// <summary>
    ///     Configuration of the LLM bot subsystem, read from the "LlmBots" section of config.json.
    /// </summary>
    public class LlmBotsConfig
    {
        public const string Section = "LlmBots";

        /// <summary>
        ///     Gets or sets a value indicating whether the /llmbots HTTP API is available.
        /// </summary>
        public bool Enabled { get; set; } = true;

        /// <summary>
        ///     Gets or sets the brain used by bots: "Auto" (LLM when a key exists, otherwise heuristic),
        ///     "Heuristic" (never call an LLM) or "Llm".
        /// </summary>
        public string BrainMode { get; set; } = "Auto";

        /// <summary>
        ///     Gets or sets the OpenRouter base url.
        /// </summary>
        public string OpenRouterBaseUrl { get; set; } = "https://openrouter.ai/api/v1";

        /// <summary>
        ///     Gets or sets the name of the environment variable (or .env entry) holding the OpenRouter key.
        /// </summary>
        public string ApiKeyVariable { get; set; } = "OPENROUTER_API_KEY";

        /// <summary>
        ///     Gets or sets the models to try, in order. Only ":free" models are used unless AllowPaidModels is set.
        ///     When empty the server asks OpenRouter which free models exist.
        /// </summary>
        public string[] Models { get; set; } = Array.Empty<string>();

        /// <summary>
        ///     Gets or sets a value indicating whether models that are not free may be used.
        /// </summary>
        public bool AllowPaidModels { get; set; } = false;

        /// <summary>
        ///     Gets or sets the global request budget shared by every bot, per minute.
        /// </summary>
        public int MaxRequestsPerMinute { get; set; } = 16;

        /// <summary>
        ///     Gets or sets a hard cap of LLM requests per server run. 0 means unlimited.
        /// </summary>
        public int MaxRequestsPerRun { get; set; } = 0;

        /// <summary>
        ///     Gets or sets the timeout of a single LLM request in seconds.
        /// </summary>
        public int RequestTimeoutSeconds { get; set; } = 25;

        /// <summary>
        ///     Gets or sets the interval between two strategic LLM decisions of a single bot while playing.
        ///     Set to 0 to only use the LLM in meetings.
        /// </summary>
        public int StrategyIntervalSeconds { get; set; } = 0;

        /// <summary>
        ///     Gets or sets a speed multiplier for everything a bot does on its own clock (walking, doing tasks).
        ///     1 is the pace of a human player. Only useful above 1 for automated tests.
        /// </summary>
        public double TimeScale { get; set; } = 1;

        /// <summary>
        ///     Gets or sets the chance (0 to 1) that an impostor bot starts a reactor or O2 sabotage when it gets the
        ///     opportunity (roughly once every two minutes at most). 0 turns bot sabotage off. Only The Skeld, Dleks and MIRA HQ.
        /// </summary>
        public double ImpostorSabotageChance { get; set; } = 0.2;

        /// <summary>
        ///     Gets or sets a value indicating whether bots shuffle around a little in the lobby so they look alive.
        /// </summary>
        public bool LobbyIdleMovement { get; set; } = true;

        /// <summary>
        ///     Gets or sets the number of chat lines a bot may say per meeting at most.
        /// </summary>
        public int MaxLinesPerMeeting { get; set; } = 3;

        /// <summary>
        ///     Gets or sets how long, in seconds, the meeting intro animation lasts before discussion starts.
        /// </summary>
        public double MeetingAnimationSeconds { get; set; } = 9.9;

        /// <summary>
        ///     Gets or sets how long, in seconds, bots stay on the game over screen before rejoining the lobby.
        /// </summary>
        public double EndScreenSeconds { get; set; } = 6;

        /// <summary>
        ///     Gets or sets how long, in seconds, bots stand still after a meeting ends (exile cutscene).
        /// </summary>
        public double PostMeetingFreezeSeconds { get; set; } = 5;

        /// <summary>
        ///     Gets or sets a value indicating whether transcripts of meetings and decisions are written to disk.
        /// </summary>
        public bool WriteTranscripts { get; set; } = true;

        /// <summary>
        ///     Gets or sets the directory transcripts are written to.
        /// </summary>
        public string TranscriptDirectory { get; set; } = "llmbots-logs";

        /// <summary>
        ///     Gets or sets a token that must be sent as ?token= to control bots over HTTP from another machine.
        ///     When empty only requests from the server machine itself are accepted.
        /// </summary>
        public string ControlToken { get; set; } = string.Empty;

        /// <summary>
        ///     Gets or sets a value indicating whether the lobby host may type "!bots 3" in the lobby chat.
        /// </summary>
        public bool ChatCommands { get; set; } = true;

        /// <summary>
        ///     Gets or sets the most bots one server run may have at the same time.
        /// </summary>
        public int MaxBots { get; set; } = 12;

        /// <summary>
        ///     Gets or sets the names bots may use, up to 10 characters each.
        /// </summary>
        public string[] BotNames { get; set; } =
        {
            "Ada", "Bolt", "Cleo", "Dax", "Echo", "Fable", "Gizmo", "Hex", "Iris", "Juno", "Kilo", "Luna",
        };
    }
}
