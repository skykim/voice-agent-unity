using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace VoiceAgent.Info
{
    /// <summary>A question about one property of one thing: "What's the capital of South Korea?" → (South Korea, capital).</summary>
    public sealed class FactQuestion
    {
        public string Entity;
        public Wikidata.Property Property;

        public override string ToString() => $"{Entity} / {Property.En}";
    }

    public sealed class Fact
    {
        public string EntityId, EntityLabel, Speech;
        public List<string> Values = new();
    }

    /// <summary>
    /// Structured answers for property questions (capital, population, birthday, height, founder, …): find the entity
    /// with Wikidata's label search, read the property's current statement and say it in one sentence. Text search
    /// answers everything else, and also these questions when Wikidata has no entity or statement for them.
    /// </summary>
    public static class Wikidata
    {
        public enum Kind { Item, Quantity, Time }

        /// <summary>How the answer sentence is built: "The capital of X is Y" or "X was born on Y".</summary>
        public enum Style { Noun, Born, Died, Founded }

        public sealed class Property
        {
            public string[] Ids;
            public Kind Kind;
            public Style Style;
            public string En, Ko;
            public Regex Ask;
        }

        static Property P(string ids, Kind kind, Style style, string en, string ko, string ask) => new()
        {
            Ids = ids.Split(','), Kind = kind, Style = style, En = en, Ko = ko,
            Ask = new Regex(ask, RegexOptions.IgnoreCase | RegexOptions.Compiled),
        };

        // The earliest match in the question wins ("who directed The Lion King" asks for the director, not a king); on a
        // tie the first in this list, so "who founded" comes before "when was … founded".
        public static readonly Property[] Properties =
        {
            P("P36", Kind.Item, Style.Noun, "capital", "수도", @"capital city|\bcapital\b|수도(?!권|원)"),
            P("P1082", Kind.Quantity, Style.Noun, "population", "인구", @"population|how many people|인구"),
            P("P112", Kind.Item, Style.Noun, "founder", "창립자", @"founders?\b|founded by|who (founded|started|created)|창업자|창립자|설립자|누가 (만들|세웠|설립|창립)"),
            P("P571", Kind.Time, Style.Founded, "founding date", "설립일", @"(?<=when (was|were|did) .+ )(founded|established|built|created|opened)\b|언제 (설립|창립|세워|만들어|지어|건국)|설립(연도|일)|창립(연도|일)|건국"),
            P("P169", Kind.Item, Style.Noun, "CEO", "CEO", @"\bceo\b|chief executive|최고경영자|대표이사"),
            P("P35", Kind.Item, Style.Noun, "head of state", "국가원수", @"president|head of state|\bking\b|\bqueen\b|대통령|국가원수|국왕"),
            P("P6", Kind.Item, Style.Noun, "head of government", "정부 수반", @"prime minister|\bmayor\b|head of government|총리|수상(?!자|식|작)"),
            P("P38", Kind.Item, Style.Noun, "currency", "화폐", @"currency|화폐|통화"),
            P("P37", Kind.Item, Style.Noun, "official language", "공용어", @"official languages?|what languages?|공용어|무슨 언어|어떤 언어"),
            P("P2046", Kind.Quantity, Style.Noun, "area", "면적", @"\barea\b|how (big|large) is|면적|넓이"),
            P("P2048,P2044", Kind.Quantity, Style.Noun, "height", "높이", @"how (tall|high)|\bheight\b|\belevation\b|높이|얼마나 높|해발"),
            P("P2043", Kind.Quantity, Style.Noun, "length", "길이", @"how long is|\blength\b|길이"),
            P("P569", Kind.Time, Style.Born, "birthday", "생일", @"\bborn\b|birthday|date of birth|생일|태어났|태어난|출생"),
            P("P570", Kind.Time, Style.Died, "date of death", "사망일", @"\bdied\b|\bdie\b|death|사망|죽었|죽은|돌아가"),
            P("P159", Kind.Item, Style.Noun, "headquarters", "본사", @"headquarter|본사"),
            P("P50", Kind.Item, Style.Noun, "author", "작가", @"who wrote|\bauthor\b|written by|작가|저자|누가 썼|쓴 사람"),
            P("P57", Kind.Item, Style.Noun, "director", "감독", @"who directed|\bdirector\b|directed by|감독"),
            P("P26", Kind.Item, Style.Noun, "spouse", "배우자", @"\bwife\b|\bhusband\b|spouse|married to|남편|아내|부인|배우자"),
            P("P17", Kind.Item, Style.Noun, "country", "나라", @"which country|what country|어느 나라|무슨 나라"),
        };

        static readonly HashSet<string> s_EnStop = new()
        {
            "what", "whats", "who", "whos", "when", "where", "which", "how", "is", "was", "are", "were", "the", "a", "an", "of", "in",
            "does", "did", "do", "has", "have", "many", "much", "people", "live", "living", "tell", "me", "about", "please", "its", "it",
            "hey", "nova", "there", "current", "now", "today", "city", "s", "by", "to", "and", "for", "on", "at", "can", "you", "i",
            "know", "official", "big", "large", "tall", "high", "long", "exactly", "approximately", "roughly",
        };
        static readonly HashSet<string> s_KoStop = new() { "지금", "현재", "혹시", "그", "정확히", "대략", "알려줘", "알아", "좀", "나라", "도시" };

        // Only the current statement is read, so "the first president" or "the 1990 population" goes to text search.
        static readonly Regex s_NotCurrent = new(@"\b(first|last|former|previous|next|original)\b|\bin (1\d|20)\d\d\b|(1\d|20)\d\d년|초대|첫|역대|이전|전임|전직|전 (대통령|총리|국왕|왕)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>Null when the question isn't about a known property, names no entity, or asks about a past holder.</summary>
        public static FactQuestion Parse(string text)
        {
            var clean = TextScan.ReplaceRuns(text ?? string.Empty, TextScan.Punctuation + "\"“”", " ").Trim();
            if (clean.Length == 0 || s_NotCurrent.IsMatch(clean)) return null;
            var (property, match) = Properties.Select(p => (Property: p, Match: p.Ask.Match(clean))).Where(m => m.Match.Success)
                .OrderBy(m => m.Match.Index).FirstOrDefault();
            if (property == null) return null;
            // Korean puts the entity before the property ("Korea's capital is where"); English on either side.
            var before = clean[..match.Index];
            var after = clean[(match.Index + match.Length)..];
            var entity = LangDetect.Of(match.Value) == Lang.Ko ? EntityPhrase(before) : string.Empty;
            if (entity.Length == 0) entity = EntityPhrase(before + " " + after);
            return entity.Length == 0 ? null : new FactQuestion { Entity = entity, Property = property };
        }

        static string EntityPhrase(string rest)
        {
            var words = new List<string>();
            foreach (var raw in rest.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                var word = raw.Trim('\'', '’');
                if (word.EndsWith("'s", StringComparison.OrdinalIgnoreCase) || word.EndsWith("’s", StringComparison.OrdinalIgnoreCase)) word = word[..^2];
                if (word.Length == 0) continue;
                if (word[0] >= '가')
                {
                    if (InfoText.IsQuestionWord(word) || s_KoStop.Contains(word) || s_KoTail.Contains(word)) continue;
                    word = InfoText.StripParticle(word);
                    if (s_KoStop.Contains(word)) continue;
                }
                else if (s_EnStop.Contains(word.ToLowerInvariant()) || InfoText.IsQuestionWord(word)) continue;
                words.Add(word);
            }
            return string.Join(" ", words);
        }

        // Korean predicate words left after the property ("is how much", "is in", "becomes").
        static readonly HashSet<string> s_KoTail = new()
        {
            "야", "이야", "예요", "이에요", "인가요", "일까", "니", "나요", "있어", "있니", "있나요", "돼", "되니", "되나요", "몇", "명", "명이야", "얼마나",
            "했어", "했니", "됐어", "됐니", "어디야", "누구야", "뭐야",
        };

        /// <summary>Wikipedia/Wikidata API calls the fast path usually needs: search, statements, labels.</summary>
        public const int ActionApiCalls = 3;

        /// <summary>
        /// The Action API answers in ~0.3 s per call but spends the anonymous budget; the Query Service has its own,
        /// looser limit but takes 0.6–13 s. So: the Action API while the budget allows, the Query Service otherwise.
        /// </summary>
        public static async Awaitable<Fact> AnswerAsync(IWebClient web, FactQuestion question, Lang lang, CancellationToken cancel)
        {
            if (web.CanAffordWikimedia(ActionApiCalls))
            {
                try
                {
                    return await ActionApiAsync(web, question, lang, cancel);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    // Budget used up, a 429 midway or an error page instead of JSON: the Query Service still works.
                }
            }
            return await QueryServiceAsync(web, question, lang, cancel);
        }

        static async Awaitable<Fact> ActionApiAsync(IWebClient web, FactQuestion question, Lang lang, CancellationToken cancel)
        {
            var language = lang == Lang.Ko ? "ko" : "en";
            foreach (var phrase in EntityVariants(question.Entity))
            {
                var candidates = ParseSearch(await web.GetAsync($"https://www.wikidata.org/w/api.php?action=wbsearchentities&search={Uri.EscapeDataString(phrase)}" +
                                                                $"&language={language}&uselang={language}&type=item&limit=3&format=json", cancel));
                if (candidates.Count == 0) continue;
                foreach (var id in question.Property.Ids)
                    // The first candidate in search order that has the property wins; one request at a time.
                    foreach (var (entityId, label) in candidates.Take(2))
                    {
                        var claims = await web.GetAsync($"https://www.wikidata.org/w/api.php?action=wbgetclaims&entity={entityId}&property={id}&format=json", cancel);
                        var statements = CurrentStatements(claims, id, question.Property.Kind);
                        if (statements.Count == 0) continue;
                        var values = await ValuesAsync(web, statements, question.Property, lang, cancel);
                        if (values.Count == 0) continue;
                        return new Fact { EntityId = entityId, EntityLabel = label, Values = values, Speech = Sentence(question.Property, label, values, lang) };
                    }
            }
            return null;
        }

        static async Awaitable<Fact> QueryServiceAsync(IWebClient web, FactQuestion question, Lang lang, CancellationToken cancel)
        {
            foreach (var phrase in EntityVariants(question.Entity))
                foreach (var id in question.Property.Ids)
                {
                    var url = "https://query.wikidata.org/sparql?format=json&query=" + Uri.EscapeDataString(Query(phrase, id, lang));
                    var fact = Pick(ParseRows(await web.GetAsync(url, cancel)), question.Property, lang);
                    if (fact != null) return fact;
                }
            return null;
        }

        /// <summary>Dates stay strings: Newtonsoft would turn "1912-06-23T00:00:00Z" into a local DateTime.</summary>
        static JObject Json(string json) => JsonConvert.DeserializeObject<JObject>(json, new JsonSerializerSettings { DateParseHandling = DateParseHandling.None });

        public static List<(string Id, string Label)> ParseSearch(string json)
        {
            var list = new List<(string, string)>();
            if (Json(json)?["search"] is not JArray results) return list;
            foreach (var r in results)
            {
                var id = (string)r["id"];
                var label = (string)r["display"]?["label"]?["value"] ?? (string)r["label"];
                if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(label)) list.Add((id, label));
            }
            return list;
        }

        /// <summary>
        /// Action API statements that still hold: not deprecated, no end time; the latest "point in time" for series
        /// such as population, otherwise preferred rank first.
        /// </summary>
        public static List<JToken> CurrentStatements(string json, string property, Kind kind)
        {
            if (Json(json)?["claims"]?[property] is not JArray claims) return new List<JToken>();
            var live = claims.Where(c => (string)c["rank"] != "deprecated" && c["mainsnak"]?["datavalue"] != null && c["qualifiers"]?["P582"] == null).ToList();
            if (live.Count == 0) return live;
            var dated = live.Where(c => c["qualifiers"]?["P585"] != null).ToList();
            if (kind == Kind.Quantity && dated.Count > 0)
                return new List<JToken> { dated.OrderByDescending(c => (string)c["qualifiers"]["P585"][0]["datavalue"]?["value"]?["time"], StringComparer.Ordinal).First() };
            var preferred = live.Where(c => (string)c["rank"] == "preferred").ToList();
            var chosen = preferred.Count > 0 ? preferred : live;
            return kind == Kind.Item ? chosen.Take(3).ToList() : chosen.Take(1).ToList();
        }

        static async Awaitable<List<string>> ValuesAsync(IWebClient web, List<JToken> statements, Property property, Lang lang, CancellationToken cancel)
        {
            var values = statements.Select(s => s["mainsnak"]["datavalue"]["value"]).ToList();
            switch (property.Kind)
            {
                case Kind.Quantity:
                    return values.Select(v => Quantity((string)v["amount"], (string)v["unit"], property, lang)).Where(v => v != null).ToList();
                case Kind.Time:
                    return values.Select(v => Date((string)v["time"], (int?)v["precision"] ?? 11, lang)).Where(v => v != null).ToList();
                default:
                    var ids = values.Select(v => (string)v["id"]).Where(id => id != null).ToList();
                    if (ids.Count == 0) return new List<string>();
                    // Many labels exist only in "mul" (the default for every language).
                    var order = lang == Lang.Ko ? new[] { "ko", "mul", "en" } : new[] { "en", "mul" };
                    var entities = Json(await web.GetAsync($"https://www.wikidata.org/w/api.php?action=wbgetentities&ids={string.Join("|", ids)}" +
                                                           $"&props=labels&languages={string.Join("|", order)}&format=json", cancel))?["entities"];
                    return ids.Select(id => order.Select(l => (string)entities?[id]?["labels"]?[l]?["value"]).FirstOrDefault(v => !string.IsNullOrEmpty(v)))
                        .Where(l => l != null).ToList();
            }
        }

        /// <summary>The phrase itself, then without its last word, then without its first (a name followed by a title).</summary>
        static IEnumerable<string> EntityVariants(string entity)
        {
            yield return entity;
            var words = entity.Split(' ');
            if (words.Length < 2) yield break;
            yield return string.Join(" ", words[..^1]);
            yield return string.Join(" ", words[1..]);
        }

        /// <summary>
        /// One SPARQL request does everything: the label search behind Wikidata's search box (top 3 candidates),
        /// the property's statements that still hold (not deprecated, no end time), quantity/time details, the
        /// "point in time" of series such as population, and labels in the question's language.
        /// </summary>
        public static string Query(string phrase, string property, Lang lang)
        {
            var language = lang == Lang.Ko ? "ko" : "en";
            var search = new string(phrase.Select(c => c is '"' or '\\' or '{' or '}' ? ' ' : c).ToArray());
            return "SELECT ?item ?itemLabel ?num ?value ?valueLabel ?amount ?unit ?time ?precision ?when ?rank WHERE {\n" +
                   $"  SERVICE wikibase:mwapi {{ bd:serviceParam wikibase:endpoint \"www.wikidata.org\"; wikibase:api \"EntitySearch\"; mwapi:search \"{search}\"; mwapi:language \"{language}\". " +
                   "?item wikibase:apiOutputItem mwapi:item. ?num wikibase:apiOrdinal true. }\n" +
                   "  FILTER(?num < 3)\n" +
                   $"  ?item p:{property} ?st. ?st ps:{property} ?value; wikibase:rank ?rank.\n" +
                   "  FILTER(?rank != wikibase:DeprecatedRank) FILTER NOT EXISTS { ?st pq:P582 [] }\n" +
                   $"  OPTIONAL {{ ?st psv:{property} [ wikibase:quantityAmount ?amount; wikibase:quantityUnit ?unit ] }}\n" +
                   $"  OPTIONAL {{ ?st psv:{property} [ wikibase:timeValue ?time; wikibase:timePrecision ?precision ] }}\n" +
                   "  OPTIONAL { ?st pq:P585 ?when }\n" +
                   $"  SERVICE wikibase:label {{ bd:serviceParam wikibase:language \"{language},mul,en\". }}\n" +
                   "} ORDER BY ?num DESC(?when) LIMIT 300";
        }

        public sealed class Row
        {
            public int Num, Precision;
            public string Item, ItemLabel, ValueLabel, Amount, Unit, Time, When;
            public bool Preferred;
        }

        public static List<Row> ParseRows(string json)
        {
            var rows = new List<Row>();
            if (Json(json)?["results"]?["bindings"] is not JArray bindings) return rows;
            foreach (var b in bindings)
            {
                // "Unknown value" statements come back as blank nodes.
                if ((string)b["value"]?["type"] == "bnode") continue;
                string V(string name) => (string)b[name]?["value"];
                rows.Add(new Row
                {
                    Num = int.TryParse(V("num"), out var num) ? num : 0,
                    Precision = int.TryParse(V("precision"), out var precision) ? precision : 11,
                    Item = V("item"), ItemLabel = V("itemLabel"), ValueLabel = V("valueLabel"),
                    Amount = V("amount"), Unit = V("unit"), Time = V("time"), When = V("when"),
                    Preferred = V("rank")?.EndsWith("PreferredRank", StringComparison.Ordinal) == true,
                });
            }
            return rows;
        }

        /// <summary>"Q884": an item without a label in the asked language.</summary>
        static bool IsEntityId(string label) => label.Length > 1 && label[0] == 'Q' && IsDigits(label[1..]);

        static bool IsDigits(string s) => s.Length > 0 && s.All(c => c is >= '0' and <= '9');

        /// <summary>
        /// The first candidate (in search order) with a usable value. Series such as population give their latest point
        /// in time; otherwise preferred statements win. Items give up to three values ("founders").
        /// </summary>
        public static Fact Pick(List<Row> rows, Property property, Lang lang)
        {
            foreach (var group in rows.GroupBy(r => r.Item).OrderBy(g => g.Min(r => r.Num)))
            {
                var statements = group.ToList();
                var label = statements[0].ItemLabel;
                if (string.IsNullOrEmpty(label) || IsEntityId(label)) continue;
                var preferred = statements.Where(r => r.Preferred).ToList();
                var chosen = preferred.Count > 0 ? preferred : statements;
                List<string> values;
                switch (property.Kind)
                {
                    case Kind.Quantity:
                        var dated = statements.Where(r => r.When != null && r.Amount != null).ToList();
                        var row = dated.Count > 0 ? dated.OrderByDescending(r => r.When, StringComparer.Ordinal).First() : chosen.FirstOrDefault(r => r.Amount != null);
                        values = row == null ? new List<string>() : new List<string> { Quantity(row.Amount, row.Unit, property, lang) };
                        break;
                    case Kind.Time:
                        var time = chosen.FirstOrDefault(r => r.Time != null);
                        values = time == null ? new List<string>() : new List<string> { Date(time.Time, time.Precision, lang) };
                        break;
                    default:
                        values = chosen.Select(r => r.ValueLabel).Where(v => !string.IsNullOrEmpty(v) && !IsEntityId(v)).Distinct().Take(3).ToList();
                        break;
                }
                values = values.Where(v => v != null).ToList();
                if (values.Count == 0) continue;
                var id = group.Key[(group.Key.LastIndexOf('/') + 1)..];
                return new Fact { EntityId = id, EntityLabel = label, Values = values, Speech = Sentence(property, label, values, lang) };
            }
            return null;
        }

        static readonly Dictionary<string, (string En, string Ko)> s_Units = new()
        {
            ["Q11573"] = ("metres", "미터"), ["Q828224"] = ("kilometres", "킬로미터"), ["Q174728"] = ("centimetres", "센티미터"),
            ["Q712226"] = ("square kilometres", "제곱킬로미터"), ["Q25343"] = ("square metres", "제곱미터"), ["Q11570"] = ("kilograms", "킬로그램"),
            ["Q3710"] = ("feet", "피트"), ["Q253276"] = ("miles", "마일"),
        };

        public static string Quantity(string amount, string unit, Property property, Lang lang)
        {
            if (!double.TryParse(amount, NumberStyles.Float, CultureInfo.InvariantCulture, out var n)) return null;
            var number = n.ToString("#,0.##", CultureInfo.InvariantCulture);
            if (unit == "1" || unit?.EndsWith("/Q199", StringComparison.Ordinal) == true)
                return property.Ids[0] == "P1082" ? Count(n, lang) : number;
            var id = unit?[(unit.LastIndexOf('/') + 1)..] ?? string.Empty;
            if (!s_Units.TryGetValue(id, out var name)) return number;
            return lang == Lang.Ko ? $"{number}{name.Ko}" : $"{number} {name.En}";
        }

        /// <summary>51,628,117 → "about 51.6 million people"; Korean counts in units of 10,000 and 100,000,000.</summary>
        static string Count(double n, Lang lang)
        {
            var c = CultureInfo.InvariantCulture;
            if (lang == Lang.Ko)
            {
                var man = Math.Round(n / 1e4);
                if (man >= 1e4) return $"약 {Math.Floor(man / 1e4).ToString(c)}억 {(man % 1e4 > 0 ? (man % 1e4).ToString("#,0", c) + "만 " : string.Empty)}명";
                return n >= 1e4 ? $"약 {man.ToString("#,0", c)}만 명" : $"{n.ToString("#,0", c)}명";
            }
            if (n >= 1e9) return $"about {(n / 1e9).ToString("0.#", c)} billion people";
            return n >= 1e6 ? $"about {(n / 1e6).ToString("0.#", c)} million people" : $"{n.ToString("#,0", c)} people";
        }

        static readonly string[] s_Months = CultureInfo.InvariantCulture.DateTimeFormat.MonthNames;

        /// <summary>
        /// "+1912-06-23T00:00:00Z" → "June 23, 1912", year-month-day in Korean (precision 9 = year, 10 = month, 11 = day).
        /// Null for a decade or century (precision 8 or less), which a single year would misstate.
        /// </summary>
        public static string Date(string time, int precision, Lang lang)
        {
            if (precision < 9) return null;
            // "[+-]year-MM-DD…"; cosmological dates ("+13798000000-00-00") don't fit an int and aren't worth saying as a date.
            var t = time ?? string.Empty;
            var signed = t.Length > 0 && t[0] is '+' or '-';
            var bc = signed && t[0] == '-';
            var parts = t[(signed ? 1 : 0)..].Split('-');
            if (parts.Length < 3 || !IsDigits(parts[0]) || parts[1].Length != 2 || !IsDigits(parts[1]) || parts[2].Length < 2 || !IsDigits(parts[2][..2]) ||
                !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var year)) return null;
            int month = int.Parse(parts[1], CultureInfo.InvariantCulture), day = int.Parse(parts[2][..2], CultureInfo.InvariantCulture);
            var hasMonth = precision >= 10 && month is >= 1 and <= 12;
            var hasDay = hasMonth && precision >= 11 && day > 0;
            if (lang == Lang.Ko)
            {
                var y = bc ? $"기원전 {year}년" : $"{year}년";
                return hasDay ? $"{y} {month}월 {day}일" : hasMonth ? $"{y} {month}월" : y;
            }
            var ey = bc ? $"{year} BC" : year.ToString(CultureInfo.InvariantCulture);
            return hasDay ? $"{s_Months[month - 1]} {day}, {ey}" : hasMonth ? $"{s_Months[month - 1]} {ey}" : ey;
        }

        public static string Sentence(Property property, string entity, List<string> values, Lang lang)
        {
            var value = lang == Lang.Ko ? string.Join(", ", values) : JoinEn(values);
            if (lang == Lang.Ko)
            {
                return property.Style switch
                {
                    Style.Born => $"{InfoText.Topic(entity)} {value}에 태어났어요.",
                    Style.Died => $"{InfoText.Topic(entity)} {value}에 세상을 떠났어요.",
                    Style.Founded => $"{InfoText.Topic(entity)} {value}에 설립됐어요.",
                    _ => $"{entity}의 {InfoText.Topic(property.Ko)} {value}{Copula(value)}",
                };
            }
            // "June 23, 1912" is a day ("on"); "June 1912" or "1912" is not ("in").
            var on = values[0].Contains(',') ? "on" : "in";
            return property.Style switch
            {
                Style.Born => $"{entity} was born {on} {value}.",
                Style.Died => $"{entity} died {on} {value}.",
                Style.Founded => $"{entity} was founded {on} {value}.",
                _ => values.Count > 1 ? $"The {Plural(property.En)} of {entity} are {value}." : $"The {property.En} of {entity} is {value}.",
            };
        }

        /// <summary>"founder" → "founders", "head of state" → "heads of state", "country" → "countries", "headquarters" stays.</summary>
        static string Plural(string noun)
        {
            var cut = noun.IndexOf(" of ", StringComparison.Ordinal);
            var head = cut < 0 ? noun : noun[..cut];
            head = head.EndsWith("s", StringComparison.Ordinal) ? head
                : head.EndsWith("y", StringComparison.Ordinal) && !head.EndsWith("ey", StringComparison.Ordinal) ? head[..^1] + "ies" : head + "s";
            return cut < 0 ? head : head + noun[cut..];
        }

        static string JoinEn(List<string> values) =>
            values.Count <= 1 ? values.FirstOrDefault() ?? string.Empty : string.Join(", ", values.Take(values.Count - 1)) + " and " + values[^1];

        /// <summary>The Korean copula: chosen by the final consonant after a Hangul word, the formal form after digits or Latin letters.</summary>
        static string Copula(string value)
        {
            var last = value.TrimEnd().LastOrDefault();
            if (last < '가' || last > '힣') return "입니다.";
            return InfoText.HasBatchim(value) ? "이에요." : "예요.";
        }
    }
}
