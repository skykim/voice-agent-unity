using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace VoiceAgent
{
    /// <summary>Plain string scanning for the text rules: whitespace, punctuation, whole words and letter runs.</summary>
    public static class TextScan
    {
        /// <summary>The punctuation an utterance loses before scoring or parsing (STT adds it, typing usually doesn't).</summary>
        public const string Punctuation = "?!.,~…";

        /// <summary>Whitespace runs become one space, and the ends are trimmed.</summary>
        public static string CollapseSpaces(string text) => string.Join(" ", Words(text));

        /// <summary>The text split on whitespace, without empty entries.</summary>
        public static string[] Words(string text) => (text ?? string.Empty).Split((char[])null, StringSplitOptions.RemoveEmptyEntries);

        /// <summary>Every run of characters from <paramref name="chars"/> becomes one <paramref name="with"/>.</summary>
        public static string ReplaceRuns(string text, string chars, string with)
        {
            var sb = new StringBuilder(text.Length);
            for (var i = 0; i < text.Length; i++)
            {
                if (chars.IndexOf(text[i]) < 0)
                {
                    sb.Append(text[i]);
                    continue;
                }
                while (i + 1 < text.Length && chars.IndexOf(text[i + 1]) >= 0) i++;
                sb.Append(with);
            }
            return sb.ToString();
        }

        /// <summary>A letter, digit, combining mark or connector: what a word is made of.</summary>
        public static bool IsWordChar(char c) =>
            char.IsLetterOrDigit(c) || CharUnicodeInfo.GetUnicodeCategory(c) is UnicodeCategory.NonSpacingMark or UnicodeCategory.ConnectorPunctuation;

        static bool WholeWordAt(string text, int index, string word, StringComparison comparison) =>
            index + word.Length <= text.Length && string.Compare(text, index, word, 0, word.Length, comparison) == 0 &&
            (index == 0 || !IsWordChar(text[index - 1])) &&
            (index + word.Length == text.Length || !IsWordChar(text[index + word.Length]));

        /// <summary>Where <paramref name="word"/> first appears as a whole word ("news" is not in "newsletter"), or -1.</summary>
        public static int IndexOfWord(string text, string word, StringComparison comparison = StringComparison.OrdinalIgnoreCase)
        {
            for (var i = text.IndexOf(word, comparison); i >= 0; i = i + 1 < text.Length ? text.IndexOf(word, i + 1, comparison) : -1)
                if (WholeWordAt(text, i, word, comparison)) return i;
            return -1;
        }

        public static bool ContainsWord(string text, string word, StringComparison comparison = StringComparison.OrdinalIgnoreCase) =>
            IndexOfWord(text, word, comparison) >= 0;

        /// <summary>
        /// Replaces whole-word occurrences of any of <paramref name="words"/>, left to right; where several start at the
        /// same place, the first in the list wins (so list "the latest" before "latest").
        /// </summary>
        public static string ReplaceWords(string text, IReadOnlyList<string> words, string with, StringComparison comparison = StringComparison.OrdinalIgnoreCase)
        {
            var sb = new StringBuilder(text.Length);
            for (var i = 0; i < text.Length;)
            {
                var hit = -1;
                for (var w = 0; w < words.Count && hit < 0; w++)
                    if (WholeWordAt(text, i, words[w], comparison)) hit = w;
                if (hit < 0)
                {
                    sb.Append(text[i++]);
                    continue;
                }
                sb.Append(with);
                i += words[hit].Length;
            }
            return sb.ToString();
        }

        /// <summary>Runs of letters and numbers, lowercased: "Alan Turing's (1912)" → alan, turing, s, 1912.</summary>
        public static List<string> LetterRuns(string text)
        {
            var runs = new List<string>();
            var lower = (text ?? string.Empty).ToLowerInvariant();
            for (var i = 0; i < lower.Length; i++)
            {
                if (!IsLetterOrNumber(lower[i])) continue;
                var start = i;
                while (i + 1 < lower.Length && IsLetterOrNumber(lower[i + 1])) i++;
                runs.Add(lower[start..(i + 1)]);
            }
            return runs;
        }

        /// <summary>Letters and numbers of any script, including ① and Ⅳ.</summary>
        public static bool IsLetterOrNumber(char c) => char.IsLetter(c) || char.IsNumber(c);
    }
}
