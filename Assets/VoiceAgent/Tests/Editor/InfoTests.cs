using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using VoiceAgent.Info;
using UnityEngine;
using UnityEngine.TestTools;

namespace VoiceAgent.Tests
{
    /// <summary>Offline tests on captured responses, plus live checks against the real services (category "Network").</summary>
    public class InfoTests
    {
        static string Fixture(string name) => File.ReadAllText($"Assets/VoiceAgent/Tests/Editor/Fixtures/{name}");

        [TestCase("블랙홀이 뭐야?", "블랙홀")]
        [TestCase("앨런 튜링에 대해 알려줘", "앨런 튜링")]
        [TestCase("최근 애플 소식 알려줘", "애플")]
        [TestCase("유니티 6 출시일 검색해줘", "유니티 6 출시일")]
        [TestCase("한글날이 며칠이야", "한글날")]
        [TestCase("에펠탑 높이가 얼마야", "에펠탑 높이")]
        [TestCase("송지원이 누군지 알아?", "송지원")]
        [TestCase("Who is Alan Turing?", "Alan Turing")]
        [TestCase("what's the latest news about nvidia", "nvidia")]
        [TestCase("tell me about black holes", "black holes")]
        [TestCase("What is the Eiffel Tower?", "Eiffel Tower")]
        [TestCase("블랙홀이란 뭐야?", "블랙홀")]
        [TestCase("이란에 대해 알려줘", "이란")]
        [TestCase("테헤란 알려줘", "테헤란")]
        public void SearchQuery_StripsQuestionWords(string text, string expected) => Assert.AreEqual(expected, InfoText.SearchQuery(text));

        [TestCase("최근 애플 소식 알려줘", true)]
        [TestCase("any news on Nvidia?", true)]
        [TestCase("블랙홀이 뭐야", false)]
        [TestCase("Who is Gavin Newsom?", false)]
        [TestCase("What is a newspaper?", false)]
        public void NewsQuestion_Detected(string text, bool news) => Assert.AreEqual(news, InfoText.IsNewsQuestion(text));

        [TestCase("도쿄 날씨 어때?", "도쿄")]
        [TestCase("내일 부산 날씨 알려줘", "부산")]
        [TestCase("what's the weather like in Paris", "Paris")]
        [TestCase("what time is it in new york", "new york")]
        [TestCase("상하이 날씨 어때", "상하이")]
        [TestCase("두바이는 지금 몇 시야", "두바이")]
        public void PlaceCandidates_FindCity(string text, string expected) => CollectionAssert.Contains(GeoWeather.PlaceCandidates(text), expected);

        [Test]
        public void PlaceCandidates_PreferTheNamedPlace()
        {
            Assert.AreEqual("Paris", GeoWeather.PlaceCandidates("Is it going to rain in Paris tomorrow?")[0]);
            CollectionAssert.DoesNotContain(GeoWeather.PlaceCandidates("Is it going to rain in Paris?"), "going");
            Assert.AreEqual("상하이", GeoWeather.PlaceCandidates("상하이 날씨")[0], "the word as written before its particle-stripped form");
        }

        [Test]
        public void TimeReply_FormatsHalfHourOffsets()
        {
            var place = new Place { Name = "Kathmandu" };
            StringAssert.EndsWith("(UTC+5:45).", GeoWeather.TimeReply(place, 20700, new DateTime(2026, 9, 28, 6, 0, 0), Lang.En));
            StringAssert.EndsWith("(UTC-3:30).", GeoWeather.TimeReply(place, -12600, new DateTime(2026, 9, 28, 6, 0, 0), Lang.En));
        }

        [Test]
        public void PlaceCandidates_EmptyWithoutCity()
        {
            CollectionAssert.IsEmpty(GeoWeather.PlaceCandidates("오늘 날씨 어때"));
            CollectionAssert.IsEmpty(GeoWeather.PlaceCandidates("지금 몇 시야"));
        }

