using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;

namespace VoiceAgent.Info
{
    /// <summary>Text helpers for turning utterances into queries and web text into speakable sentences.</summary>
    public static class InfoText
    {
        // Longest first, so an ending is stripped before any shorter ending it contains.
        static readonly string[] s_KoEndings =
        {
            "이란 뭐야", "란 뭐야", "이란 뭐예요", "란 뭐예요", "이란 뭔가요", "란 뭔가요", "이란 무엇인가요", "란 무엇인가요",
            "에 대해서 자세히 알려줘", "에 대해서 알려줘", "에 대해 알려줘", "에 대해서 설명해줘", "에 대해 설명해줘", "에 대해서", "에 대해",
            "좀 알려줄래", "좀 알려줘", "알려줄래", "알려 줘", "알려줘", "알려주세요", "설명해 줘", "설명해줘", "찾아봐 줘", "찾아봐줘", "찾아 줘", "찾아줘",
            "검색해 줘", "검색해줘", "검색해", "이 뭐야", "가 뭐야", "은 뭐야", "는 뭐야", "이 뭐예요", "가 뭐예요", "뭐야", "뭐예요", "뭔가요", "뭐지",
            "이 누구야", "가 누구야", "은 누구야", "는 누구야", "누구야", "누구예요", "누구지", "어디야", "언제야", "어때", "있어", "알아요", "아세요", "알아", "좀",
        };
        static readonly string[] s_EnPrefixes =
        {
            "can you tell me about", "tell me about", "what do you know about", "search for", "search", "look up", "who was", "who is", "who are",
            "what was", "what is", "what's", "what are", "define", "explain",
        };
        static readonly string[] s_NewsWords = { "최근", "요즘", "뉴스", "소식", "새로운", "최신", "오늘의", "latest", "recent", "news", "headlines", "any new" };
        // "the latest" before "latest", so the longer phrase goes as a whole.
        static readonly string[] s_EnQueryFiller = { "please", "for me", "the latest", "latest", "recent", "news", "about", "any" };
        static readonly string[] s_Articles = { "the", "a", "an" };

        /// <summary>Tags are removed before and after decoding, so entity-escaped markup (&amp;lt;a&amp;gt;) goes too.</summary>
        public static string StripHtml(string html) =>
            string.IsNullOrEmpty(html) ? string.Empty : TextScan.CollapseSpaces(StripTags(WebUtility.HtmlDecode(StripTags(html))));

        /// <summary>Every "&lt;…&gt;" becomes a space; a "&lt;" with no "&gt;" after it stays.</summary>
        static string StripTags(string html)
        {
            var sb = new StringBuilder(html.Length);
            for (var i = 0; i < html.Length; i++)
            {
                var close = html[i] == '<' ? html.IndexOf('>', i + 1) : -1;
                if (close > i + 1)
                {
                    sb.Append(' ');
                    i = close;
                }
                else sb.Append(html[i]);
            }
            return sb.ToString();
        }

        /// <summary>Drops parentheses/brackets (hanja, English glosses, citations) so TTS reads the sentence cleanly.</summary>
        public static string Speakable(string text, bool keepNumbers = false)
        {
            text ??= string.Empty;
            var sb = new StringBuilder(text.Length);
            for (var i = 0; i < text.Length; i++)
            {
                var c = text[i];
                var close = c is '(' or '（' ? text.IndexOfAny(new[] { ')', '）' }, i + 1) : c == '[' ? text.IndexOf(']', i + 1) : -1;
                if (close < 0)
                {
                    sb.Append(c);
                    continue;
                }
                // The spaces before a dropped group go with it.
                var group = text[i..(close + 1)];
                if (keepNumbers && group.Any(char.IsDigit) && !group.Contains(':')) sb.Append(group);
                else
                    while (sb.Length > 0 && char.IsWhiteSpace(sb[^1])) sb.Length--;
                i = close;
            }
            // Pronunciation glosses leave "(; 23 June 1912 …)" behind.
            var kept = sb.ToString();
            sb.Clear();
            for (var i = 0; i < kept.Length; i++)
            {
                sb.Append(kept[i]);
                if (kept[i] != '(') continue;
                var j = i + 1;
                while (j < kept.Length && char.IsWhiteSpace(kept[j])) j++;
                if (j == kept.Length || kept[j] is not (';' or ',')) continue;
                j++;
                while (j < kept.Length && char.IsWhiteSpace(kept[j])) j++;
                i = j - 1;
            }
            return TextScan.CollapseSpaces(sb.ToString());
        }

