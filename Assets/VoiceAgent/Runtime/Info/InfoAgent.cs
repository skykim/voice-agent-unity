using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using SentisModels;
using UnityEngine;

namespace VoiceAgent.Info
{
    public sealed class InfoAnswer
    {
        public bool Found;
        /// <summary>What the TTS says (no source line).</summary>
        public string Speech;
        /// <summary>Source attribution shown under the answer.</summary>
        public string Sources;
        public readonly List<string> Steps = new();
        public double Ms;
    }

    /// <summary>
    /// Answers the information commands from the internet: get_weather / get_time (a named city) via Open-Meteo,
    /// get_location via the public IP, and web_search via Wikidata for property questions, otherwise query rewrite →
    /// parallel keyless sources → sentence ranking (lexical + Gemma3 similarity) → extractive answer.
    /// </summary>
    public sealed class InfoAgent
    {
        public float MinScore = 0.7f;
        public int MaxAnswerChars = 190;

        readonly IWebClient m_Web;
        readonly GeoWeather m_Geo;
        readonly PassageRanker m_Ranker;

        public InfoAgent(IWebClient web, Gemma3Model gemma = null)
        {
            m_Web = web;
            m_Geo = new GeoWeather(web);
            m_Ranker = new PassageRanker(gemma);
        }

        /// <summary>Opens DNS/TLS to every service once so the first real question doesn't pay for it (~3 s cold vs 0.6 s warm).</summary>
        public async Awaitable WarmupAsync()
        {
            foreach (var url in new[]
                     {
                         "https://geocoding-api.open-meteo.com/v1/search?name=Seoul&count=1",
                         "https://api.open-meteo.com/v1/forecast?latitude=37.57&longitude=126.98&current=temperature_2m",
                         // Static files: they open the connections without spending the API's request budget.
                         "https://ko.wikipedia.org/static/favicon/wikipedia.ico",
                         "https://en.wikipedia.org/static/favicon/wikipedia.ico",
                         "https://query.wikidata.org/robots.txt",
                         "https://news.google.com/rss/search?q=news&hl=ko&gl=KR&ceid=KR:ko",
                     })
            {
                try { await m_Web.GetAsync(url); }
                catch (Exception) { /* warm-up only */ }
            }
        }

        public static bool Supports(string commandId) => commandId is "get_weather" or "get_location" or "get_time" or CommandCatalog.WebSearch;

        /// <summary>The placeholder bubble shown while an answer is being fetched.</summary>
        public static string LookingUp(Lang lang) => lang == Lang.Ko ? "찾아보는 중…" : "Looking it up…";

        /// <summary>The timing panel's action label for an answer from <see cref="AnswerAsync"/>.</summary>
        public static string ActionLabel(string commandId, string sources) => commandId switch
        {
            "get_weather" or "get_time" => "Action / Open-Meteo",
            "get_location" => "Action / IP lookup",
            _ when sources == null => "Action / web search",
            _ when sources.StartsWith("Wikidata", StringComparison.Ordinal) => "Action / Wikidata",
            _ when sources.StartsWith("Wikipedia", StringComparison.Ordinal) || sources.StartsWith("위키백과", StringComparison.Ordinal) => "Action / Wikipedia",
            _ => "Action / news search",
        };