        [Test]
        public void Weather_ParsesOpenMeteoAndSpeaks()
        {
            var place = GeoWeather.ParseGeocode(Fixture("geocode_tokyo.json"));
            Assert.AreEqual("도쿄", place.Name);
            var forecast = GeoWeather.ParseForecast(Fixture("forecast_tokyo.json"));
            Assert.AreEqual(32400, forecast.UtcOffsetSeconds);
            Assert.AreEqual(3, forecast.DailyMax.Length);
            var ko = GeoWeather.WeatherReply(place, forecast, 0, Lang.Ko);
            StringAssert.StartsWith("도쿄는 지금 ", ko);
            StringAssert.Contains("최고", ko);
            StringAssert.StartsWith("내일 도쿄 날씨는", GeoWeather.WeatherReply(place, forecast, 1, Lang.Ko));
            StringAssert.Contains("in 도쿄 right now", GeoWeather.WeatherReply(place, forecast, 0, Lang.En));
            Assert.AreEqual("도쿄는 지금 오후 3시 5분이에요 (UTC+9).", GeoWeather.TimeReply(place, 32400, new DateTime(2026, 9, 28, 6, 5, 0), Lang.Ko));
            Debug.Log($"[Weather] {ko}");
        }

        [Test]
        public void Wikipedia_LeadBecomesSpeakableDefinition()
        {
            var passages = SearchSources.ParseWikiPages(Fixture("wiki_blackhole_ko.json"), Lang.Ko);
            Assert.AreEqual("블랙홀", passages[0].Title);
            Assert.AreEqual("Black hole", passages[0].EnglishTitle);
            var ranked = new PassageRanker().Rank("블랙홀이 뭐야", "블랙홀", passages);
            Debug.Log("[Rank] " + string.Join("\n       ", ranked.Take(3)));
            Assert.AreEqual(0, ranked[0].Index);
            var speech = InfoText.Speakable(ranked[0].Sentence);
            StringAssert.StartsWith("블랙홀은 ", speech);
            StringAssert.DoesNotContain("영어", speech);
        }

        [Test]
        public void Wikipedia_DateQuestionPicksTheDateSentence()
        {
            var passages = SearchSources.ParseWikiPages(Fixture("wiki_hangulday_ko.json"), Lang.Ko);
            var ranked = new PassageRanker().Rank("한글날이 며칠이야", InfoText.SearchQuery("한글날이 며칠이야"), passages);
            Debug.Log("[Rank] " + string.Join("\n       ", ranked.Take(3)));
            StringAssert.Contains("10월 9일", ranked[0].Sentence);
            Assert.IsTrue(ranked[0].CarriesType);
        }

        [Test]
        public void GoogleNews_SourceTagUsed()
        {
            var news = SearchSources.ParseRss(Fixture("googlenews_apple_ko.xml"), PassageKind.News);
            Assert.GreaterOrEqual(news.Count, 3);
            Assert.AreNotEqual("news.google.com", news[0].Source);
            Debug.Log("[GoogleNews] " + string.Join(" | ", news.Take(3).Select(n => $"{n.Source}: {n.Title}")));
        }

        [TestCase("한글날이 며칠이야", AnswerType.Date)]
        [TestCase("When was Alan Turing born?", AnswerType.Date)]
        [TestCase("에펠탑 높이가 얼마야", AnswerType.Measure)]
        [TestCase("How tall is the Eiffel Tower?", AnswerType.Measure)]
        [TestCase("how many people live in Seoul", AnswerType.Number)]
        [TestCase("블랙홀이 뭐야", AnswerType.Any)]
        public void AnswerType_FromQuestion(string question, AnswerType expected) => Assert.AreEqual(expected, PassageRanker.Expect(question));

        [Test]
        public void FuzzyKeywords_ToleranceGrowsWithLength()
        {
            var words = new List<string> { "alan", "turing", "was", "an", "english", "mathematician" };
            Assert.IsTrue(PassageRanker.FuzzyContains(words, "ellan"), "5 letters, distance 2");
            Assert.IsTrue(PassageRanker.FuzzyContains(words, "turin"), "distance 1");
            Assert.IsFalse(PassageRanker.FuzzyContains(words, "was"), "too short to be fuzzy");
            Assert.IsFalse(PassageRanker.FuzzyContains(words, "french"));
        }

