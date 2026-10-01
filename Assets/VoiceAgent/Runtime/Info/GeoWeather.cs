using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Newtonsoft.Json.Linq;
using UnityEngine;
using static System.FormattableString;

namespace VoiceAgent.Info
{
    public sealed class Place
    {
        public string Name, Country;
        public double Latitude, Longitude;
        public bool FromIp;
    }

    public sealed class Forecast
    {
        public double Temperature;
        public int Code, UtcOffsetSeconds;
        public int[] DailyCode;
        public double[] DailyMax, DailyMin;
        public int[] DailyRain;
    }

    /// <summary>
    /// Places (Open-Meteo geocoding, ipwho.is for "where am I"), weather and local time (Open-Meteo forecast).
    /// No API keys; every call is one small JSON GET.
    /// </summary>
    public sealed class GeoWeather
    {
        [Serializable] sealed class GeoResponse { public GeoResult[] results; }
        [Serializable] sealed class GeoResult { public string name, country; public double latitude, longitude; }
        [Serializable] sealed class IpResponse { public bool success; public string city, country; public double latitude, longitude; }

        static readonly string[] s_WeatherWords =
        {
            "날씨", "기온", "온도", "어때", "어떄", "알려줘", "알려", "줘", "좀", "지금", "오늘", "내일", "모레", "현재", "요즘", "비", "와", "오니", "올까", "몇", "도야",
            "시간", "몇시", "시야", "거기", "에서", "는", "은", "의",
            "weather", "will", "going", "gonna", "to", "be", "a", "an", "of", "on", "for", "and", "there", "this", "week", "weekend", "tonight", "should", "can", "could",
            "please", "outside", "are", "was", "rain", "raining", "snow", "sunny", "cloudy", "does", "do", "i", "need", "umbrella", "우산", "what's", "whats", "what", "is", "the", "like", "in", "today", "tomorrow", "now", "right", "time", "it", "how", "hot", "cold", "forecast", "tell", "me", "at", "current",
        };

        readonly IWebClient m_Web;
        Place m_IpPlace;

        public GeoWeather(IWebClient web) => m_Web = web;

        /// <summary>The place named in the utterance ("weather in Tokyo", "time in Paris"), or null when none is named.</summary>
        public async Awaitable<Place> PlaceInTextAsync(string text, Lang lang, CancellationToken cancel = default)
        {
            foreach (var candidate in PlaceCandidates(text))
            {
                var place = await GeocodeAsync(candidate, lang, cancel);
                if (place != null) return place;
            }
            return null;
        }

        /// <summary>
        /// Place names to geocode, in order: the words after "in"/"at"/"for", the first two words left after removing
        /// weather/time words and particles ("new york weather" → "new york"), then the two longest words. A word that
        /// ends like a particle is tried as written first: 상하이 and 두바이 end in 이, but 도쿄는 is 도쿄.
        /// </summary>
        public static List<string> PlaceCandidates(string text)
        {
            var clean = new string((text ?? string.Empty).Select(c => "?!.,~".IndexOf(c) >= 0 ? ' ' : c).ToArray());
            var words = TextScan.Words(clean)
                .Select(w => (Raw: w, Stripped: InfoText.StripParticle(w)))
                .Where(w => w.Stripped.Length > 0 && !s_WeatherWords.Contains(w.Stripped.ToLowerInvariant()) && !char.IsDigit(w.Stripped[0]))
                .ToList();
            IEnumerable<string> Forms((string Raw, string Stripped) w) => w.Raw == w.Stripped ? new[] { w.Raw } : new[] { w.Raw, w.Stripped };

            var candidates = new List<string>();
            var named = AfterPreposition(clean);
            if (named != null)
            {
                var place = TextScan.Words(named).TakeWhile(w => !s_WeatherWords.Contains(w.ToLowerInvariant())).Take(3).ToList();
                if (place.Count > 0) candidates.Add(string.Join(" ", place));
            }
            if (words.Count >= 2) candidates.Add(words[0].Stripped + " " + words[1].Stripped);
            candidates.AddRange(words.OrderByDescending(w => w.Stripped.Length).Take(2).SelectMany(Forms));
            return candidates.Distinct().Where(c => c.Length >= 2).ToList();
        }

        static readonly string[] s_Prepositions = { "in", "at", "for" };

