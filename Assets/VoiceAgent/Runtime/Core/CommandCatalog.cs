using System;
using System.Collections.Generic;
using UnityEngine;

namespace VoiceAgent
{
    public enum Lang { Ko, En }

    public static class LangDetect
    {
        public static Lang Of(string text)
        {
            foreach (var c in text ?? string.Empty)
                if (c >= '가' && c <= '힣') return Lang.Ko;
            return Lang.En;
        }
    }

    [Serializable]
    public sealed class Command
    {
        public string id, icon, color, label, reply, label_ko, reply_ko;
        public string[] options;

        public string Label(Lang lang) => lang == Lang.Ko && !string.IsNullOrEmpty(label_ko) ? label_ko : label;
        public Color Tint => ColorUtility.TryParseHtmlString(color, out var c) ? c : Color.gray;
    }

    /// <summary>The command set (Resources/Commands.json), shared with the trainer; order = jevlike option order.</summary>
    public sealed class CommandCatalog
    {
        [Serializable]
        sealed class File
        {
            public Command[] commands;
        }

        public const string Chat = "chat";
        public const string WebSearch = "web_search";
        public const string IntroduceSelf = "introduce_self";

        readonly Dictionary<string, int> m_Index = new();

        public IReadOnlyList<Command> Commands { get; }

        public CommandCatalog(string json)
        {
            Commands = JsonUtility.FromJson<File>(json).commands;
            for (var i = 0; i < Commands.Count; i++) m_Index[Commands[i].id] = i;
        }

        public static CommandCatalog Load() => new(Resources.Load<TextAsset>("Commands").text);

        public int IndexOf(string id) => m_Index.TryGetValue(id, out var i) ? i : -1;
        public Command this[string id] => Commands[m_Index[id]];
        public int Count => Commands.Count;
    }

    /// <summary>One ranked command for the suggestion chips.</summary>
    public readonly struct RankedCommand
    {
        public readonly Command Command;
        public readonly float Probability;

        public RankedCommand(Command command, float probability)
        {
            Command = command;
            Probability = probability;
        }
    }

    public static class Ranking
    {
        public static List<RankedCommand> Top(CommandCatalog catalog, float[] probs, int count)
        {
            var order = new List<int>(probs.Length);
            for (var i = 0; i < probs.Length; i++) order.Add(i);
            order.Sort((a, b) => probs[b].CompareTo(probs[a]));
            var top = new List<RankedCommand>(count);
            for (var i = 0; i < Math.Min(count, order.Count); i++) top.Add(new RankedCommand(catalog.Commands[order[i]], probs[order[i]]));
            return top;
        }
    }
}