        [Test]
        public void DuckDuckGo_AbstractParsed()
        {
            var passages = SearchSources.ParseDuckDuckGo(Fixture("ddg_turing.json"));
            Assert.IsNotEmpty(passages);
            StringAssert.Contains("Turing", passages[0].Text);
        }

        [Test]
        public void BingNews_HeadlinesParsed()
        {
            var news = SearchSources.ParseRss(Fixture("bingnews_apple_ko.xml"), PassageKind.News);
            Assert.GreaterOrEqual(news.Count, 3);
            foreach (var n in news)
            {
                Assert.IsNotEmpty(n.Title);
                StringAssert.DoesNotContain(" on MSN", n.Source);
                StringAssert.StartsWith("http", n.Url);
            }
            Debug.Log("[News] " + string.Join(" | ", news.Take(3).Select(n => $"{n.Source}: {n.Title}")));
        }

        [Test]
        public void Text_Helpers()
        {
            Assert.AreEqual("블랙홀은 시공간 영역이다.", InfoText.Speakable("블랙홀(영어: black hole)은 시공간 영역이다.[1]"));
            Assert.AreEqual("Alan Turing (23 June 1912 – 7 June 1954) was", InfoText.Speakable("Alan Turing (23 June 1912 – 7 June 1954) was", keepNumbers: true));
            Assert.AreEqual("Alan Turing was", InfoText.Speakable("Alan Turing (23 June 1912 – 7 June 1954) was"));
            Assert.AreEqual("서울은", InfoText.Topic("서울"));
            Assert.AreEqual("도쿄는", InfoText.Topic("도쿄"));
            Assert.AreEqual("애플, 새 아이폰 공개", InfoText.StripOutlet("애플, 새 아이폰 공개 - 조선일보"));
            Assert.AreEqual("베젤 두꺼워진 애플워치12… 불만", InfoText.StripOutlet("베젤 두꺼워진 애플워치12… 불만 - 조선비즈 - Chosunbiz"));
            Assert.AreEqual(2, InfoText.Sentences("첫 문장이다. 두 번째 문장이다.").Count);
            Assert.LessOrEqual(InfoText.Truncate(new string('가', 300), 100).Length, 101);
        }

        [TestCase("한국의 수도는 어디야?", "한국", "capital")]
        [TestCase("대한민국 수도가 어디야", "대한민국", "capital")]
        [TestCase("What's the capital of South Korea?", "South Korea", "capital")]
        [TestCase("the capital city of the soul", "soul", "capital")]
        [TestCase("일본 인구는 몇 명이야", "일본", "population")]
        [TestCase("How many people live in Japan?", "Japan", "population")]
        [TestCase("이순신 장군은 언제 태어났어", "이순신 장군", "birthday")]
        [TestCase("When was Alan Turing born?", "Alan Turing", "birthday")]
        [TestCase("How tall is the Eiffel Tower?", "Eiffel Tower", "height")]
        [TestCase("에베레스트산 높이가 얼마야", "에베레스트산", "height")]
        [TestCase("애플 창업자가 누구야", "애플", "founder")]
        [TestCase("Who founded Apple?", "Apple", "founder")]
        [TestCase("When was Google founded?", "Google", "founding date")]
        [TestCase("Who is the president of France?", "France", "head of state")]
        public void FactQuestion_FindsEntityAndProperty(string text, string entity, string property)
        {
            var q = Wikidata.Parse(text);
            Assert.IsNotNull(q, text);
            Assert.AreEqual(entity, q.Entity);
            Assert.AreEqual(property, q.Property.En);
        }

        [TestCase("블랙홀이 뭐야?")]
        [TestCase("Who was Alan Turing?")]
        [TestCase("아이유가 누구야")]
        [TestCase("Who was the first president of the United States?")]
        [TestCase("대한민국 초대 대통령은 누구야?")]
        [TestCase("What was the population of Seoul in 1990?")]
        public void FactQuestion_NullForOpenQuestions(string text) => Assert.IsNull(Wikidata.Parse(text), text);