        /// <summary>Null means "not mine after all" (get_time without a city → the local clock).</summary>
        public async Awaitable<InfoAnswer> AnswerAsync(string commandId, string text, Lang lang, Action<string> progress = null, CancellationToken cancel = default)
        {
            var clock = Stopwatch.StartNew();
            var answer = new InfoAnswer();
            void Step(string line)
            {
                answer.Steps.Add($"{clock.Elapsed.TotalSeconds:F1}s {line}");
                progress?.Invoke(line);
            }

            try
            {
                switch (commandId)
                {
                    case "get_weather": await Weather(text, lang, answer, Step, cancel); break;
                    case "get_time":
                        if (!await Time(text, lang, answer, Step, cancel)) return null;
                        break;
                    case "get_location": await Location(lang, answer, Step, cancel); break;
                    default: await Search(text, lang, answer, Step, cancel); break;
                }
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // Network and parse failures end in the "couldn't reach" reply below; anything else is a bug worth seeing.
                if (e is not (WebException or InvalidOperationException or ArgumentException)) UnityEngine.Debug.LogException(e);
                Step($"failed: {e.Message}");
            }
            if (!answer.Found && string.IsNullOrEmpty(answer.Speech) && commandId != CommandCatalog.WebSearch)
                answer.Speech = lang == Lang.Ko ? "인터넷에서 정보를 가져오지 못했어요. 연결을 확인해 주세요." : "I couldn't reach the internet for that. Please check the connection.";
            answer.Ms = clock.Elapsed.TotalMilliseconds;
            return answer;
        }

        async Awaitable Weather(string text, Lang lang, InfoAnswer answer, Action<string> step, CancellationToken cancel)
        {
            step(lang == Lang.Ko ? "위치 확인 중…" : "Finding the place…");
            var place = await m_Geo.PlaceInTextAsync(text, lang, cancel) ?? await m_Geo.IpPlaceAsync(lang, cancel);
            if (place == null) return;
            step(lang == Lang.Ko ? $"{place.Name} 날씨 가져오는 중…" : $"Fetching weather for {place.Name}…");
            var forecast = await m_Geo.ForecastAsync(place, cancel);
            if (forecast == null) return;
            answer.Speech = GeoWeather.WeatherReply(place, forecast, GeoWeather.DayOffset(text), lang);
            answer.Sources = $"Open-Meteo / {place.Name}{(place.FromIp ? " (IP)" : string.Empty)}";
            answer.Found = true;
        }

        async Awaitable<bool> Time(string text, Lang lang, InfoAnswer answer, Action<string> step, CancellationToken cancel)
        {
            var place = await m_Geo.PlaceInTextAsync(text, lang, cancel);
            if (place == null) return false;
            step(lang == Lang.Ko ? $"{place.Name} 시간대 확인 중…" : $"Looking up the time zone of {place.Name}…");
            var forecast = await m_Geo.ForecastAsync(place, cancel);
            if (forecast == null) return false;
            answer.Speech = GeoWeather.TimeReply(place, forecast.UtcOffsetSeconds, DateTime.UtcNow, lang);
            answer.Sources = $"Open-Meteo / {place.Name}";
            answer.Found = true;
            return true;
        }

        async Awaitable Location(Lang lang, InfoAnswer answer, Action<string> step, CancellationToken cancel)
        {
            step(lang == Lang.Ko ? "IP로 위치 확인 중…" : "Locating by IP…");
            var place = await m_Geo.IpPlaceAsync(lang, cancel);
            if (place == null) return;
            answer.Speech = GeoWeather.LocationReply(place, lang);
            answer.Sources = "ipwho.is / Open-Meteo";
            answer.Found = true;
        }

