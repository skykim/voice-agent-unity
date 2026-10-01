using System.Collections.Generic;

namespace VoiceAgent
{
    public enum TurnRoute
    {
        /// <summary>Run the command (home control, or an info command answered from the web).</summary>
        Execute,
        /// <summary>Open-ended: Gemma3 generates the reply.</summary>
        Chat,
        /// <summary>No command is confident enough: Nova asks whether <see cref="TurnRouting.Suggestion"/> was meant, and the chips stay up.</summary>
        LowConfidence,
    }

    public static class TurnRouting
    {
        /// <summary>A command at least this likely runs.</summary>
        public const float AutoRunThreshold = 0.5f;
        /// <summary>A command below the threshold still runs when it leads clearly: at least this probability…</summary>
        public const float LeadFloor = 0.35f;
        /// <summary>…and this far ahead of the runner-up.</summary>
        public const float LeadMargin = 0.2f;
        /// <summary>
        /// A chat pick below this, with a command less than <see cref="ChatMargin"/> behind, is asked about instead of chatted
        /// to: it catches commands the head reads as small talk ("시끄러워 죽겠어": chat just ahead of volume_down).
        /// </summary>
        public const float ChatFloor = 0.3f;
        public const float ChatMargin = 0.1f;

        public static TurnRoute Decide(IReadOnlyList<RankedCommand> top, bool hasInfoAgent, float threshold)
        {
            var best = top[0];
            var id = best.Command.id;
            // With the info agent, a search always runs, however unsure: a miss falls back to Gemma, so asking gains nothing.
            if (id == CommandCatalog.WebSearch && hasInfoAgent) return TurnRoute.Execute;
            if (id == CommandCatalog.Chat && top.Count > 1 && best.Probability < ChatFloor && best.Probability - top[1].Probability < ChatMargin)
                return TurnRoute.LowConfidence;
            if (id is CommandCatalog.Chat or CommandCatalog.WebSearch) return TurnRoute.Chat;
            if (best.Probability >= threshold) return TurnRoute.Execute;
            var runnerUp = top.Count > 1 ? top[1].Probability : 0f;
            return best.Probability >= LeadFloor && best.Probability - runnerUp >= LeadMargin ? TurnRoute.Execute : TurnRoute.LowConfidence;
        }

        /// <summary>The command a low-confidence turn asks about: the top pick, or the runner-up when the top pick is chat.</summary>
        public static Command Suggestion(IReadOnlyList<RankedCommand> top) =>
            top[0].Command.id == CommandCatalog.Chat && top.Count > 1 ? top[1].Command : top[0].Command;
    }
}
