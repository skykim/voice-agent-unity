using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Xml;
using System.Xml.Linq;
using UnityEngine;

namespace VoiceAgent.Info
{
    public enum PassageKind { Wiki, Abstract, Web, News }

    /// <summary>One piece of retrieved text with where it came from.</summary>
    public sealed class Passage
    {
        public PassageKind Kind;
        public string Source, Title, Text, Url, Date;
        /// <summary>English Wikipedia title of a Korean article (for the cross-lingual fallback).</summary>
        public string EnglishTitle;

        public override string ToString() => $"[{Source}] {Title}: {Text}";
    }

    /// <summary>
    /// Keyless sources: Wikipedia lead sections (Action API, ko/en), DuckDuckGo Instant Answer (abstracts),
    /// Bing web RSS (general snippets), and Google News / Bing News RSS (headlines). DuckDuckGo's HTML results are
    /// left out: they answer bursts from one IP with a 202 challenge.
    /// </summary>
    public static class SearchSources
    {
        [Serializable] sealed class WikiResponse { public WikiQuery query; }
        [Serializable] sealed class WikiQuery { public WikiPage[] pages; }
        [Serializable] sealed class WikiPage { public int index; public string title, extract; public WikiLangLink[] langlinks; }
        [Serializable] sealed class WikiLangLink { public string lang, title; }
        [Serializable] sealed class DdgAnswer { public string Heading, AbstractText, AbstractSource, AbstractURL, Answer; }

        static string WikiHost(Lang lang) => lang == Lang.Ko ? "ko.wikipedia.org" : "en.wikipedia.org";
        static string Market(Lang lang) => lang == Lang.Ko ? "setlang=ko&cc=KR" : "setlang=en-US&cc=US";

        /// <summary>
        /// Full lead sections (several paragraphs, plain text) of the best matching articles in one request, with the
        /// English title of each Korean article.
        /// </summary>
        public static async Awaitable<List<Passage>> WikipediaAsync(IWebClient web, string query, Lang lang, int articles, CancellationToken cancel)
        {
            var url = $"https://{WikiHost(lang)}/w/api.php?action=query&generator=search&gsrsearch={Uri.EscapeDataString(query)}&gsrlimit={articles}" +
                      $"&prop=extracts|langlinks&exintro=1&explaintext=1&exlimit={articles}&lllang=en&redirects=1&format=json&formatversion=2";
            return ParseWikiPages(await web.GetAsync(url, cancel), lang);
        }

        /// <summary>The lead section of one article by exact title.</summary>
        public static async Awaitable<List<Passage>> WikipediaIntroAsync(IWebClient web, string title, Lang lang, CancellationToken cancel)
        {
            var url = $"https://{WikiHost(lang)}/w/api.php?action=query&titles={Uri.EscapeDataString(title)}&prop=extracts&exintro=1&explaintext=1&redirects=1&format=json&formatversion=2";
            return ParseWikiPages(await web.GetAsync(url, cancel), lang);
        }

        public static List<Passage> ParseWikiPages(string json, Lang lang)
        {
            var pages = JsonUtility.FromJson<WikiResponse>(json)?.query?.pages;
            if (pages == null) return new List<Passage>();
            return pages.Where(p => !string.IsNullOrWhiteSpace(p.extract) && !p.extract.Contains("may refer to") && !p.extract.Contains("다음을 가리킨다"))
                .OrderBy(p => p.index)
                .Select(p => new Passage
                {
                    Kind = PassageKind.Wiki,
                    Source = lang == Lang.Ko ? "위키백과" : "Wikipedia",
                    Title = p.title,
                    Text = JoinLines(p.extract.Trim()),
                    Url = $"https://{WikiHost(lang)}/wiki/{Uri.EscapeDataString(p.title.Replace(' ', '_'))}",
                    EnglishTitle = p.langlinks?.FirstOrDefault(l => l.lang == "en")?.title,
                })
                .ToList();
        }

        /// <summary>A run of whitespace with a line break in it becomes one space; other spacing stays as written.</summary>
        static string JoinLines(string text)
        {
            var sb = new StringBuilder(text.Length);
            for (var i = 0; i < text.Length; i++)
            {
                if (!char.IsWhiteSpace(text[i]))
                {
                    sb.Append(text[i]);
                    continue;
                }
                var end = i;
                while (end + 1 < text.Length && char.IsWhiteSpace(text[end + 1])) end++;
                var run = text[i..(end + 1)];
                sb.Append(run.Contains('\n') ? " " : run);
                i = end;
            }
            return sb.ToString();
        }