        async Awaitable Search(string text, Lang lang, InfoAnswer answer, Action<string> step, CancellationToken cancel)
        {
            var query = InfoText.SearchQuery(text);
            var news = InfoText.IsNewsQuestion(text);
            var sourceNames = news ? (lang == Lang.Ko ? "Google 뉴스 / Bing 뉴스" : "Google News / Bing News") : lang == Lang.Ko ? "위키백과" : "Wikipedia / DuckDuckGo / Bing";
            step(lang == Lang.Ko ? $"'{query}' {(news ? "뉴스 찾는" : "검색")} 중… ({sourceNames})" : $"Searching '{query}'… ({sourceNames})");

            // Property questions ("capital of …", "when was … born") get a structured answer from Wikidata first. The text
            // sources only run when it has none, which keeps Wikipedia's small anonymous request budget for them.
            var fact = news ? null : Wikidata.Parse(text);
            if (fact != null)
            {
                answer.Steps.Add($"wikidata: {fact}");
                var found = await SafeFact(fact, lang, step, cancel);
                if (found != null)
                {
                    answer.Steps.Add($"wikidata: {found.EntityId} {found.EntityLabel} → {string.Join(", ", found.Values)}");
                    answer.Speech = found.Speech;
                    answer.Sources = $"Wikidata / {found.EntityLabel}";
                    answer.Found = true;
                    return;
                }
                answer.Steps.Add("wikidata: no entity or statement");
            }

            // All sources at once; each failure only costs that source. Bing's web RSS ignores Korean queries, so Korean
            // facts come from Wikipedia lead sections (with an English cross-check below).
            var tasks = new List<(string Name, Awaitable<List<Passage>> Task)>();
            if (news)
            {
                tasks.Add(("google-news", Safe(() => SearchSources.GoogleNewsAsync(m_Web, query, lang, 8, cancel), "google-news", step)));
                tasks.Add(("bing-news", Safe(() => SearchSources.BingNewsAsync(m_Web, query, lang, 8, cancel), "bing-news", step)));
            }
            else
            {
                tasks.Add(("wikipedia", Safe(() => SearchSources.WikipediaAsync(m_Web, query, lang, 2, cancel), "wikipedia", step)));
                if (lang == Lang.En)
                {
                    tasks.Add(("duckduckgo", Safe(() => SearchSources.DuckDuckGoAnswerAsync(m_Web, query, cancel), "duckduckgo", step)));
                    tasks.Add(("bing-web", Safe(() => SearchSources.BingWebAsync(m_Web, query, lang, 6, cancel), "bing-web", step)));
                }
            }
            var passages = new List<Passage>();
            var counts = new List<string>();
            foreach (var (name, task) in tasks)
            {
                var found = await task;
                passages.AddRange(found);
                counts.Add($"{name} {found.Count}");
            }
            answer.Steps.Add("sources: " + string.Join(", ", counts));

            // A misheard name ("Ellan Turing", "Alllan Turing") finds no article, or only lookalikes (a ship designed by
            // "Robert Allan Ltd."): retry with the longest word on its own ("Turing").
            if (!news && !passages.Any(p => p.Kind == PassageKind.Wiki && PassageRanker.TitleCovers(p.Title, query)))
            {
                var words = query.Split(' ').Where(w => w.Length >= 4).OrderByDescending(w => w.Length).Take(2).ToList();
                List<Passage> fallback = null;
                if (words.Count > 0 && query.Contains(' '))
                    foreach (var word in words)
                    {
                        step(lang == Lang.Ko ? $"'{query}' 문서가 없어 '{word}'(으)로 다시 찾는 중…" : $"No article for '{query}'; trying '{word}'…");
                        var retry = await Safe(() => SearchSources.WikipediaAsync(m_Web, word, lang, 2, cancel), "wikipedia-retry", step);
                        answer.Steps.Add($"retry '{word}': {retry.Count}");
                        if (retry.Count == 0) continue;
                        // The misheard word itself ("Alllan") finds more lookalikes; keep going until a title fits.
                        if (retry.Any(p => PassageRanker.TitleCovers(p.Title, query)))
                        {
                            passages.AddRange(retry);
                            fallback = null;
                            break;
                        }
                        fallback ??= retry;
                    }
                if (fallback != null) passages.AddRange(fallback);
            }
            if (passages.Count == 0) return;

            step(lang == Lang.Ko ? $"자료 {passages.Count}개에서 답 고르는 중…" : $"Ranking {passages.Count} results…");
            var expect = PassageRanker.Expect(text);
            var ranked = m_Ranker.Rank(text, query, passages);
            foreach (var c in ranked.Take(3)) answer.Steps.Add("rank: " + c);

            // Korean leads often omit the number or date asked for; the English article usually has it.
            if (!news && expect != AnswerType.Any && (ranked.Count == 0 || !ranked[0].CarriesType))
            {
                var english = passages.FirstOrDefault(p => !string.IsNullOrEmpty(p.EnglishTitle))?.EnglishTitle;
                if (english != null)
                {
                    step(lang == Lang.Ko ? $"영어 위키백과 '{english}' 확인 중…" : $"Checking '{english}'…");
                    var more = await Safe(() => SearchSources.WikipediaIntroAsync(m_Web, english, Lang.En, cancel), "wikipedia-en", step);
                    var rankedEn = m_Ranker.Rank(english + " " + text, english, more, expect);
                    answer.Steps.Add($"cross-lingual: {more.Count} passages, best {(rankedEn.Count > 0 ? rankedEn[0].ToString() : "none")}");
                    if (rankedEn.Count > 0 && rankedEn[0].CarriesType && rankedEn[0].Score >= MinScore) ranked = rankedEn;
                }
            }
            // When Wikidata couldn't answer "What's the capital of Korea?", the text answer must at least mention the capital, not just Korea.
            // Numbers and dates are already enforced by the expected answer type.
            if (fact != null && fact.Property.Kind == Wikidata.Kind.Item)
                ranked = ranked.Where(c => fact.Property.Ask.IsMatch(c.Sentence)).ToList();
            if (ranked.Count == 0 || ranked[0].Score < MinScore) return;
            // A sentence that covers none of the query's keywords is a lookalike, not an answer, however similar Gemma finds it.
            if (ranked[0].Coverage <= 0f) return;

            if (news) ComposeNews(query, lang, ranked, answer);
            else ComposeFact(ranked, expect, answer);
            answer.Found = !string.IsNullOrEmpty(answer.Speech);
        }

