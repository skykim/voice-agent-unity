using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Networking;

namespace VoiceAgent.Editor
{
    /// <summary>
    /// The training sentences: the functiongemma-270m-finetune rows in the chosen languages (refusals become "chat"
    /// unless an extra-phrase command covers them) plus extra_phrases.json, with a fifth of each command's extra
    /// phrases (at least one, never all) held out. Training sentences also appear with a filler word and without
    /// apostrophes; everything is normalized like <see cref="DecisionAIRanker.Normalize"/>. A validation sentence that is
    /// also a training sentence (the dataset repeats some rows, and some extra phrases are dataset rows) is dropped.
    /// </summary>
    public sealed class TrainingData
    {
        public const string DatasetRepo = "Sky-Kim/functiongemma-270m-finetune";

        [Serializable] sealed class Row { public string user, fn; }

        public readonly List<(string Text, int Label)> Train = new(), Val = new();
        /// <summary>
        /// <see cref="Train"/> plus the held-out sentences, augmented the same way and with the extra phrases repeated:
        /// the sentences of a final head trained on everything.
        /// </summary>
        public readonly List<(string Text, int Label)> All = new();
        /// <summary>The commands that have extra phrases, in file order (reported separately).</summary>
        public readonly List<string> ExtraCommands = new();

        static readonly string[] s_KoFillers = { "음 ", "노바야, ", "저기 ", "그 " };
        static readonly string[] s_EnFillers = { "um ", "hey nova, ", "so ", "okay " };

        public static TrainingData Load(CommandCatalog catalog, string extraPath, string datasetFolder, ISet<Lang> languages, int repeatExtra, System.Random random)
        {
            var data = new TrainingData();
            var extra = JObject.Parse(File.ReadAllText(extraPath));
            var reclaimed = new Regex((string)extra["reclaim_from_chat"], RegexOptions.IgnoreCase);
            var phrases = (JObject)extra["phrases"];
            var unknown = phrases.Properties().Select(p => p.Name).Where(n => catalog.IndexOf(n) < 0).ToList();
            if (unknown.Count > 0) throw new InvalidDataException($"extra_phrases.json has commands that aren't in Commands.json: {string.Join(", ", unknown)}");

            var extraTrain = new List<(string, int)>();
            var extraVal = new List<(string, int)>();
            foreach (var property in phrases.Properties())
            {
                var label = catalog.IndexOf(property.Name);
                var shuffled = property.Value.Select(v => (string)v).Where(u => languages.Contains(LangDetect.Of(u))).ToList();
                Shuffle(shuffled, random);
                var cut = shuffled.Count < 2 ? 0 : Math.Clamp(shuffled.Count / 5, 1, shuffled.Count - 1);
                extraVal.AddRange(shuffled.Take(cut).Select(u => (u, label)));
                extraTrain.AddRange(shuffled.Skip(cut).Select(u => (u, label)));
                data.ExtraCommands.Add(property.Name);
            }

            var trainRows = Rows(Path.Combine(datasetFolder, "train.jsonl"), catalog, languages, reclaimed);
            for (var i = 0; i < repeatExtra; i++) trainRows.AddRange(extraTrain);
            var datasetVal = Rows(Path.Combine(datasetFolder, "val.jsonl"), catalog, languages, reclaimed);
            var valRows = new List<(string, int)>(datasetVal);
            valRows.AddRange(extraVal);

            var missing = catalog.Commands.Where((c, i) => trainRows.All(r => r.Item2 != i)).Select(c => c.id).ToList();
            if (missing.Count > 0) throw new InvalidDataException($"no training sentences for {string.Join(", ", missing)}: add phrases for them to extra_phrases.json");

            foreach (var (text, label) in trainRows)
                foreach (var variant in Augment(text, random))
                    data.Train.Add((variant, label));
            var trained = new HashSet<string>(data.Train.Select(r => r.Text));
            var validated = new HashSet<string>();
            foreach (var (text, label) in valRows)
            {
                var normalized = DecisionAIRanker.Normalize(text);
                if (!trained.Contains(normalized) && validated.Add(normalized)) data.Val.Add((normalized, label));
            }

            // Augmented after Train, so the split and Train's augmentation don't depend on whether All is used.
            var heldOut = new List<(string, int)>(datasetVal);
            for (var i = 0; i < repeatExtra; i++) heldOut.AddRange(extraVal);
            data.All.AddRange(data.Train);
            foreach (var (text, label) in heldOut)
                foreach (var variant in Augment(text, random))
                    data.All.Add((variant, label));
            return data;
        }

        static List<(string, int)> Rows(string path, CommandCatalog catalog, ISet<Lang> languages, Regex reclaimed)
        {
            var rows = new List<(string, int)>();
            foreach (var line in File.ReadLines(path))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var r = JsonUtility.FromJson<Row>(line);
                if (!languages.Contains(LangDetect.Of(r.user))) continue;
                string fn;
                if (r.fn == "__none__")
                {
                    if (reclaimed.IsMatch(r.user)) continue;
                    fn = CommandCatalog.Chat;
                }
                else fn = r.fn;
                var label = catalog.IndexOf(fn);
                if (label >= 0) rows.Add((r.user, label));
            }
            return rows;
        }

        static IEnumerable<string> Augment(string text, System.Random random)
        {
            var fillers = LangDetect.Of(text) == Lang.Ko ? s_KoFillers : s_EnFillers;
            var variants = new[] { text, fillers[random.Next(fillers.Length)] + text, text.Replace("'", string.Empty) };
            return variants.Select(DecisionAIRanker.Normalize).Distinct();
        }

        static void Shuffle<T>(IList<T> list, System.Random random)
        {
            for (var i = list.Count - 1; i > 0; i--)
            {
                var j = random.Next(i + 1);
                (list[i], list[j]) = (list[j], list[i]);
            }
        }

        /// <summary>
        /// train.jsonl/val.jsonl: <paramref name="folder"/> when given, else the local checkout next to the project, else a
        /// one-time download into Library/VoiceAgent/functiongemma.
        /// </summary>
        public static string DatasetFolder(string projectRoot, string folder = null)
        {
            if (folder != null) return folder;
            var local = Path.GetFullPath(Path.Combine(projectRoot, "..", "..", "functiongemma-270m-finetune", "data"));
            if (File.Exists(Path.Combine(local, "train.jsonl"))) return local;
            var cache = Path.Combine(projectRoot, "Library", "VoiceAgent", "functiongemma");
            foreach (var name in new[] { "train.jsonl", "val.jsonl" })
            {
                var path = Path.Combine(cache, name);
                if (!File.Exists(path)) Download($"https://huggingface.co/{DatasetRepo}/resolve/main/data/{name}", path);
            }
            return cache;
        }

        static void Download(string url, string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var partial = path + ".part";
            using (var request = new UnityWebRequest(url, UnityWebRequest.kHttpVerbGET, new DownloadHandlerFile(partial), null) { timeout = 120 })
            {
                request.SendWebRequest();
                while (!request.isDone) Thread.Sleep(20);
                if (request.result != UnityWebRequest.Result.Success)
                {
                    File.Delete(partial);
                    throw new IOException($"GET {url} → {request.responseCode} {request.error}");
                }
            }
            File.Move(partial, path);
        }
    }
}