        [TestCase("Who directed The Lion King?", "director")]
        [TestCase("노벨상 수상자는 누구야", null)]
        public void FactQuestion_EarliestPropertyWins(string text, string property) => Assert.AreEqual(property, Wikidata.Parse(text)?.Property.En, text);

        [Test]
        public void Wikidata_DecadesArentYearsAndPluralsStay()
        {
            Assert.IsNull(Wikidata.Date("+1500-00-00T00:00:00Z", 8, Lang.En), "a decade is not the year 1500");
            var headquarters = Wikidata.Properties.First(p => p.En == "headquarters");
            Assert.AreEqual("The headquarters of X are A and B.", Wikidata.Sentence(headquarters, "X", new List<string> { "A", "B" }, Lang.En));
        }

        [Test]
        public void FirstSentences_KeepsTwo()
        {
            Assert.AreEqual("It's 17.9°C now. Rain later.", InfoText.FirstSentences("It's 17.9°C now. Rain later. Stay dry!", 2), "a decimal point is not a sentence end");
            Assert.AreEqual("비가 와요. 우산 챙기세요.", InfoText.FirstSentences("비가 와요. 우산 챙기세요. 내일은 맑아요.", 2));
        }

        [Test]
        public void Truncate_KeepsDecimals()
        {
            var text = "The highest mountain above sea level, Mount Everest, is 8,848.86 metres high on the border of Nepal and China";
            Assert.Greater(text.IndexOf('.'), 40);
            StringAssert.DoesNotEndWith("8,848.", InfoText.Truncate(text, 80));
        }

        [Test]
        public void FactQuestion_CapitalRegionIsNotCapital() => Assert.AreEqual("population", Wikidata.Parse("수도권 인구가 얼마야").Property.En);

        static Fact Pick(string fixture, string property, Lang lang) =>
            Wikidata.Pick(Wikidata.ParseRows(Fixture($"wikidata_{fixture}.json")), Wikidata.Properties.First(p => p.En == property), lang);

        [Test]
        public void Wikidata_PicksCurrentStatementsAndSpeaks()
        {
            var capital = Pick("korea_capital_ko", "capital", Lang.Ko);
            Assert.AreEqual("Q884", capital.EntityId, "the first search candidate, not 대한제국");
            Assert.AreEqual("대한민국의 수도는 서울특별시예요.", capital.Speech);

            Assert.AreEqual("The population of South Korea is about 51.6 million people.", Pick("korea_population_en", "population", Lang.En).Speech,
                "the latest point in time (2022), not the older preferred one");
            Assert.AreEqual("The height of Eiffel Tower is 330 metres.", Pick("eiffel_height_en", "height", Lang.En).Speech, "the preferred statement");
            Assert.AreEqual("The head of state of France is Emmanuel Macron.", Pick("france_head_en", "head of state", Lang.En).Speech,
                "former presidents have an end time; the label comes from 'mul'");
            Assert.AreEqual("앨런 튜링은 1912년 6월 23일에 태어났어요.", Pick("turing_born_ko", "birthday", Lang.Ko).Speech);
            Assert.AreEqual("The founders of Apple Inc. are Steve Jobs, Steve Wozniak and Ronald Wayne.", Pick("apple_founder_en", "founder", Lang.En).Speech);

            var population = Wikidata.Properties.First(p => p.En == "population");
            Assert.AreEqual("약 5,163만 명", Wikidata.Quantity("51628117", "http://www.wikidata.org/entity/Q199", population, Lang.Ko));
            Assert.AreEqual("약 1억 2,398만 명", Wikidata.Quantity("+123975371", "1", population, Lang.Ko));

            Assert.AreEqual("June 23, 1912", Wikidata.Date("+1912-06-23T00:00:00Z", 11, Lang.En));
            Assert.AreEqual("1912년 6월 23일", Wikidata.Date("+1912-06-23T00:00:00Z", 11, Lang.Ko));
            Assert.AreEqual("1998", Wikidata.Date("+1998-00-00T00:00:00Z", 9, Lang.En));

            var born = Wikidata.Properties.First(p => p.En == "birthday");
            var capitalProperty = Wikidata.Properties.First(p => p.En == "capital");
            Assert.AreEqual("대한민국의 수도는 서울특별시예요.", Wikidata.Sentence(capitalProperty, "대한민국", new() { "서울특별시" }, Lang.Ko));
            Assert.AreEqual("The capital of South Korea is Seoul.", Wikidata.Sentence(capitalProperty, "South Korea", new() { "Seoul" }, Lang.En));
            Assert.AreEqual("앨런 튜링은 1912년 6월 23일에 태어났어요.", Wikidata.Sentence(born, "앨런 튜링", new() { "1912년 6월 23일" }, Lang.Ko));
            Assert.AreEqual("Alan Turing was born on June 23, 1912.", Wikidata.Sentence(born, "Alan Turing", new() { "June 23, 1912" }, Lang.En));
            Assert.AreEqual("대한민국의 인구는 약 5,163만 명이에요.", Wikidata.Sentence(population, "대한민국", new() { "약 5,163만 명" }, Lang.Ko));
        }

