using System.Collections.Generic;

namespace VoiceAgent.Info
{
    /// <summary>Loose text comparison for passage ranking: normalized strings and character-bigram (Dice) similarity.</summary>
    public static class TextMatch
    {
        /// <summary>Lowercase with whitespace and punctuation removed, so "lights on" and "lightson!" match.</summary>
        public static string Normalize(string text)
        {
            var chars = new List<char>(text?.Length ?? 0);
            foreach (var c in text ?? string.Empty)
                if (char.IsLetterOrDigit(c)) chars.Add(char.ToLowerInvariant(c));
            return new string(chars.ToArray());
        }

        /// <summary>Dice coefficient of the two strings' character bigrams: 1 for the same bigrams, 0 for none shared.</summary>
        public static float Dice(string a, string b)
        {
            if (a.Length < 2 || b.Length < 2) return a == b && a.Length > 0 ? 1f : 0f;
            var bigrams = new Dictionary<int, int>();
            for (var i = 0; i < a.Length - 1; i++)
            {
                var key = (a[i] << 16) | a[i + 1];
                bigrams[key] = bigrams.TryGetValue(key, out var n) ? n + 1 : 1;
            }
            var overlap = 0;
            for (var i = 0; i < b.Length - 1; i++)
            {
                var key = (b[i] << 16) | b[i + 1];
                if (bigrams.TryGetValue(key, out var n) && n > 0)
                {
                    overlap++;
                    bigrams[key] = n - 1;
                }
            }
            return 2f * overlap / (a.Length - 1 + b.Length - 1);
        }
    }
}
