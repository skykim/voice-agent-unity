using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;
using SentisModels;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace VoiceAgent.Editor
{
    /// <summary>
    /// Trains the command picker: Gemma3 token states for every training sentence (the FP32 encoder the runtime scores
    /// with), then the jevlike head, checked with the on/off guard. Writes StreamingAssets/Intent/jevlike_head.sentis
    /// and a report in Logs/train-command-head.json.
    /// Settings are edited in <see cref="CommandTrainerWindow"/> (VoiceAgent/Train Command Head…).
    /// </summary>
    public static class CommandTrainer
    {
        [Serializable]
        public sealed class Settings
        {
            /// <summary>
            /// <see cref="Epochs"/> is a cap: validation loss levels off around epoch 10, and 20 scored the same as 40 on the
            /// benchmark and the second test set over three seeds, in half the time (README, "Settings").
            /// </summary>
            public int Epochs = 20, Rank = 128, MaxTokens = 48, Heads = 1, RepeatExtra = 4, Seed = 7, BatchSize = 64;
            /// <summary>
            /// Gemma3 layer whose token states the head reads (<see cref="Gemma3Model.Encode"/>): 1 to 18, or 0 for the
            /// final norm. Layer 12 scored best in the layer comparison in README.md.
            /// </summary>
            public int Layer = 12;
            /// <summary>AdamW and regularization, as in <see cref="HeadTrainer"/>.</summary>
            public float LearningRate = 2e-3f, WeightDecay = 1e-2f, Dropout = 0.1f, LabelSmoothing = 0.1f, ClipNorm = 1f;
            /// <summary>Dataset and extra-phrase languages: "en", "ko" or "en,ko".</summary>
            public string Languages = "en,ko";
            /// <summary>Report accuracy with the on/off guard (<see cref="PolarityGuard"/>), as the runtime scores.</summary>
            public bool Guard = true;
            /// <summary>
            /// After the first run picks the epoch count, train the written head again from scratch on the training and
            /// validation sentences together, so no phrase is left out of the shipped head.
            /// </summary>
            public bool FinalOnAllSentences = true;
            public string HeadOut = ModelRoots.IntentHead;
            public string Report = Path.Combine(ProjectRoot, "Logs", "train-command-head.json");
            /// <summary>Folder with train.jsonl/val.jsonl; empty = local checkout or Hugging Face download.</summary>
            public string Dataset;
        }

        public sealed class Result
        {
            /// <summary>Validation loss (label-smoothed, heads averaged, before the on/off guard), accuracy and calibration error.</summary>
            public float ValLoss, ValAccuracy, ValEce;
            public int Layer, TrainSentences, ValSentences, PolarityCorrect, PolarityTotal;
            /// <summary>Epochs kept per head in the first run; sentences of the final run (0 when it is off).</summary>
            public int[] KeptEpochs;
            public int FinalTrainSentences;
            /// <summary>
            /// benchmark.json through the written head and the runtime routing: the top pick is right; the app does the right
            /// thing (runs the command, or chats for small talk); it asks about the right command; it does something wrong.
            /// </summary>
            public int BenchmarkTotal, BenchmarkTop1, BenchmarkRight, BenchmarkAsks, BenchmarkWrong;
            public double TotalSeconds;
            /// <summary>The summary printed to the Console: per-language accuracy, misses, the on/off check, samples.</summary>
            public string Log;
        }

        static readonly string[] s_Samples =
        {
            "turn on the living room lights", "turn off the tv please", "what's the weather like in tokyo", "play some music",
            "what time is it in new york", "who are you", "who was alan turing", "turn the volume up", "make it quieter",
            "tell me a joke", "change the light color to blue", "make the lights green",
            "거실 불 켜줘", "소리 좀 줄여줘", "넌 누구야", "에펠탑은 얼마나 높아", "Switch the music off.",
        };

        static string ProjectRoot => Path.GetDirectoryName(Application.dataPath);
        static string DataFolder => Path.Combine(Application.dataPath, "VoiceAgent", "Editor", "Training");

        /// <param name="progress">Called with a status line and 0..1; returning true cancels.</param>
        public static Result Train(Settings settings, Func<string, float, bool> progress = null)
        {
            var clock = Stopwatch.StartNew();
            var phases = new List<(string Name, double Seconds)>();
            var log = new StringBuilder();
            void Line(string text)
            {
                log.AppendLine(text);
                Debug.Log($"[Trainer] {text}");
            }
            // Per-epoch lines go to the summary only, not one Console entry each.
            void Detail(string text) => log.AppendLine(text);
            T Phase<T>(string name, Func<T> run)
            {
                var phase = Stopwatch.StartNew();
                var result = run();
                phases.Add((name, phase.Elapsed.TotalSeconds));
                Line($"{name}: {phase.Elapsed.TotalSeconds:F1} s");
                return result;
            }
            void Report(string text, float value)
            {
                if (progress?.Invoke(text, value) == true) throw new OperationCanceledException();
            }

            var catalog = CommandCatalog.Load();
            var intents = catalog.Commands.Select(c => c.id).ToArray();
            var languages = new HashSet<Lang>(settings.Languages.Split(',').Select(l => l.Trim() == "ko" ? Lang.Ko : Lang.En));
            var random = new System.Random(settings.Seed);
            var data = Phase("load training sentences", () => TrainingData.Load(catalog, Path.Combine(DataFolder, "extra_phrases.json"),
                TrainingData.DatasetFolder(ProjectRoot, string.IsNullOrWhiteSpace(settings.Dataset) ? null : settings.Dataset), languages, settings.RepeatExtra, random));

            Report("Loading Gemma3…", 0f);
            var gemma = Phase("load gemma-3-270m-it", () => ModelRoots.LoadGemma(null));
            try
            {
                var encoder = new Encoder(gemma, settings.Layer, settings.MaxTokens);
                var options = Phase("encode command options", () => OptionVectors(encoder, catalog));
                var (train, val) = Phase("encode training sentences", () =>
                {
                    var unique = data.Train.Concat(data.Val).Select(r => r.Text).Distinct().Count();
                    var trainUnique = data.Train.Select(r => r.Text).Distinct().Count();
                    return (encoder.Examples(data.Train, (i, text) => Report($"Encoding {i} / {unique}: {text}", 0.6f * i / unique)),
                        encoder.Examples(data.Val, (i, text) => Report($"Encoding {trainUnique + i} / {unique}: {text}", 0.6f * (trainUnique + i) / unique)));
                });
                Line($"{train.Count} training sentences ({data.Train.Select(r => r.Text).Distinct().Count()} unique), {val.Count} validation, {intents.Length} commands, " +
                     (settings.Layer == Gemma3Model.FinalNorm ? "final-norm states" : $"states after layer {settings.Layer}"));

                Line($"loss floor {LossFloor(settings.LabelSmoothing, intents.Length):F3}: with label smoothing {settings.LabelSmoothing} over {intents.Length} commands, no model scores lower");
                Detail("  train loss = mean over the epoch's batches (with dropout); val loss = the same loss on the validation sentences (no dropout)");
                var firstShare = settings.FinalOnAllSentences ? 0.2f : 0.4f;
                var first = Phase($"train {settings.Heads} head(s) ({settings.Epochs} epochs each)", () =>
                    Enumerable.Range(0, settings.Heads).Select(k => TrainHead(k, settings, options, train, val, Line, Detail,
                        (epoch, text) => Report(text, 0.6f + firstShare * (k * settings.Epochs + epoch) / (settings.Heads * settings.Epochs)))).ToList());
                var heads = first.Select(h => h.Head).ToList();
                var keptEpochs = first.Select(h => h.Epochs).ToArray();

                var guard = PolarityGuard.Load(intents);
                float[][] Score(IReadOnlyList<Example> examples, bool guarded = true, IReadOnlyList<HeadTrainer> scoring = null)
                {
                    var perHead = (scoring ?? heads).Select(h => h.Predict(examples)).ToList();
                    return examples.Select((e, i) =>
                    {
                        var mean = new float[intents.Length];
                        foreach (var p in perHead)
                            for (var o = 0; o < mean.Length; o++) mean[o] += p[i][o] / perHead.Count;
                        return guarded && settings.Guard ? guard.Apply(e.Text, mean) : mean;
                    }).ToArray();
                }

                // The guard moves a pair's probability to one side (the other becomes 0), which the smoothed loss can't score.
                var valLoss = (float)Loss(Score(val, guarded: false), val, settings.LabelSmoothing);
                var valProbs = Score(val);
                var (valAccuracy, valEce) = Evaluate(valProbs, val);
                Line($"{heads.Count} head(s) averaged: validation loss {valLoss:F3} (before the on/off guard), accuracy {valAccuracy:F3}, calibration error {valEce:F3}");
                var hits = val.Select((e, i) => ArgMax(valProbs[i]) == e.Label).ToArray();
                var groups = languages.OrderBy(l => l.ToString()).Select(l => (l == Lang.Ko ? "ko" : "en", (Func<Example, bool>)(e => LangDetect.Of(e.Text) == l)))
                    .Concat(data.ExtraCommands.Select(name => (name, (Func<Example, bool>)(e => e.Label == catalog.IndexOf(name)))));
                foreach (var (label, selects) in groups)
                {
                    var selected = val.Select((e, i) => (e, i)).Where(x => selects(x.e)).ToList();
                    if (selected.Count > 0) Line($"  {label}: {selected.Count(x => hits[x.i]) / (float)selected.Count:F3} ({selected.Count} held out)");
                }
                for (var i = 0; i < val.Count; i++)
                    if (!hits[i])
                        Line($"  miss: '{val[i].Text}' → {intents[ArgMax(valProbs[i])]} ({valProbs[i].Max():F2}), expected {intents[val[i].Label]}");

                // The numbers above measure the first run, which held the validation sentences out. The written head can
                // also learn them: the same epoch count, from the same start, on every sentence.
                var written = heads;
                var finalCount = 0;
                if (settings.FinalOnAllSentences)
                {
                    var all = encoder.Examples(data.All);
                    finalCount = all.Count;
                    Line($"final head(s): {all.Count} training + validation sentences, {string.Join(", ", keptEpochs)} epoch(s) as kept above; the validation numbers above come from the first run");
                    var finalEpochs = keptEpochs.Sum();
                    written = Phase("train final head(s) on all sentences", () =>
                        Enumerable.Range(0, settings.Heads).Select(k => TrainHead(k, settings, options, all, null, Line, Detail,
                            (epoch, text) => Report(text, 0.8f + 0.2f * (keptEpochs.Take(k).Sum() + epoch) / finalEpochs), keptEpochs[k]).Head).ToList());
                }

                var polarity = JObject.Parse(File.ReadAllText(Path.Combine(DataFolder, "polarity_eval.json")))["pairs"]
                    .Select(p => (Text: JevlikeRanker.Normalize((string)p[0]), Expected: catalog.IndexOf((string)p[1]))).ToList();
                var polarityExamples = encoder.Examples(polarity.Select(p => (p.Text, p.Expected)).ToList());
                var polarityProbs = Score(polarityExamples, scoring: written);
                var polarityHits = polarity.Where((p, i) => ArgMax(polarityProbs[i]) == p.Expected).Count();
                var polarityMean = polarity.Select((p, i) => polarityProbs[i][p.Expected]).Average();
                Line($"on/off check (written head): {polarityHits}/{polarity.Count} correct, mean probability {polarityMean:F2}, {polarity.Where((p, i) => polarityProbs[i][p.Expected] < 0.5f).Count()} below 0.5");
                for (var i = 0; i < polarity.Count; i++)
                    if (ArgMax(polarityProbs[i]) != polarity[i].Expected || polarityProbs[i][polarity[i].Expected] < 0.5f)
                        Line($"  weak: '{polarity[i].Text}' → {intents[ArgMax(polarityProbs[i])]} ({polarityProbs[i].Max():F2}), expected {intents[polarity[i].Expected]} ({polarityProbs[i][polarity[i].Expected]:F2})");

                var benchmark = Benchmark(Path.Combine(DataFolder, "benchmark.json"), catalog, encoder, examples => Score(examples, scoring: written), Line);

                Phase("write outputs", () =>
                {
                    HeadExporter.Write(settings.HeadOut, written, intents, settings.Layer, settings.MaxTokens, valAccuracy, valEce);
                    return 0;
                });
                var samples = encoder.Examples(s_Samples.Select(s => (JevlikeRanker.Normalize(s), 0)).ToList());
                var sampleProbs = Score(samples, scoring: written);
                for (var i = 0; i < samples.Count; i++)
                    Line($"  '{s_Samples[i]}' → " + string.Join(", ", Enumerable.Range(0, intents.Length).OrderByDescending(o => sampleProbs[i][o]).Take(3)
                        .Select(o => $"{intents[o]} {sampleProbs[i][o]:F2}")));

                var result = new Result
                {
                    ValLoss = valLoss, ValAccuracy = valAccuracy, ValEce = valEce, Layer = settings.Layer, TrainSentences = train.Count, ValSentences = val.Count,
                    PolarityCorrect = polarityHits, PolarityTotal = polarity.Count, TotalSeconds = clock.Elapsed.TotalSeconds,
                    KeptEpochs = keptEpochs, FinalTrainSentences = finalCount,
                    BenchmarkTotal = benchmark.Total, BenchmarkTop1 = benchmark.Top1, BenchmarkRight = benchmark.Right,
                    BenchmarkAsks = benchmark.Asks, BenchmarkWrong = benchmark.Wrong,
                };
                var report = new JObject
                {
                    ["device"] = $"Gemma3 on GPUCompute, head on {Environment.ProcessorCount} CPU threads",
                    ["machine"] = $"{SystemInfo.processorType} / {SystemInfo.operatingSystem}", ["layer"] = settings.Layer, ["heads"] = heads.Count,
                    ["train_sentences"] = train.Count, ["val_sentences"] = val.Count, ["commands"] = intents.Length,
                    ["val_loss"] = valLoss, ["val_accuracy"] = valAccuracy, ["val_ece"] = valEce, ["polarity_correct"] = polarityHits, ["polarity_total"] = polarity.Count,
                    ["polarity_mean_probability"] = Math.Round(polarityMean, 3),
                    ["benchmark"] = new JObject { ["total"] = benchmark.Total, ["top1"] = benchmark.Top1, ["right"] = benchmark.Right, ["asks"] = benchmark.Asks, ["wrong"] = benchmark.Wrong },
                    ["kept_epochs"] = new JArray(keptEpochs), ["final_on_all_sentences"] = settings.FinalOnAllSentences, ["final_train_sentences"] = finalCount,
                    ["seconds"] = new JObject(phases.Select(p => new JProperty(p.Name, Math.Round(p.Seconds, 2)))),
                    ["total_seconds"] = Math.Round(result.TotalSeconds, 2),
                };
                Directory.CreateDirectory(Path.GetDirectoryName(settings.Report)!);
                File.WriteAllText(settings.Report, report.ToString());
                Line($"total {result.TotalSeconds:F1} s / wrote {settings.HeadOut} and {settings.Report}");
                result.Log = log.ToString();
                Debug.Log("[Trainer] summary\n" + log);
                return result;
            }
            finally
            {
                gemma.Dispose();
            }
        }

        /// <summary>
        /// Trains one head. With <paramref name="fixedEpochs"/> 0 it runs <see cref="Settings.Epochs"/> and keeps the epoch with
        /// the lowest validation loss (ties: the higher accuracy); otherwise it runs that many epochs without validation
        /// and keeps the last. Returns the head and the number of epochs it was trained for.
        /// </summary>
        static (HeadTrainer Head, int Epochs) TrainHead(int k, Settings settings, float[] options, List<Example> train, List<Example> val, Action<string> line,
            Action<string> detail, Action<int, string> progress, int fixedEpochs = 0)
        {
            var random = new System.Random(settings.Seed + 1000 * k);
            var head = new HeadTrainer(Gemma3Model.Width, settings.Rank, options, random)
            {
                LearningRate = settings.LearningRate, WeightDecay = settings.WeightDecay, Dropout = settings.Dropout,
                LabelSmoothing = settings.LabelSmoothing, ClipNorm = settings.ClipNorm,
            };
            var order = Enumerable.Range(0, train.Count).ToArray();
            var batch = new List<Example>(settings.BatchSize);
            (float Loss, float Accuracy, float[] Parameters, int Epoch) best = (float.PositiveInfinity, -1f, null, -1);
            // Short enough for the progress bar, which cuts long lines in the middle.
            var headLabel = settings.Heads > 1 ? $"Head {k + 1} / " : string.Empty;
            var epochs = fixedEpochs > 0 ? fixedEpochs : settings.Epochs;
            var trainLoss = 0.0;
            for (var epoch = 0; epoch < epochs; epoch++)
            {
                for (var i = order.Length - 1; i > 0; i--)
                {
                    var j = random.Next(i + 1);
                    (order[i], order[j]) = (order[j], order[i]);
                }
                double loss = 0;
                var steps = 0;
                for (var start = 0; start < order.Length; start += settings.BatchSize)
                {
                    batch.Clear();
                    for (var i = start; i < Math.Min(order.Length, start + settings.BatchSize); i++) batch.Add(train[order[i]]);
                    loss += head.Step(batch, ((ulong)(uint)random.Next() << 32) | (uint)random.Next());
                    steps++;
                }
                trainLoss = loss / steps;
                if (!double.IsFinite(trainLoss))
                    throw new InvalidOperationException($"training diverged at epoch {epoch + 1} (train loss {trainLoss}): lower the learning rate");
                if (fixedEpochs > 0)
                {
                    detail($"  final head {k + 1} epoch {epoch + 1,3}: train loss {trainLoss:F3}");
                    progress(epoch + 1, $"{headLabel}Final / epoch {epoch + 1}/{epochs} / train loss {trainLoss:F3}");
                    continue;
                }
                var valProbs = head.Predict(val);
                var (accuracy, ece) = Evaluate(valProbs, val);
                var valLoss = (float)Loss(valProbs, val, settings.LabelSmoothing);
                if (valLoss < best.Loss || (valLoss == best.Loss && accuracy > best.Accuracy)) best = (valLoss, accuracy, head.Snapshot(), epoch);
                detail($"  head {k + 1} epoch {epoch + 1,3}: train loss {trainLoss:F3} / val loss {valLoss:F3} / val accuracy {accuracy:F3} / calibration error {ece:F3}");
                progress(epoch + 1, $"{headLabel}Epoch {epoch + 1}/{settings.Epochs} / train loss {trainLoss:F3} / val loss {valLoss:F3} / val acc {accuracy:F3}");
            }
            if (fixedEpochs > 0)
            {
                line($"  final head {k + 1}: {epochs} epochs, train loss {trainLoss:F3}");
                return (head, epochs);
            }
            if (best.Parameters == null) throw new InvalidOperationException("no epoch had a finite validation loss: lower the learning rate");
            head.Restore(best.Parameters);
            line($"  head {k + 1}: kept epoch {best.Epoch + 1} (lowest val loss {best.Loss:F3}), val accuracy {best.Accuracy:F3}");
            return (head, best.Epoch + 1);
        }

        /// <summary>Per command, the mean over its option phrases of each phrase's mean token state.</summary>
        static float[] OptionVectors(Encoder encoder, CommandCatalog catalog)
        {
            const int width = Gemma3Model.Width;
            var options = new float[catalog.Count * width];
            var encoded = encoder.Examples(catalog.Commands.SelectMany(c => c.options).Select(o => (o, 0)).ToList());
            var next = 0;
            for (var c = 0; c < catalog.Count; c++)
            {
                var phrases = catalog.Commands[c].options.Select(_ => encoded[next++]).Where(e => e.Length > 0).Select(e => (e.States, e.Length)).ToList();
                foreach (var (states, length) in phrases)
                    for (var t = 0; t < length; t++)
                        for (var i = 0; i < width; i++)
                            options[c * width + i] += states[t * width + i] / length / phrases.Count;
            }
            return options;
        }

        /// <summary>
        /// Scores benchmark.json (sentences never trained on) the way the app handles them: <see cref="TurnRouting"/> with the
        /// runtime's threshold decides whether the top command runs, Nova asks, or Gemma chats.
        /// </summary>
        static (int Total, int Top1, int Right, int Asks, int Wrong) Benchmark(string path, CommandCatalog catalog, Encoder encoder,
            Func<IReadOnlyList<Example>, float[][]> score, Action<string> line)
        {
            if (!File.Exists(path)) return default;
            var rows = JObject.Parse(File.ReadAllText(path))["sentences"]
                .Select(p => (Text: JevlikeRanker.Normalize((string)p[0]), Expected: (string)p[1])).ToList();
            var probs = score(encoder.Examples(rows.Select(r => (r.Text, catalog.IndexOf(r.Expected))).ToList()));
            int top1 = 0, right = 0, asks = 0;
            var wrong = new List<string>();
            for (var i = 0; i < rows.Count; i++)
            {
                var top = Ranking.Top(catalog, probs[i], 3);
                var route = TurnRouting.Decide(top, true, TurnRouting.AutoRunThreshold);
                var expected = rows[i].Expected;
                if (top[0].Command.id == expected) top1++;
                string outcome;
                if (route == TurnRoute.LowConfidence && TurnRouting.Suggestion(top).id == expected) outcome = "asks";
                else if (expected == CommandCatalog.Chat) outcome = route == TurnRoute.Chat ? "right" : route == TurnRoute.LowConfidence ? $"asks about {TurnRouting.Suggestion(top).id}" : $"runs {top[0].Command.id}";
                else if (route == TurnRoute.Execute) outcome = top[0].Command.id == expected ? "right" : $"runs {top[0].Command.id}";
                else if (route == TurnRoute.LowConfidence) outcome = $"asks about {TurnRouting.Suggestion(top).id}";
                else outcome = "chats";
                if (outcome == "right") right++;
                else if (outcome == "asks") asks++;
                else wrong.Add($"  benchmark: '{rows[i].Text}' → {outcome} ({top[0].Command.id} {top[0].Probability:F2}), expected {expected}");
            }
            line($"benchmark (written head, never trained on): {right}/{rows.Count} right, {asks} ask about the right command, {wrong.Count} wrong / top-1 {top1}/{rows.Count}");
            foreach (var w in wrong) line(w);
            return (rows.Count, top1, right, asks, wrong.Count);
        }

        /// <summary>Mean cross-entropy against the label-smoothed targets: the loss the head trains on, without dropout.</summary>
        static double Loss(float[][] probs, IReadOnlyList<Example> examples, float smoothing)
        {
            double sum = 0;
            for (var e = 0; e < examples.Count; e++)
                for (var o = 0; o < probs[e].Length; o++)
                {
                    var target = (o == examples[e].Label ? 1 - smoothing : 0) + smoothing / probs[e].Length;
                    if (target > 0) sum -= target * Math.Log(Math.Max(probs[e][o], 1e-30f));
                }
            return sum / examples.Count;
        }

        /// <summary>The lowest possible <see cref="Loss"/>: the entropy of the smoothed target (0 without smoothing).</summary>
        static double LossFloor(float smoothing, int count)
        {
            double top = 1 - smoothing + smoothing / count, rest = smoothing / count, entropy = 0;
            if (top > 0) entropy -= top * Math.Log(top);
            if (rest > 0) entropy -= (count - 1) * rest * Math.Log(rest);
            return entropy;
        }

        /// <summary>Accuracy, and the expected calibration error over ten confidence bins (0–0.1, …, 0.9–1).</summary>
        static (float Accuracy, float Ece) Evaluate(float[][] probs, IReadOnlyList<Example> examples)
        {
            var correct = 0;
            var bins = new (int Count, int Correct, double Confidence)[10];
            for (var i = 0; i < examples.Count; i++)
            {
                var top = ArgMax(probs[i]);
                var hit = top == examples[i].Label;
                if (hit) correct++;
                var confidence = probs[i][top];
                var bin = Math.Clamp((int)Math.Ceiling(confidence * 10) - 1, 0, 9);
                bins[bin] = (bins[bin].Count + 1, bins[bin].Correct + (hit ? 1 : 0), bins[bin].Confidence + confidence);
            }
            var ece = 0.0;
            foreach (var b in bins)
                if (b.Count > 0)
                    ece += b.Count / (double)examples.Count * Math.Abs(b.Correct / (double)b.Count - b.Confidence / b.Count);
            return (correct / (float)examples.Count, (float)ece);
        }

        static int ArgMax(float[] values)
        {
            var best = 0;
            for (var i = 1; i < values.Length; i++)
                if (values[i] > values[best]) best = i;
            return best;
        }

        /// <summary>Gemma3 token states of one layer per sentence, without the &lt;bos&gt; row, each text encoded once with <see cref="Gemma3Model.EncodeBatch"/>.</summary>
        sealed class Encoder
        {
            readonly Gemma3Model m_Gemma;
            readonly int m_Layer, m_MaxTokens;
            readonly Dictionary<string, (float[] States, int Length)> m_Cache = new();

            public Encoder(Gemma3Model gemma, int layer, int maxTokens)
            {
                m_Gemma = gemma;
                m_Layer = layer;
                m_MaxTokens = maxTokens;
            }

            public List<Example> Examples(IReadOnlyList<(string Text, int Label)> rows, Action<int, string> progress = null)
            {
                const int group = 256;
                var pending = rows.Select(r => r.Text).Distinct().Where(t => !m_Cache.ContainsKey(t)).ToList();
                for (var start = 0; start < pending.Count; start += group)
                {
                    progress?.Invoke(start, pending[start]);
                    var texts = pending.Skip(start).Take(group).ToList();
                    var ids = texts.Select(t => m_Gemma.Tokenize(t, m_MaxTokens)).ToList();
                    var states = m_Gemma.EncodeBatch(ids, m_Layer);
                    for (var i = 0; i < texts.Count; i++) m_Cache[texts[i]] = WithoutBos(states[i], ids[i].Length);
                }
                return rows.Select(r =>
                {
                    var (states, length) = m_Cache[r.Text];
                    return new Example { Text = r.Text, States = states, Length = length, Label = r.Label };
                }).ToList();
            }

            static (float[] States, int Length) WithoutBos(float[] states, int tokens)
            {
                var rows = new float[(tokens - 1) * Gemma3Model.Width];
                Array.Copy(states, Gemma3Model.Width, rows, 0, rows.Length);
                return (rows, tokens - 1);
            }
        }
    }
}