        /// <summary>What follows the first "in", "at" or "for" and its spaces ("weather in new york" → "new york"), or null.</summary>
        static string AfterPreposition(string text)
        {
            for (var i = 0; i < text.Length; i++)
            {
                if (i > 0 && TextScan.IsWordChar(text[i - 1])) continue;
                foreach (var w in s_Prepositions)
                {
                    var end = i + w.Length;
                    if (end >= text.Length || !char.IsWhiteSpace(text[end]) || string.Compare(text, i, w, 0, w.Length, StringComparison.OrdinalIgnoreCase) != 0) continue;
                    var rest = text[end..].Trim();
                    return rest.Length > 0 ? rest : null;
                }
            }
            return null;
        }

        public async Awaitable<Place> GeocodeAsync(string name, Lang lang, CancellationToken cancel = default)
        {
            var json = await m_Web.GetAsync($"https://geocoding-api.open-meteo.com/v1/search?name={Uri.EscapeDataString(name)}&count=1&language={(lang == Lang.Ko ? "ko" : "en")}&format=json", cancel);
            return ParseGeocode(json);
        }

        public static Place ParseGeocode(string json)
        {
            var r = JsonUtility.FromJson<GeoResponse>(json)?.results;
            if (r == null || r.Length == 0) return null;
            return new Place { Name = r[0].name, Country = r[0].country, Latitude = r[0].latitude, Longitude = r[0].longitude };
        }

        /// <summary>Where this machine is, from its public IP; the name is re-geocoded for a localized spelling (Seoul in Hangul for a Korean question).</summary>
        public async Awaitable<Place> IpPlaceAsync(Lang lang, CancellationToken cancel = default)
        {
            if (m_IpPlace == null)
            {
                var ip = ParseIp(await m_Web.GetAsync("https://ipwho.is/?fields=success,city,country,latitude,longitude", cancel));
                if (ip == null) return null;
                m_IpPlace = ip;
            }
            var place = new Place { Name = m_IpPlace.Name, Country = m_IpPlace.Country, Latitude = m_IpPlace.Latitude, Longitude = m_IpPlace.Longitude, FromIp = true };
            if (lang == Lang.Ko)
            {
                try
                {
                    var local = await GeocodeAsync(m_IpPlace.Name, Lang.Ko, cancel);
                    if (local != null)
                    {
                        place.Name = local.Name;
                        place.Country = local.Country;
                    }
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    // An error page or no answer: keep the IP's own spelling.
                }
            }
            return place;
        }

        public static Place ParseIp(string json)
        {
            var r = JsonUtility.FromJson<IpResponse>(json);
            if (r == null || !r.success || string.IsNullOrEmpty(r.city)) return null;
            return new Place { Name = r.city, Country = r.country, Latitude = r.latitude, Longitude = r.longitude, FromIp = true };
        }

        public async Awaitable<Forecast> ForecastAsync(Place place, CancellationToken cancel = default)
        {
            var url = "https://api.open-meteo.com/v1/forecast?" +
                      Invariant($"latitude={place.Latitude}&longitude={place.Longitude}") +
                      "&current=temperature_2m,weather_code" +
                      "&daily=weather_code,temperature_2m_max,temperature_2m_min,precipitation_probability_max&timezone=auto&forecast_days=3";
            return ParseForecast(await m_Web.GetAsync(url, cancel));
        }

        public static Forecast ParseForecast(string json)
        {
            var r = JObject.Parse(json);
            if (r["current"] is not JObject current) return null;
            var daily = r["daily"] as JObject;
            return new Forecast
            {
                Temperature = Number(current["temperature_2m"]), Code = (int)Number(current["weather_code"]), UtcOffsetSeconds = (int)Number(r["utc_offset_seconds"]),
                DailyCode = Ints(daily?["weather_code"]), DailyMax = Numbers(daily?["temperature_2m_max"]), DailyMin = Numbers(daily?["temperature_2m_min"]), DailyRain = Ints(daily?["precipitation_probability_max"]),
            };
        }

        // Open-Meteo sends null for a value it doesn't have (often the precipitation probability): -1, which no value here can be.
        static double Number(JToken t) => t == null ? 0 : t.Type == JTokenType.Null ? -1 : (double)t;
        static double[] Numbers(JToken t) => t is JArray a ? a.Select(Number).ToArray() : null;
        static int[] Ints(JToken t) => t is JArray a ? a.Select(v => (int)Number(v)).ToArray() : null;

        /// <summary>0 = today, 1 = tomorrow, 2 = the day after.</summary>
        public static int DayOffset(string text)
        {
            var lower = (text ?? string.Empty).ToLowerInvariant();
            if (lower.Contains("모레") || lower.Contains("day after tomorrow")) return 2;
            if (lower.Contains("내일") || lower.Contains("tomorrow")) return 1;
            return 0;
        }

