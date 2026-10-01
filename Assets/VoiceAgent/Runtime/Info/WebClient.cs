using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using UnityEngine;
using UnityEngine.Networking;

namespace VoiceAgent.Info
{
    public sealed class WebException : Exception
    {
        public WebException(string message) : base(message) { }
    }

    /// <summary>HTTP seam so the info pipeline can be tested with fixtures.</summary>
    public interface IWebClient
    {
        Awaitable<string> GetAsync(string url, CancellationToken cancel = default);

        /// <summary>Whether this many Wikipedia/Wikidata API calls fit the anonymous budget without a long wait.</summary>
        bool CanAffordWikimedia(int calls) => true;
    }

    /// <summary>UnityWebRequest GET with a timeout and a short in-memory cache (main thread only).</summary>
    public sealed class UnityWebClient : IWebClient
    {
        // Bing only returns RSS to non-browser agents. Wikimedia's anonymous API allows roughly 10–15 requests per IP
        // before a 429 that lasts 20–60 s, so API calls are budgeted and a 429 pauses Wikimedia instead of waiting on it.
        public string UserAgent = "Mozilla/5.0 (compatible; VoiceAgent/0.1; Unity on-device demo)";
        public string WikimediaUserAgent = "VoiceAgent/0.2 (Unity on-device voice assistant demo)";

        public int WikimediaRequestsPerWindow = 8;
        /// <summary>Requests during a block extend it, so nothing is sent to Wikimedia for this long after a 429.</summary>
        public float WikimediaPauseSeconds = 30f;
        public float WikimediaWindowSeconds = 30f;
        /// <summary>How long a Wikimedia API call may wait for the budget before it gives up (the caller falls back).</summary>
        public float MaxBudgetWaitSeconds = 4f;
        public int TimeoutSeconds = 5;
        /// <summary>The Wikidata Query Service answers in 0.6–13 s depending on its load.</summary>
        public int QueryServiceTimeoutSeconds = 10;
        public TimeSpan CacheTime = TimeSpan.FromMinutes(10);

        const string k_QueryServiceHost = "query.wikidata.org";
        // These share one per-IP limit; the Query Service has its own.
        static readonly string[] s_ActionApiHosts = { "en.wikipedia.org", "ko.wikipedia.org", "www.wikidata.org" };

        readonly Dictionary<string, (DateTime At, string Body)> m_Cache = new();
        // Shared by every client: Wikimedia counts requests per IP.
        static readonly Dictionary<string, float> s_PausedUntil = new();
        static readonly Queue<float> s_WikimediaCalls = new();

        /// <summary>True when the calls fit now, or once enough budget slots free up within <see cref="MaxBudgetWaitSeconds"/>.</summary>
        public bool CanAffordWikimedia(int calls)
        {
            var now = Time.realtimeSinceStartup;
            foreach (var host in s_ActionApiHosts)
                if (s_PausedUntil.TryGetValue(host, out var until) && now < until) return false;
            DropExpiredCalls(now);
            var missing = calls - (WikimediaRequestsPerWindow - s_WikimediaCalls.Count);
            if (missing <= 0) return true;
            if (missing > s_WikimediaCalls.Count) return false;
            var freedAt = s_WikimediaCalls.ElementAt(missing - 1) + WikimediaWindowSeconds;
            return freedAt - now <= MaxBudgetWaitSeconds;
        }