        void ComposeFact(List<Candidate> ranked, AnswerType expect, InfoAnswer answer)
        {
            var best = ranked[0];
            var keepNumbers = expect != AnswerType.Any;
            var speech = InfoText.Speakable(best.Sentence, keepNumbers);
            var sentences = best.Passage.Kind == PassageKind.News ? new List<string>() : InfoText.Sentences(best.Passage.Text);
            if (speech.Length < 60 && best.Index + 1 < sentences.Count)
                speech += " " + InfoText.Speakable(sentences[best.Index + 1], keepNumbers);
            answer.Speech = InfoText.Truncate(speech, MaxAnswerChars);
            answer.Sources = string.IsNullOrEmpty(best.Passage.Title) ? best.Passage.Source : $"{best.Passage.Source} / {best.Passage.Title}";
        }

        static void ComposeNews(string query, Lang lang, List<Candidate> ranked, InfoAnswer answer)
        {
            // Two headlines in two sentences, as short as the other web answers.
            var headlines = new List<Candidate>();
            foreach (var c in ranked.Where(c => c.Passage.Kind == PassageKind.News && c.Coverage > 0))
                if (headlines.Count < 2 && headlines.All(h => TextMatch.Dice(TextMatch.Normalize(h.Sentence), TextMatch.Normalize(c.Sentence)) < 0.6f))
                    headlines.Add(c);
            if (headlines.Count == 0) return;
            var lines = headlines.Select(h => InfoText.Speakable(h.Sentence).TrimEnd('.', ' ')).ToList();
            answer.Speech = lang == Lang.Ko ? $"{query} 최근 소식이에요: {lines[0]}." : $"Here's the latest on {query}: {lines[0]}.";
            if (lines.Count > 1) answer.Speech += lang == Lang.Ko ? $" 또, {lines[1]}." : $" Also, {lines[1]}.";
            answer.Sources = string.Join(" / ", headlines.Select(h => h.Passage.Source + (h.Passage.Date != null ? $" {h.Passage.Date[5..]}" : string.Empty)).Distinct());
        }

        async Awaitable<Fact> SafeFact(FactQuestion question, Lang lang, Action<string> step, CancellationToken cancel)
        {
            try
            {
                return await Wikidata.AnswerAsync(m_Web, question, lang, cancel);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                step($"wikidata failed: {e.Message}");
                return null;
            }
        }

        static async Awaitable<List<Passage>> Safe(Func<Awaitable<List<Passage>>> source, string name, Action<string> step)
        {
            try
            {
                return await source();
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                step($"{name} failed: {e.Message}");
                return new List<Passage>();
            }
        }
    }
}
