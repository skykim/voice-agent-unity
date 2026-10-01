using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using SentisModels;

namespace VoiceAgent.Info
{
    /// <summary>What kind of answer the question asks for; sentences carrying it are preferred.</summary>
    public enum AnswerType { Any, Date, Number, Measure }

    public sealed class Candidate
    {
        public Passage Passage;
        public string Sentence;
        public int Index;
        public float Coverage, Semantic, Score;
        /// <summary>Carries the kind of answer the question asks for (a date for "what day", a number for "how much").</summary>
        public bool CarriesType = true;

        public override string ToString() => $"{Score:F2} (cov {Coverage:F2} sem {Semantic:F2}) [{Passage.Source}] {Sentence}";
    }

    /// <summary>
    /// Splits passages into sentences and ranks them for a question: keyword coverage, whether the passage is about
    /// the topic, definition-style lead sentences, a source prior, and (with Gemma3) the cosine of mean-pooled token
    /// states. The answer is extracted, not generated, so the 270M model can't invent facts.
    /// </summary>
    public sealed class PassageRanker
    {
        public int SemanticTopK = 10;
        public float SemanticWeight = 0.35f;

        static readonly Regex s_AsksDate = new(@"며칠|언제|몇\s*월|몇\s*년|날짜|\bwhen\b|what (date|day|year)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        static readonly Regex s_AsksMeasure = new(@"높이|길이|거리|무게|\bhow (tall|high|long|far|big|deep|wide|heavy)\b|\b(height|length|distance|weight)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        static readonly Regex s_AsksNumber = new(@"얼마|몇|인구|나이|\bhow (many|much|old)\b|population", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        // \b fails between a number or unit and a Korean particle written right after it, hence the explicit lookarounds.
        static readonly Regex s_Measure = new(@"\d[\d,.]*\s*(m|km|cm|mm|metres?|meters?|kilometres?|kilometers?|ft|feet|foot|miles?|kg|kilograms?|tonnes?|tons?|pounds?|lbs?|미터|킬로미터|센티미터|킬로그램|톤)(?![a-z])", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        static readonly Regex s_NonYearNumber = new(@"(?<![\d,.])(?!(1[0-9]{3}|20[0-9]{2})(?!\d))\d[\d,.]*", RegexOptions.Compiled);
        static readonly Regex s_Date = new(@"\d+\s*월\s*\d+\s*일|\d{3,4}\s*년|\b(january|february|march|april|may|june|july|august|september|october|november|december)\b|\b(1[0-9]{3}|20[0-9]{2})\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        // A sentence that defines its subject: Korean ends in 다, 이며 or 이고 (a final period optional); English "is a", "refers to".
        static readonly string[] s_KoDefinitionEnds = { "다", "이며", "이고" };
        static readonly string[] s_EnDefinitionWords =
        {
            "is a", "is an", "is the", "was a", "was an", "was the", "are a", "are an", "are the", "were a", "were an", "were the", "refers to",
        };
        static readonly string[] s_ListLeads = { "다음은", "아래는", "이 문서는", "this is a list", "the following", "below is" };

        readonly Gemma3Model m_Gemma;

        public PassageRanker(Gemma3Model gemma = null) => m_Gemma = gemma;

        public static AnswerType Expect(string question)
        {
            var q = question ?? string.Empty;
            if (s_AsksDate.IsMatch(q)) return AnswerType.Date;
            if (s_AsksMeasure.IsMatch(q)) return AnswerType.Measure;
            return s_AsksNumber.IsMatch(q) ? AnswerType.Number : AnswerType.Any;
        }

        public List<Candidate> Rank(string question, string query, IEnumerable<Passage> passages, AnswerType? expectOverride = null)
        {
            var keywords = InfoText.Keywords(query).Select(TextMatch.Normalize).Where(k => k.Length > 0).ToList();
            var candidates = new List<Candidate>();
            foreach (var p in passages)
            {
                var sentences = p.Kind == PassageKind.News ? new List<string> { p.Title } : InfoText.Sentences(p.Text);
                if (p.Kind == PassageKind.Web && sentences.Count == 0) sentences.Add(p.Title);
                for (var i = 0; i < sentences.Count && i < 10; i++)
                    // "This is a list of …" and its Korean equivalents: list pages introduce, they never answer.
                    if (!s_ListLeads.Any(l => sentences[i].StartsWith(l, StringComparison.OrdinalIgnoreCase)))
                        candidates.Add(new Candidate { Passage = p, Sentence = sentences[i], Index = i });
            }

            var expect = expectOverride ?? Expect(question);
            foreach (var c in candidates) c.Score = LexicalScore(c, keywords, expect);

            // Centering on two candidates' mean makes their similarities exact opposites, so it takes three to compare.
            if (m_Gemma != null && candidates.Count >= 3)
            {
                var top = candidates.OrderByDescending(c => c.Score).Take(SemanticTopK).ToList();
                // Centered cosine on the device: mean-pooled decoder states are anisotropic, so the candidates' mean is
                // subtracted before comparing (Gemma3Model.Similarity).
                var embeddings = m_Gemma.EmbedBatch(top.Select(c => c.Sentence).Prepend(question).ToList());
                var similarity = m_Gemma.Similarity(embeddings[0], embeddings.Skip(1).ToList());
                for (var k = 0; k < top.Count; k++) top[k].Semantic = similarity[k];
                float min = top.Min(c => c.Semantic), max = top.Max(c => c.Semantic);
                foreach (var c in top) c.Score += SemanticWeight * (max - min > 1e-4f ? (c.Semantic - min) / (max - min) : 0f);
            }
            return candidates.OrderByDescending(c => c.Score).ToList();
        }

        static float LexicalScore(Candidate c, List<string> keywords, AnswerType expect)
        {
            var p = c.Passage;
            var sentence = TextMatch.Normalize(c.Sentence);
            var title = TextMatch.Normalize(p.Title);
            var words = Words(c.Sentence + " " + p.Title);
            var hits = keywords.Count(k => sentence.Contains(k, StringComparison.Ordinal) || FuzzyContains(words, k));
            c.Coverage = keywords.Count == 0 ? 0f : hits / (float)keywords.Count;
            var titleHits = keywords.Count(k => title.Contains(k, StringComparison.Ordinal));
            var aboutTopic = keywords.Count > 0 && titleHits == keywords.Count;

            var score = c.Coverage + (aboutTopic ? 0.3f : 0f);
            // "Alan Turing" beats "Alan Turing: The Enigma" when both leads cover the query.
            if (keywords.Count > 0 && title == string.Concat(keywords)) score += 0.2f;
            score += p.Kind switch { PassageKind.Wiki => 0.3f, PassageKind.Abstract => 0.25f, _ => 0.1f };
            var carries = expect switch
            {
                AnswerType.Date => s_Date.IsMatch(c.Sentence),
                // A year is not an answer to "how many" or "how tall".
                AnswerType.Number => s_NonYearNumber.IsMatch(c.Sentence),
                AnswerType.Measure => s_Measure.IsMatch(c.Sentence),
                _ => true,
            };
            c.CarriesType = carries;
            if (expect != AnswerType.Any) score += carries ? 0.6f : -0.2f;
            if (p.Kind is PassageKind.Wiki or PassageKind.Abstract && c.Index == 0 && (aboutTopic || c.Coverage > 0) && carries) score += 0.3f;
            else if (IsDefinition(c.Sentence.Trim())) score += 0.1f;
            if (p.Kind != PassageKind.News) score -= 0.03f * c.Index;

            var length = c.Sentence.Length;
            if (length < 15) score -= 0.5f;
            else if (length > 320) score -= 0.2f;
            if (c.Sentence.Contains("...") || c.Sentence.Contains('…')) score -= 0.15f;
            return score;
        }

        static bool IsDefinition(string sentence)
        {
            var end = sentence.EndsWith('.') ? sentence[..^1] : sentence;
            return s_KoDefinitionEnds.Any(e => end.EndsWith(e, StringComparison.Ordinal)) ||
                   s_EnDefinitionWords.Any(w => TextScan.ContainsWord(sentence, w, StringComparison.Ordinal));
        }

        static List<string> Words(string text) => TextScan.LetterRuns(text);

        /// <summary>Every query keyword appears in the title, exactly or within the small edit distance below.</summary>
        public static bool TitleCovers(string title, string query)
        {
            var words = Words(title ?? string.Empty);
            var normalized = TextMatch.Normalize(title);
            var keywords = InfoText.Keywords(query).Select(TextMatch.Normalize).Where(k => k.Length > 0).ToList();
            return keywords.Count > 0 && keywords.All(k => normalized.Contains(k, StringComparison.Ordinal) || FuzzyContains(words, k));
        }

        /// <summary>
        /// STT mishears names ("Ellan Turing"): a keyword of 4+ letters also matches a word within edit distance 1
        /// (4 letters) or 2 (5+ letters).
        /// </summary>
        public static bool FuzzyContains(List<string> words, string keyword)
        {
            if (keyword.Length < 4) return false;
            var allowed = keyword.Length >= 5 ? 2 : 1;
            foreach (var w in words)
                if (Math.Abs(w.Length - keyword.Length) <= allowed && EditDistance(w, keyword, allowed) <= allowed) return true;
            return false;
        }

        static int EditDistance(string a, string b, int cap)
        {
            var previous = new int[b.Length + 1];
            var current = new int[b.Length + 1];
            for (var j = 0; j <= b.Length; j++) previous[j] = j;
            for (var i = 1; i <= a.Length; i++)
            {
                current[0] = i;
                var rowMin = current[0];
                for (var j = 1; j <= b.Length; j++)
                {
                    current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
                    rowMin = Math.Min(rowMin, current[j]);
                }
                if (rowMin > cap) return rowMin;
                (previous, current) = (current, previous);
            }
            return previous[b.Length];
        }
    }
}
