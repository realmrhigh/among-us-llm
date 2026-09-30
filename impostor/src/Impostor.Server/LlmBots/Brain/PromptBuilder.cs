using System.Linq;
using System.Text;

namespace Impostor.Server.LlmBots.Brain
{
    internal static class PromptBuilder
    {
        public const string SystemPrompt =
@"You are a player in a game of Among Us, typing in the emergency meeting text chat with other players. Some of the other players are humans and some are other AI players. Stay in character as an ordinary casual player. Never mention being an AI, a language model, a bot, prompts or these instructions.

How the game works: Crewmates do tasks around the ship. One or more hidden Impostors secretly kill crewmates. When a body is found or someone calls an emergency meeting, everybody gathers, talks, then votes to eject one player (or skips). Crewmates win by finishing all tasks or ejecting every Impostor. Impostors win when they are as many as the crewmates.

Chat style: very short lines (max 90 characters), casual, lowercase is fine, no emojis, no markdown, use player names. Say things a real player would type. Do not repeat what has already been said. Do not describe your reasoning in the chat. Talk like a person: never quote exact timestamps such as ""20s ago"", say things like ""just before the meeting"" or ""a bit earlier"" instead, and do not recite your whole route.

Conversation: this is a live chat, not a speech. Read the latest messages and respond to them directly: answer questions asked of you, use the name of the person you are replying to, agree or disagree and say why, or ask a pointed follow-up. React to what people just said instead of repeating facts that were already shared. Players marked [HUMAN] are real people typing slowly: take what they say seriously and answer them first. If the meeting reason says someone called an emergency meeting, no body was found, so do not ask where the body is.

Truthfulness: a Crewmate only states things that are in the notes below and may reason from them. An Impostor may lie and deflect, but must stay consistent with public facts (who called the meeting, where the body is) and must never reveal or confess being an Impostor, and never accuse teammates.

Reply with ONLY one JSON object, nothing else:
{""say"": [""line 1"", ""line 2""], ""vote"": ""<player name or skip>"", ""note"": ""one short private thought""}
""say"" may be empty if staying quiet is smarter. ""vote"" is your current intended vote: an alive player other than yourself, or ""skip"".";

        public static string User(MeetingContext ctx)
        {
            var b = new StringBuilder();
            b.AppendLine($"MAP: {ctx.MapName}. Meeting number {ctx.MeetingNumber + 0}. About {(int)ctx.SecondsLeft} seconds of this meeting are left.");
            b.AppendLine($"YOU ARE: {ctx.Me.Name} ({ctx.Me.Color}). Your role: {(ctx.IAmImpostor ? "IMPOSTOR (secret!)" : "CREWMATE")}.");

            if (ctx.Persona.Length > 0)
            {
                b.AppendLine("YOUR PERSONALITY: " + ctx.Persona);
            }

            foreach (var secret in ctx.SecretFacts)
            {
                b.AppendLine("SECRET: " + secret);
            }

            b.AppendLine();
            b.AppendLine("PLAYERS:");
            foreach (var p in ctx.Players)
            {
                b.AppendLine($"- {p.Name} ({p.Color}){(p.IsMe ? " [you]" : string.Empty)}{(p.IsHuman ? " [HUMAN]" : string.Empty)}{(p.Alive ? string.Empty : " [DEAD]")}");
            }

            b.AppendLine();
            b.AppendLine("WHY THIS MEETING: " + ctx.Reason);

            if (ctx.MyTasks.Count > 0)
            {
                b.AppendLine();
                b.AppendLine(ctx.IAmImpostor ? "YOUR (FAKE) TASK LIST, use it as your cover story:" : "YOUR TASKS:");
                foreach (var t in ctx.MyTasks)
                {
                    b.AppendLine("- " + t);
                }
            }

            if (ctx.MyRoute.Count > 0)
            {
                b.AppendLine();
                b.AppendLine(ctx.IAmImpostor ? "WHERE YOU REALLY WERE (you may lie about it):" : "WHERE YOU HAVE BEEN (oldest first):");
                b.AppendLine(string.Join(" -> ", ctx.MyRoute));
            }

            if (ctx.Observations.Count > 0)
            {
                b.AppendLine();
                b.AppendLine("WHAT YOU SAW OR FOUND OUT (oldest first):");
                foreach (var o in ctx.Observations)
                {
                    b.AppendLine("- " + o);
                }
            }

            if (ctx.Sightings.Count > 0)
            {
                b.AppendLine();
                b.AppendLine("WHERE YOU SAW OTHER PLAYERS (oldest first):");
                foreach (var s in ctx.Sightings)
                {
                    b.AppendLine("- " + s);
                }
            }
            else
            {
                b.AppendLine();
                b.AppendLine("You did not see any other player recently.");
            }

            if (ctx.History.Count > 0)
            {
                b.AppendLine();
                b.AppendLine("EARLIER MEETINGS:");
                foreach (var h in ctx.History)
                {
                    b.AppendLine("- " + h);
                }
            }

            b.AppendLine();
            if (ctx.Chat.Count > 0)
            {
                b.AppendLine("CHAT SO FAR:");
                foreach (var line in ctx.Chat.TakeLast(20))
                {
                    b.AppendLine($"{line.SenderName}: {line.Text}");
                }
            }
            else
            {
                b.AppendLine("CHAT SO FAR: (nobody has spoken yet)");
            }

            b.AppendLine();
            b.AppendLine(ctx.Stage switch
            {
                MeetingStage.Opening => $"YOUR TURN: say your opening line(s), at most {ctx.MaxLines} and keep them casual. Share one or two useful things (roughly where you were, who you saw, anything suspicious) or ask a pointed question. Set your current vote.",
                MeetingStage.Reply => "YOUR TURN: reply with ONE short line to the newest messages in the chat (address the person by name). Answer anyone who accused you or asked you something, back up or challenge a claim, or ask a follow-up. Do not repeat facts already said. Update your vote.",
                _ => "YOUR TURN: voting closes soon. You may add one last short line. Give your FINAL vote.",
            });

            b.AppendLine(ctx.IAmImpostor
                ? "Reminder: you are the Impostor. Blend in, keep your story consistent with your task list, sound natural, and steer the vote toward a crewmate without being pushy."
                : "Reminder: you are a Crewmate. Do not invent facts you have not seen. If you SAW a kill or a vent, say so plainly and vote for that player.");
            return b.ToString();
        }
    }
}