        public async Awaitable<string> GetAsync(string url, CancellationToken cancel = default)
        {
            if (m_Cache.TryGetValue(url, out var hit) && DateTime.UtcNow - hit.At < CacheTime) return hit.Body;
            var host = Host(url);
            ThrowIfPaused(host);
            var budgeted = IsWikimedia(host) && url.Contains("/w/api.php");
            if (budgeted) await WaitForBudget(host, cancel);
            try
            {
                return await SendAsync(url, true, cancel);
            }
            catch (RetryableException e) when (e.Status == 429 && IsWikimedia(host))
            {
                // Waiting a few seconds wouldn't help (the window is ~30 s): skip Wikimedia until Retry-After has passed.
                var pause = Mathf.Max(e.RetryAfterSeconds, WikimediaPauseSeconds);
                var until = Time.realtimeSinceStartup + pause;
                if (host == k_QueryServiceHost) s_PausedUntil[host] = until;
                else
                    foreach (var shared in s_ActionApiHosts) s_PausedUntil[shared] = until;
                throw new WebException($"GET {host} → 429, Wikimedia paused for {pause:F0} s");
            }
            catch (RetryableException e)
            {
                // 429 / 5xx: one retry after the server's Retry-After (capped).
                var end = Time.realtimeSinceStartup + Mathf.Clamp(e.RetryAfterSeconds, 0.5f, 3f);
                while (Time.realtimeSinceStartup < end)
                {
                    cancel.ThrowIfCancellationRequested();
                    await Awaitable.NextFrameAsync();
                }
                if (budgeted) await WaitForBudget(host, cancel);
                return await SendAsync(url, false, cancel);
            }
        }

        sealed class RetryableException : Exception
        {
            public readonly long Status;
            public readonly float RetryAfterSeconds;

            public RetryableException(long status, float seconds)
            {
                Status = status;
                RetryAfterSeconds = seconds;
            }
        }

        async Awaitable<string> SendAsync(string url, bool canRetry, CancellationToken cancel)
        {
            var host = Host(url);
            using var request = UnityWebRequest.Get(url);
            request.timeout = host == k_QueryServiceHost ? QueryServiceTimeoutSeconds : TimeoutSeconds;
            request.SetRequestHeader("User-Agent", IsWikimedia(host) ? WikimediaUserAgent : UserAgent);
            request.SetRequestHeader("Accept-Language", "ko-KR,ko;q=0.9,en;q=0.8");
            var operation = request.SendWebRequest();
            while (!operation.isDone)
            {
                if (cancel.IsCancellationRequested)
                {
                    request.Abort();
                    throw new OperationCanceledException(cancel);
                }
                await Awaitable.NextFrameAsync();
            }
            if (canRetry && (request.responseCode == 429 || request.responseCode >= 500))
            {
                var retryAfter = float.TryParse(request.GetResponseHeader("Retry-After"), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) ? seconds : 1f;
                throw new RetryableException(request.responseCode, retryAfter);
            }
            if (request.result != UnityWebRequest.Result.Success)
                throw new WebException($"GET {host} → {request.responseCode} {request.error}");
            if (request.responseCode == 202) throw new WebException($"GET {host} → 202 (rate limited)");
            var body = request.downloadHandler.text;
            m_Cache[url] = (DateTime.UtcNow, body);
            return body;
        }

        /// <summary>Keeps Wikipedia/Wikidata API calls under the anonymous limit instead of running into its 429 pause.</summary>
        async Awaitable WaitForBudget(string host, CancellationToken cancel)
        {
            while (true)
            {
                ThrowIfPaused(host);
                var now = Time.realtimeSinceStartup;
                DropExpiredCalls(now);
                if (s_WikimediaCalls.Count < WikimediaRequestsPerWindow)
                {
                    s_WikimediaCalls.Enqueue(now);
                    return;
                }
                var wait = WikimediaWindowSeconds - (now - s_WikimediaCalls.Peek());
                if (wait > MaxBudgetWaitSeconds) throw new WebException($"GET {host} → request budget used up for {wait:F0} s");
                cancel.ThrowIfCancellationRequested();
                await Awaitable.NextFrameAsync();
            }
        }

        void DropExpiredCalls(float now)
        {
            while (s_WikimediaCalls.Count > 0 && now - s_WikimediaCalls.Peek() > WikimediaWindowSeconds) s_WikimediaCalls.Dequeue();
        }

        static void ThrowIfPaused(string host)
        {
            if (s_PausedUntil.TryGetValue(host, out var until) && Time.realtimeSinceStartup < until)
                throw new WebException($"GET {host} → paused {until - Time.realtimeSinceStartup:F0} s after a 429");
        }

        static bool IsWikimedia(string host) =>
            host.EndsWith("wikipedia.org", StringComparison.Ordinal) || host.EndsWith("wikidata.org", StringComparison.Ordinal);

        static string Host(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : url;
    }
}