        public static string WeatherReply(Place place, Forecast f, int day, Lang lang)
        {
            var name = place.Name;
            if (day > 0 && f.DailyCode != null && f.DailyCode.Length > day)
            {
                var rain = Rain(f, day);
                return lang == Lang.Ko
                    ? Invariant($"{(day == 1 ? "내일" : "모레")} {name} 날씨는 {Describe(f.DailyCode[day], lang)}, 최고 {f.DailyMax[day]:0}도, 최저 {f.DailyMin[day]:0}도예요.") + (rain >= 0 ? $" 비 올 확률은 {rain}%예요." : string.Empty)
                    : Invariant($"{(day == 1 ? "Tomorrow" : "The day after tomorrow")} in {name}: {Describe(f.DailyCode[day], lang)}, high {f.DailyMax[day]:0}°, low {f.DailyMin[day]:0}°.") + (rain >= 0 ? $" {rain}% chance of rain." : string.Empty);
            }
            var today = f.DailyMax != null && f.DailyMax.Length > 0;
            var rainToday = today ? Rain(f, 0) : -1;
            if (lang == Lang.Ko)
                return Invariant($"{InfoText.Topic(name)} 지금 {Describe(f.Code, lang)}, {f.Temperature:0.#}도예요.") +
                       (today ? Invariant($" 오늘 최고 {f.DailyMax[0]:0}도, 최저 {f.DailyMin[0]:0}도") + (rainToday >= 0 ? $"이고 비 올 확률은 {rainToday}%예요." : "예요.") : string.Empty);
            return Invariant($"It's {Describe(f.Code, lang)} and {f.Temperature:0.#}°C in {name} right now.") +
                   (today ? Invariant($" Today: high {f.DailyMax[0]:0}°, low {f.DailyMin[0]:0}°") + (rainToday >= 0 ? $", {rainToday}% chance of rain." : ".") : string.Empty);
        }

        /// <summary>Precipitation probability in percent, or -1 when Open-Meteo has none for that day.</summary>
        static int Rain(Forecast f, int day) => f.DailyRain != null && f.DailyRain.Length > day && f.DailyRain[day] >= 0 ? f.DailyRain[day] : -1;

        public static string TimeReply(Place place, int utcOffsetSeconds, DateTime utcNow, Lang lang)
        {
            var local = utcNow.AddSeconds(utcOffsetSeconds);
            // UTC+9, UTC+5:30 (India), UTC+5:45 (Nepal), UTC-3:30.
            var span = TimeSpan.FromSeconds(Math.Abs(utcOffsetSeconds));
            var offset = $"UTC{(utcOffsetSeconds < 0 ? "-" : "+")}{(int)span.TotalHours}" + (span.Minutes > 0 ? $":{span.Minutes:00}" : string.Empty);
            return lang == Lang.Ko
                ? $"{InfoText.Topic(place.Name)} 지금 {SmartHome.FormatTime(local, Lang.Ko)}이에요 ({offset})."
                : $"It's {SmartHome.FormatTime(local, Lang.En)} in {place.Name} ({offset}).";
        }

        public static string LocationReply(Place place, Lang lang) => lang == Lang.Ko
            ? $"인터넷 IP 기준으로 {place.Country} {place.Name} 근처에 계신 것 같아요."
            : $"Based on your IP address, you're near {place.Name}, {place.Country}.";

        /// <summary>WMO weather interpretation codes (Open-Meteo).</summary>
        public static string Describe(int code, Lang lang)
        {
            var (ko, en) = code switch
            {
                0 => ("맑음", "clear"),
                1 => ("대체로 맑음", "mostly clear"),
                2 => ("구름 조금", "partly cloudy"),
                3 => ("흐림", "overcast"),
                45 or 48 => ("안개", "foggy"),
                51 or 53 or 55 => ("이슬비", "drizzly"),
                56 or 57 => ("어는 이슬비", "freezing drizzle"),
                61 => ("약한 비", "light rain"),
                63 => ("비", "rainy"),
                65 => ("강한 비", "heavy rain"),
                66 or 67 => ("어는 비", "freezing rain"),
                71 => ("약한 눈", "light snow"),
                73 => ("눈", "snowy"),
                75 => ("많은 눈", "heavy snow"),
                77 => ("싸락눈", "snow grains"),
                80 or 81 or 82 => ("소나기", "rain showers"),
                85 or 86 => ("눈 소나기", "snow showers"),
                95 => ("뇌우", "thunderstorms"),
                96 or 99 => ("우박을 동반한 뇌우", "thunderstorms with hail"),
                _ => ("알 수 없음", "unknown weather"),
            };
            return lang == Lang.Ko ? ko : en;
        }
    }
}