        /// <summary>Split at the spaces after a sentence end (. ! ? 。).</summary>
        public static List<string> Sentences(string text)
        {
            text ??= string.Empty;
            var sentences = new List<string>();
            var start = 0;
            for (var i = 1; i < text.Length; i++)
            {
                if (!char.IsWhiteSpace(text[i]) || text[i - 1] is not ('.' or '!' or '?' or '。')) continue;
                sentences.Add(text[start..i]);
                while (i + 1 < text.Length && char.IsWhiteSpace(text[i + 1])) i++;
                start = i + 1;
            }
            sentences.Add(text[start..]);
            return sentences.Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
        }

        /// <summary>The first <paramref name="count"/> sentences of <paramref name="text"/>.</summary>
        public static string FirstSentences(string text, int count) => string.Join(" ", Sentences(text).Take(count));

        /// <summary>English news words match whole words only, so "Newsom" and "newspaper" aren't news questions.</summary>
        public static bool IsNewsQuestion(string text)
        {
            var lower = (text ?? string.Empty).ToLowerInvariant();
            return s_NewsWords.Any(w => w[0] < 128 ? TextScan.ContainsWord(lower, w, StringComparison.Ordinal) : lower.Contains(w, StringComparison.Ordinal));
        }

        /// <summary>The topic of a question: "Who is Alan Turing?" → "Alan Turing", "any news about Nvidia" → "Nvidia" (Korean drops its question endings and particles).</summary>
        public static string SearchQuery(string text)
        {
            var q = TextScan.ReplaceRuns(text ?? string.Empty, TextScan.Punctuation, " ").Trim();
            if (LangDetect.Of(q) == Lang.Ko)
            {
                for (var changed = true; changed;)
                {
                    changed = false;
                    foreach (var ending in s_KoEndings)
                        if (q.EndsWith(ending, StringComparison.Ordinal) && q.Length > ending.Length)
                        {
                            q = q[..^ending.Length].TrimEnd();
                            changed = true;
                        }
                }
            }
            else
            {
                var lower = q.ToLowerInvariant();
                foreach (var prefix in s_EnPrefixes)
                    if (lower.StartsWith(prefix + " ", StringComparison.Ordinal))
                    {
                        q = q[(prefix.Length + 1)..];
                        break;
                    }
                q = TextScan.ReplaceWords(q, s_EnQueryFiller, " ").Trim();
                // "What is the Eiffel Tower?" → "Eiffel Tower", so the query can match the article's title.
                foreach (var article in s_Articles)
                    if (q.Length > article.Length && q.StartsWith(article, StringComparison.OrdinalIgnoreCase) && char.IsWhiteSpace(q[article.Length]))
                    {
                        q = q[article.Length..].TrimStart();
                        break;
                    }
            }
            // Whole words only for English ("newsletter" keeps its "news").
            foreach (var w in s_NewsWords) q = w[0] < 128 ? TextScan.ReplaceWords(q, new[] { w }, " ") : q.Replace(w, " ");
            q = TextScan.CollapseSpaces(q);
            // Drop trailing question words ("what day"), then the Korean particle they leave on the word before.
            var tokens = q.Split(' ').ToList();
            var dropped = false;
            while (tokens.Count > 1 && IsQuestionWord(tokens[^1]))
            {
                tokens.RemoveAt(tokens.Count - 1);
                dropped = true;
            }
            if (dropped) tokens[^1] = StripParticle(tokens[^1]);
            q = string.Join(" ", tokens);
            return q.Length > 0 ? q : text?.Trim() ?? string.Empty;
        }