        /// <summary>Answers from fixtures by URL fragment; anything else fails like the network would.</summary>
        sealed class FixtureWeb : IWebClient
        {
            public int CallsLeft = int.MaxValue;
            public readonly List<string> Requests = new();
            readonly (string Fragment, string Body)[] m_Routes;

            public FixtureWeb(params (string Fragment, string Body)[] routes) => m_Routes = routes;

            public bool CanAffordWikimedia(int calls) => CallsLeft >= calls;

            public async Awaitable<string> GetAsync(string url, System.Threading.CancellationToken cancel = default)
            {
                await Awaitable.NextFrameAsync();
                Requests.Add(url);
                foreach (var (fragment, body) in m_Routes)
                    if (url.Contains(fragment)) return body;
                throw new WebException($"no fixture for {url}");
            }
        }

        static IEnumerator AnswerFact(FixtureWeb web, string question, Lang lang, Action<Fact> check)
        {
            var task = Wikidata.AnswerAsync(web, Wikidata.Parse(question), lang, default);
            var awaiter = task.GetAwaiter();
            while (!awaiter.IsCompleted) yield return null;
            check(awaiter.GetResult());
        }

        [UnityTest]
        public IEnumerator Wikidata_ActionApiWhileTheBudgetAllows()
        {
            var korea = new FixtureWeb(("wbsearchentities", Fixture("wikidata_api_search_korea_ko.json")), ("wbgetclaims", Fixture("wikidata_api_korea_population.json")));
            yield return AnswerFact(korea, "대한민국 인구는 몇 명이야?", Lang.Ko, f => Assert.AreEqual("대한민국의 인구는 약 5,163만 명이에요.", f.Speech));
            Assert.IsFalse(korea.Requests.Any(u => u.Contains("query.wikidata.org")));

            var france = new FixtureWeb(("wbsearchentities", "{\"search\":[{\"id\":\"Q142\",\"label\":\"France\"}]}"),
                ("wbgetclaims", Fixture("wikidata_api_france_head.json")), ("wbgetentities", Fixture("wikidata_api_labels_macron.json")));
            yield return AnswerFact(france, "Who is the president of France?", Lang.En, f => Assert.AreEqual("The head of state of France is Emmanuel Macron.", f.Speech, "the label only exists in 'mul'"));
            Assert.AreEqual(3, france.Requests.Count, "search, statements, labels");
        }

