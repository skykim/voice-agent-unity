using System;
using System.Globalization;
using UnityEngine;

namespace VoiceAgent
{
    /// <summary>
    /// A simulated home the commands act on. Commands take no free-form arguments: the light uses the seven rainbow
    /// presets (a color named in the utterance, otherwise the next one; yellow by default) and "volume up/down"
    /// moves the volume by 10 points.
    /// </summary>
    public sealed class SmartHome
    {
        public readonly struct LightColor
        {
            public readonly string Name, NameKo;
            public readonly Color Value;

            public LightColor(string name, string nameKo, Color value)
            {
                Name = name;
                NameKo = nameKo;
                Value = value;
            }
        }

        public static readonly LightColor[] Colors =
        {
            new("red", "빨간색", new Color(1f, 0.27f, 0.27f)),
            new("orange", "주황색", new Color(1f, 0.6f, 0.2f)),
            new("yellow", "노란색", new Color(1f, 0.87f, 0.25f)),
            new("green", "초록색", new Color(0.35f, 0.85f, 0.4f)),
            new("blue", "파란색", new Color(0.3f, 0.55f, 1f)),
            new("indigo", "남색", new Color(0.35f, 0.3f, 0.85f)),
            new("violet", "보라색", new Color(0.75f, 0.4f, 1f)),
        };

        public const int DefaultColor = 2;
        public const int VolumeStep = 10;
        // English names count as whole words ("reddish" isn't red); Korean ones anywhere ("빨간색으로").
        static readonly (string Word, string Name)[] s_ColorWords =
        {
            ("red", "red"), ("orange", "orange"), ("yellow", "yellow"), ("green", "green"), ("blue", "blue"), ("indigo", "indigo"), ("violet", "violet"), ("purple", "violet"),
            ("빨간", "red"), ("빨강", "red"), ("주황", "orange"), ("노란", "yellow"), ("노랑", "yellow"), ("초록", "green"), ("녹색", "green"),
            ("파란", "blue"), ("파랑", "blue"), ("남색", "indigo"), ("보라", "violet"),
        };

        public bool LightOn { get; private set; }
        public int ColorIndex { get; private set; } = DefaultColor;
        public bool TvOn { get; private set; }
        public bool ComputerOn { get; private set; }
        public float VacuumUntil { get; private set; } = -1f;
        public bool MusicOn { get; private set; }
        public int Volume { get; private set; } = 50;

        public LightColor CurrentColor => Colors[ColorIndex];
        public bool Vacuuming => Time.time < VacuumUntil;

        public event Action Changed;

        /// <summary>Applies a command and returns its spoken reply, or null when another component answers it.</summary>
        public string Execute(Command command, string utterance = null)
        {
            switch (command.id)
            {
                case "turn_on_light": LightOn = true; break;
                case "turn_off_light": LightOn = false; break;
                case "set_light_color": ColorIndex = SpokenColor(utterance) ?? (ColorIndex + 1) % Colors.Length; LightOn = true; break;
                case "turn_on_tv": TvOn = true; break;
                case "turn_off_tv": TvOn = false; break;
                case "turn_on_computer": ComputerOn = true; break;
                case "turn_off_computer": ComputerOn = false; break;
                case "start_vacuum": VacuumUntil = Time.time + 8f; break;
                case "stop_vacuum": VacuumUntil = -1f; break;
                case "play_music": MusicOn = true; break;
                case "stop_music": MusicOn = false; break;
                case "volume_up": Volume = Mathf.Min(100, Volume + VolumeStep); break;
                case "volume_down": Volume = Mathf.Max(0, Volume - VolumeStep); break;
            }
            Changed?.Invoke();

            // The reply follows the language of the request: a Korean request gets reply_ko.
            var lang = LangDetect.Of(utterance);
            var reply = lang == Lang.Ko && !string.IsNullOrEmpty(command.reply_ko) ? command.reply_ko : command.reply;
            if (string.IsNullOrEmpty(reply)) return null;
            return reply
                .Replace("{color}", lang == Lang.Ko ? CurrentColor.NameKo : CurrentColor.Name)
                .Replace("{volume}", Volume.ToString(CultureInfo.InvariantCulture))
                .Replace("{time}", FormatTime(DateTime.Now, lang));
        }

        /// <summary>The rainbow preset named in the utterance, English or Korean ("purple" counts as violet), or null.</summary>
        public static int? SpokenColor(string utterance)
        {
            // The earliest color in the sentence wins.
            var text = utterance ?? string.Empty;
            string name = null;
            var first = int.MaxValue;
            foreach (var (word, color) in s_ColorWords)
            {
                var at = word[0] < 128 ? TextScan.IndexOfWord(text, word) : text.IndexOf(word, StringComparison.Ordinal);
                if (at >= 0 && at < first) (first, name) = (at, color);
            }
            return name == null ? null : Array.FindIndex(Colors, c => c.Name == name);
        }

        public static string FormatTime(DateTime time, Lang lang)
        {
            if (lang == Lang.En) return time.ToString("h:mm tt", CultureInfo.InvariantCulture);
            var hour = time.Hour % 12 == 0 ? 12 : time.Hour % 12;
            return $"{(time.Hour < 12 ? "오전" : "오후")} {hour}시 {time.Minute}분";
        }
    }
}