        static readonly string[] s_QuestionWords = { "며칠", "언제", "언젠", "얼마", "어디", "어딘", "누구", "누군", "무엇", "무슨", "뭐", "뭔", "몇", "왜", "어떻게", "어떤", "how", "what", "when", "where", "who", "why", "which" };

        public static bool IsQuestionWord(string word)
        {
            var w = word.ToLowerInvariant();
            return s_QuestionWords.Any(q => w.StartsWith(q, StringComparison.Ordinal) && (w[0] >= '가' || w.Length == q.Length));
        }

        static readonly HashSet<string> s_EnStop = new() { "the", "a", "an", "of", "is", "are", "was", "were", "to", "in", "on", "for", "and", "do", "does", "did" };

        /// <summary>Keywords for lexical scoring: query words without Korean particles and English function words.</summary>
        public static List<string> Keywords(string query)
        {
            var words = new List<string>();
            foreach (var raw in TextScan.Words(query.ToLowerInvariant()))
            {
                var w = string.Concat(raw.Where(TextScan.IsLetterOrNumber));
                w = StripParticle(w);
                if (IsQuestionWord(w) || s_EnStop.Contains(w)) continue;
                if (w.Length >= 2 || (w.Length == 1 && w[0] >= '가')) words.Add(w);
            }
            return words.Distinct().ToList();
        }

        static readonly string[] s_Particles = { "에서", "으로", "에게", "까지", "부터", "은", "는", "이", "가", "을", "를", "의", "에", "로", "와", "과", "도" };

        public static string StripParticle(string word)
        {
            foreach (var p in s_Particles)
                if (word.Length > p.Length + 1 && word.EndsWith(p, StringComparison.Ordinal) && word[0] >= '가')
                    return word[..^p.Length];
            return word;
        }

        public static bool HasBatchim(string word)
        {
            if (string.IsNullOrEmpty(word)) return false;
            var c = word[^1];
            return c >= '가' && c <= '힣' && (c - '가') % 28 != 0;
        }

        /// <summary>The noun plus the Korean topic particle, chosen by whether it ends in a final consonant.</summary>
        public static string Topic(string noun) => noun + (HasBatchim(noun) ? "은" : "는");

        /// <summary>Cuts at a sentence or comma boundary near maxChars; a decimal point ("8,848.86") is not a boundary.</summary>
        public static string Truncate(string text, int maxChars)
        {
            if (text.Length <= maxChars) return text;
            var cut = text.LastIndexOf('.', maxChars - 1);
            while (cut > 0 && cut + 1 < text.Length && char.IsDigit(text[cut + 1])) cut = text.LastIndexOf('.', cut - 1);
            if (cut > maxChars / 2) return text[..(cut + 1)];
            cut = text.LastIndexOfAny(new[] { ',', '，', '、', ' ' }, maxChars - 1);
            return text[..(cut > maxChars / 2 ? cut : maxChars)].TrimEnd(',', ' ') + "…";
        }

        static readonly char[] s_OutletDashes = { '-', '|', '–', '\u2014' };

        /// <summary>News titles end with " - Outlet" or " | Outlet".</summary>
        public static string StripOutlet(string title)
        {
            // Google News sometimes appends it twice: "… - Outlet - Outlet in English".
            var result = (title ?? string.Empty).Trim();
            for (var i = 0; i < 2; i++)
            {
                // The outlet follows the last dash or bar, with spaces on both sides, and is 2 to 30 characters long.
                var dash = result.LastIndexOfAny(s_OutletDashes);
                if (dash < 13 || !char.IsWhiteSpace(result[dash - 1]) || dash + 1 == result.Length || !char.IsWhiteSpace(result[dash + 1]) || result.IndexOf('\n', 0, dash) >= 0) break;
                var outlet = result[(dash + 1)..].TrimStart();
                if (outlet.Length > 30 || result.Length - dash - 2 < 2) break;
                result = result[..dash].Trim();
            }
            return result;
        }
    }
}