        [UnityTest]
        public IEnumerator Wikidata_QueryServiceWhenTheBudgetIsUsedUp()
        {
            var web = new FixtureWeb(("query.wikidata.org", Fixture("wikidata_korea_capital_ko.json"))) { CallsLeft = 1 };
            yield return AnswerFact(web, "한국의 수도는 어디야?", Lang.Ko, f => Assert.AreEqual("대한민국의 수도는 서울특별시예요.", f.Speech));
            Assert.IsTrue(web.Requests.All(u => u.Contains("query.wikidata.org")));

            var midway = new FixtureWeb(("query.wikidata.org", Fixture("wikidata_korea_capital_ko.json")));
            yield return AnswerFact(midway, "한국의 수도는 어디야?", Lang.Ko, f => Assert.AreEqual("대한민국의 수도는 서울특별시예요.", f.Speech, "a failed Action API call falls back too"));
        }

        [TestCase("Alan Turing", "Alllan Turing", true)]
        [TestCase("Alan Turing", "Alan Turing", true)]
        [TestCase("Allan (ship)", "Alllan Turing", false)]
        public void TitleCovers_AllowsMisheardNames(string title, string query, bool covers) => Assert.AreEqual(covers, PassageRanker.TitleCovers(title, query));

        [Test]
        public void Ranker_SkipsListIntroductions()
        {
            var list = new Passage { Kind = PassageKind.Wiki, Source = "위키백과", Title = "한국의 수도", Text = "다음은 한국사에 존재했던 여러 나라들의 수도이다." };
            Assert.IsEmpty(new PassageRanker().Rank("한국의 수도는 어디야?", "한국의 수도", new[] { list }));
        }

        [TestCase("대한민국의 수도는 서울특별시예요.", SentisModels.SupertonicLanguage.ko)]
        [TestCase("Seoul is the capital of South Korea.", SentisModels.SupertonicLanguage.en)]
        [TestCase("IU (아이유) is a singer.", SentisModels.SupertonicLanguage.en)]
        public void Speech_LanguageFollowsText(string text, SentisModels.SupertonicLanguage expected) => Assert.AreEqual(expected, SpeechOutput.LanguageOf(text));

        [TestCase(SentisModels.SenseVoiceLanguage.Ja, SentisModels.SenseVoiceLanguage.Ko)]
        [TestCase(SentisModels.SenseVoiceLanguage.Zh, SentisModels.SenseVoiceLanguage.En)]
        public void Stt_NeighbourLanguagesAreRetried(SentisModels.SenseVoiceLanguage detected, SentisModels.SenseVoiceLanguage retry) => Assert.AreEqual(retry, SpeechLanguage.Retry(detected));

        [Test]
        public void Stt_EnglishAndKoreanAreKept()
        {
            Assert.IsNull(SpeechLanguage.Retry(SentisModels.SenseVoiceLanguage.En));
            Assert.IsNull(SpeechLanguage.Retry(SentisModels.SenseVoiceLanguage.Ko));
        }

        static InfoAgent s_Agent;

        static IEnumerator Ask(string id, string text, Lang lang, Action<InfoAnswer> check)
        {
            // Wikimedia's anonymous budget is shared per IP: wait for it (and retry once on a 429) rather than skip.
            s_Agent ??= new InfoAgent(new UnityWebClient { MaxBudgetWaitSeconds = 60f });
            InfoAnswer answer = null;
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var task = s_Agent.AnswerAsync(id, text, lang);
                var awaiter = task.GetAwaiter();
                while (!awaiter.IsCompleted) yield return null;
                answer = awaiter.GetResult();
                if (answer == null || !answer.Steps.Any(s => s.Contains("429") || s.Contains("paused") || s.Contains("budget"))) break;
                Debug.Log($"[Live] '{text}' hit the Wikimedia limit; asking again in 35 s");
                var end = Time.realtimeSinceStartup + 35f;
                while (Time.realtimeSinceStartup < end) yield return null;
            }
            Debug.Log($"[Live] {id} '{text}' → {answer?.Speech} (source: {answer?.Sources}, {answer?.Ms:F0} ms)\n    " + string.Join("\n    ", answer?.Steps ?? new List<string>()));
            check(answer);
        }