        public static async Awaitable<List<Passage>> DuckDuckGoAnswerAsync(IWebClient web, string query, CancellationToken cancel) =>
            ParseDuckDuckGo(await web.GetAsync($"https://api.duckduckgo.com/?q={Uri.EscapeDataString(query)}&format=json&no_html=1&skip_disambig=1", cancel));

        public static List<Passage> ParseDuckDuckGo(string json)
        {
            var list = new List<Passage>();
            var a = JsonUtility.FromJson<DdgAnswer>(json);
            if (a == null) return list;
            if (!string.IsNullOrWhiteSpace(a.Answer))
                list.Add(new Passage { Kind = PassageKind.Abstract, Source = "DuckDuckGo", Title = a.Heading ?? string.Empty, Text = InfoText.StripHtml(a.Answer) });
            if (!string.IsNullOrWhiteSpace(a.AbstractText))
                list.Add(new Passage { Kind = PassageKind.Abstract, Source = string.IsNullOrEmpty(a.AbstractSource) ? "DuckDuckGo" : a.AbstractSource, Title = a.Heading ?? string.Empty, Text = a.AbstractText.Trim(), Url = a.AbstractURL });
            return list;
        }

        public static async Awaitable<List<Passage>> BingWebAsync(IWebClient web, string query, Lang lang, int count, CancellationToken cancel) =>
            ParseRss(await web.GetAsync($"https://www.bing.com/search?q={Uri.EscapeDataString(query)}&format=rss&{Market(lang)}", cancel), PassageKind.Web).Take(count).ToList();

        public static async Awaitable<List<Passage>> GoogleNewsAsync(IWebClient web, string query, Lang lang, int count, CancellationToken cancel) =>
            ParseRss(await web.GetAsync($"https://news.google.com/rss/search?q={Uri.EscapeDataString(query)}&" +
                                        (lang == Lang.Ko ? "hl=ko&gl=KR&ceid=KR:ko" : "hl=en-US&gl=US&ceid=US:en"), cancel), PassageKind.News).Take(count).ToList();

        public static async Awaitable<List<Passage>> BingNewsAsync(IWebClient web, string query, Lang lang, int count, CancellationToken cancel) =>
            ParseRss(await web.GetAsync($"https://www.bing.com/news/search?q={Uri.EscapeDataString(query)}&format=RSS&{Market(lang)}", cancel), PassageKind.News).Take(count).ToList();

        public static List<Passage> ParseRss(string xml, PassageKind kind)
        {
            var list = new List<Passage>();
            XDocument doc;
            try
            {
                doc = XDocument.Parse(xml ?? string.Empty);
            }
            catch (XmlException)
            {
                // An error page or a cut-off response.
                return list;
            }
            foreach (var item in doc.Descendants("item"))
            {
                var link = Field(item, "link");
                // Bing wraps the article in a click-tracking link: …/apiclick.aspx?…&url=<article>&….
                if (Uri.TryCreate(link, UriKind.Absolute, out var tracked) && Query(tracked, "url") is { } real) link = real;
                var title = InfoText.StripHtml(Field(item, "title"));
                if (title.Length == 0) continue;
                var date = DateTime.TryParse(Field(item, "pubDate"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : null;
                var site = Uri.TryCreate(link, UriKind.Absolute, out var uri) ? uri.Host.Replace("www.", string.Empty) : "Bing";
                list.Add(new Passage
                {
                    Kind = kind,
                    Source = kind == PassageKind.News ? NewsSource(item, site) : site,
                    Title = kind == PassageKind.News ? InfoText.StripOutlet(title) : title,
                    Text = InfoText.StripHtml(Field(item, "description")),
                    Url = link,
                    Date = date,
                });
            }
            return list;
        }

        /// <summary>Google News names the outlet in &lt;source&gt;, Bing News in &lt;News:Source&gt; (its namespace changes with every query).</summary>
        static string NewsSource(XElement item, string site)
        {
            var google = item.Element("source");
            if (google != null) return InfoText.StripHtml(google.Value);
            var bing = item.Elements().FirstOrDefault(e => e.Name.LocalName == "Source" && e.GetPrefixOfNamespace(e.Name.Namespace) == "News");
            return bing != null ? InfoText.StripHtml(bing.Value).Replace(" on MSN", string.Empty) : site;
        }

        static string Field(XElement item, string tag) => item.Element(tag)?.Value ?? string.Empty;

        static string Query(Uri uri, string key)
        {
            foreach (var pair in uri.Query.TrimStart('?').Split('&'))
                if (pair.StartsWith(key + "=", StringComparison.Ordinal))
                    return Uri.UnescapeDataString(pair[(key.Length + 1)..]);
            return null;
        }
    }
}