        [UnityTest, Category("Network")]
        public IEnumerator Live_Weather()
        {
            yield return Ask("get_weather", "What's the weather like in Tokyo?", Lang.En, a => { Assert.IsTrue(a.Found); StringAssert.Contains("in Tokyo right now", a.Speech); });
            yield return Ask("get_weather", "what's the weather", Lang.En, a => { Assert.IsTrue(a.Found); StringAssert.Contains("(IP)", a.Sources); });
            yield return Ask("get_weather", "will it rain tomorrow in London", Lang.En, a => { Assert.IsTrue(a.Found); StringAssert.StartsWith("Tomorrow in London", a.Speech); });
        }

        [UnityTest, Category("Network")]
        public IEnumerator Live_TimeAndLocation()
        {
            yield return Ask("get_time", "what time is it in new york", Lang.En, a => { Assert.IsNotNull(a); StringAssert.Contains("New York", a.Speech); });
            yield return Ask("get_time", "what time is it", Lang.En, Assert.IsNull);
            yield return Ask("get_location", "where am i", Lang.En, a => { Assert.IsTrue(a.Found); StringAssert.Contains("IP address", a.Speech); });
        }

        [UnityTest, Category("Network")]
        public IEnumerator Live_Search()
        {
            yield return Ask("web_search", "What is a black hole?", Lang.En, a => { Assert.IsTrue(a.Found); StringAssert.Contains("black hole", a.Speech.ToLowerInvariant()); });
            yield return Ask("web_search", "Who was Alan Turing?", Lang.En, a => { Assert.IsTrue(a.Found); StringAssert.Contains("Turing", a.Speech); });
            yield return Ask("web_search", "Who was Ellan Turing?", Lang.En, a => { Assert.IsTrue(a.Found, "misheard name"); StringAssert.Contains("Alan Mathison Turing", a.Speech); });
            yield return Ask("web_search", "Who was Alllan Turing?", Lang.En, a => { Assert.IsTrue(a.Found, "misheard name with lookalike articles"); StringAssert.Contains("Alan Mathison Turing", a.Speech); });
            yield return Ask("web_search", "When was Alan Turing born?", Lang.En, a => { Assert.IsTrue(a.Found); StringAssert.Contains("1912", a.Speech); });
            yield return Ask("web_search", "How tall is the Eiffel Tower?", Lang.En, a => { Assert.IsTrue(a.Found); StringAssert.IsMatch(@"\d+ (metres|meters|m|ft)\b", a.Speech); });
            yield return Ask("web_search", "Any news about Nvidia?", Lang.En, a => { Assert.IsTrue(a.Found); StringAssert.StartsWith("Here's the latest on", a.Speech); });
            yield return Ask("web_search", "What's the capital of South Korea?", Lang.En, a => { Assert.IsTrue(a.Found); StringAssert.Contains("Seoul", a.Speech); StringAssert.StartsWith("Wikidata", a.Sources); });
        }

        [UnityTest, Category("Network")]
        public IEnumerator Live_SearchKorean()
        {
            yield return Ask("web_search", "한국의 수도는 어디야?", Lang.Ko, a => { Assert.IsTrue(a.Found); StringAssert.Contains("서울", a.Speech); });
            yield return Ask("web_search", "일본 인구는 몇 명이야?", Lang.Ko, a => { Assert.IsTrue(a.Found); StringAssert.Contains("만 명", a.Speech); });
            yield return Ask("web_search", "애플 창업자가 누구야?", Lang.Ko, a => { Assert.IsTrue(a.Found); StringAssert.Contains("스티브 잡스", a.Speech); });
            yield return Ask("web_search", "에베레스트산 높이가 얼마야?", Lang.Ko, a => { Assert.IsTrue(a.Found); StringAssert.IsMatch(@"8,8\d\d(\.\d+)?미터", a.Speech); });
            yield return Ask("web_search", "아이유가 누구야?", Lang.Ko, a => { Assert.IsTrue(a.Found); StringAssert.Contains("가수", a.Speech); });
            yield return Ask("web_search", "블랙홀이 뭐야?", Lang.Ko, a => { Assert.IsTrue(a.Found); StringAssert.Contains("블랙홀", a.Speech); });
        }
    }
}
