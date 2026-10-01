using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using SentisModels;
using VoiceAgent.Editor;

namespace VoiceAgent.Tests
{
    /// <summary>The C# head trainer against finite differences, the runtime ranker and a toy task (no Gemma needed).</summary>
    public class TrainerTests
    {
        static float[] RandomArray(Random random, int count, float scale) =>
            Enumerable.Range(0, count).Select(_ => (float)(random.NextDouble() * 2 - 1) * scale).ToArray();

        static Example RandomExample(Random random, int width, int length, int label) =>
            new() { Text = $"example {label}", States = RandomArray(random, length * width, 1.5f), Length = length, Label = label };

        [Test]
        public void Gradients_MatchFiniteDifferences()
        {
            const int width = 6, rank = 3, count = 4;
            var random = new Random(1);
            var trainer = new HeadTrainer(width, rank, RandomArray(random, count * width, 1f), random) { Dropout = 0f };
            var p = trainer.Parameters;
            // Move the LayerNorm weights off 1 and 0 so their gradients are exercised too.
            for (var i = 0; i < 4 * width; i++) p[i] += (float)(random.NextDouble() - 0.5) * 0.5f;
            var batch = Enumerable.Range(0, 5).Select(i => RandomExample(random, width, 1 + i % 4, i % count)).ToList();

            trainer.ComputeGradients(batch, 0);
            var analytic = (float[])trainer.Gradients.Clone();
            const float h = 1e-2f;
            for (var j = 0; j < p.Length; j++)
            {
                var saved = p[j];
                p[j] = saved + h;
                var up = trainer.ComputeGradients(batch, 0);
                p[j] = saved - h;
                var down = trainer.ComputeGradients(batch, 0);
                p[j] = saved;
                var numeric = (up - down) / (2 * h);
                Assert.AreEqual(numeric, analytic[j], 2e-3 + 2e-2 * Math.Abs(numeric), $"parameter {j}");
            }
        }

        [TestCase(1)]
        [TestCase(2)]
        public void ExportedHead_ScoresLikeTheRuntimeRanker(int headCount)
        {
            var catalog = CommandCatalog.Load();
            var random = new Random(2);
            var batch = Enumerable.Range(0, 16).Select(i => RandomExample(random, Gemma3Model.Width, 3 + i % 5, i % catalog.Count)).ToList();
            var heads = Enumerable.Range(0, headCount).Select(_ =>
            {
                var trainer = new HeadTrainer(Gemma3Model.Width, 8, RandomArray(random, catalog.Count * Gemma3Model.Width, 1f), random);
                for (var step = 0; step < 3; step++) trainer.Step(batch, (ulong)step);
                return trainer;
            }).ToList();

            var path = Path.Combine(Path.GetTempPath(), "jevlike_head_trainer_test.sentis");
            try
            {
                HeadExporter.Write(path, heads, catalog.Commands.Select(c => c.id).ToList(), 12, 48, 0.5f, 0.1f);
                using var ranker = new JevlikeRanker(null, path, catalog);
                Assert.AreEqual(12, ranker.Layer);
                Assert.AreEqual(0.5f, ranker.ValidationAccuracy);
                var predictions = heads.Select(h => h.Predict(batch)).ToList();
                for (var i = 0; i < batch.Count; i++)
                {
                    var expected = Enumerable.Range(0, catalog.Count).Select(o => predictions.Average(p => p[i][o])).ToArray();
                    var actual = ranker.ScoreStates(batch[i].States, batch[i].Length);
                    for (var o = 0; o < catalog.Count; o++) Assert.AreEqual(expected[o], actual[o], 1e-4f, $"example {i}, command {o}");
                }
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Test]
        public void Settings_RoundTripThroughJson()
        {
            var edited = new CommandTrainer.Settings
            {
                Epochs = 12, Layer = 6, Rank = 64, MaxTokens = 32, Heads = 3, RepeatExtra = 2, Seed = 11, BatchSize = 32,
                LearningRate = 5e-3f, WeightDecay = 0f, Dropout = 0.25f, LabelSmoothing = 0.05f, ClipNorm = 2f,
                Languages = "ko", Guard = false, HeadOut = "/tmp/head.sentis", Report = "/tmp/report.json", Dataset = "/tmp/data",
            };
            var loaded = new CommandTrainer.Settings();
            UnityEngine.JsonUtility.FromJsonOverwrite(UnityEngine.JsonUtility.ToJson(edited), loaded);
            Assert.AreEqual(UnityEngine.JsonUtility.ToJson(edited), UnityEngine.JsonUtility.ToJson(loaded));
            Assert.AreEqual(3, loaded.Heads);
            Assert.AreEqual(6, loaded.Layer);
            Assert.AreEqual(0.25f, loaded.Dropout);
            Assert.AreEqual("ko", loaded.Languages);
        }

        [Test]
        public void TrainingData_AllAddsTheHeldOutSentences()
        {
            var catalog = CommandCatalog.Load();
            var folder = Path.Combine(Path.GetTempPath(), "voiceagent_trainingdata_test");
            Directory.CreateDirectory(folder);
            try
            {
                var phrases = string.Join(", ", catalog.Commands.Select((c, i) =>
                    $"\"{c.id}\": [{string.Join(", ", Enumerable.Range(0, 5).Select(j => $"\"do thing {i} way {j}\""))}]"));
                File.WriteAllText(Path.Combine(folder, "extra.json"), $"{{\"reclaim_from_chat\": \"^never$\", \"phrases\": {{{phrases}}}}}");
                File.WriteAllText(Path.Combine(folder, "train.jsonl"), "{\"user\": \"lights on please\", \"fn\": \"turn_on_light\"}\n");
                File.WriteAllText(Path.Combine(folder, "val.jsonl"), "{\"user\": \"lights off please\", \"fn\": \"turn_off_light\"}\n" +
                                                                     "{\"user\": \"lights on please\", \"fn\": \"turn_on_light\"}\n");
                var languages = new HashSet<Lang> { Lang.En };
                TrainingData Load() => TrainingData.Load(catalog, Path.Combine(folder, "extra.json"), folder, languages, 2, new Random(7));

                var data = Load();
                CollectionAssert.AreEqual(data.Train, Load().Train, "same seed, same split and augmentation");
                Assert.IsTrue(data.Val.Any(r => r.Text == "lights off please"));
                Assert.IsFalse(data.Val.Any(r => r.Text == "lights on please"), "a validation row that is also a training row is dropped");
                Assert.AreEqual(catalog.Count, data.Val.Count(r => r.Text.StartsWith("do thing")), "one of each command's five extra phrases is held out");
                CollectionAssert.AreEqual(data.Train, data.All.Take(data.Train.Count), "All starts with Train");
                var all = new HashSet<string>(data.All.Select(r => r.Text));
                foreach (var (text, label) in data.Val)
                {
                    Assert.IsTrue(all.Contains(text), $"held-out '{text}' missing from All");
                    Assert.IsTrue(data.All.Contains((text, label)), $"held-out '{text}' has the wrong label in All");
                    Assert.IsFalse(data.Train.Any(r => r.Text == text), $"held-out '{text}' leaked into Train");
                }
            }
            finally
            {
                Directory.Delete(folder, true);
            }
        }

        [Test]
        public void Head_LearnsAToyTask()
        {
            const int width = 16, rank = 8, count = 3;
            var random = new Random(3);
            var centers = Enumerable.Range(0, count).Select(_ => RandomArray(random, width, 2f)).ToArray();
            Example Sample(int label)
            {
                var length = 2 + random.Next(4);
                var states = new float[length * width];
                for (var t = 0; t < length; t++)
                    for (var i = 0; i < width; i++) states[t * width + i] = centers[label][i] + (float)(random.NextDouble() - 0.5) * 0.5f;
                return new Example { Text = $"class {label}", States = states, Length = length, Label = label };
            }
            var train = Enumerable.Range(0, 90).Select(i => Sample(i % count)).ToList();
            var test = Enumerable.Range(0, 30).Select(i => Sample(i % count)).ToList();

            var trainer = new HeadTrainer(width, rank, RandomArray(random, count * width, 1f), random) { LearningRate = 1e-2f };
            var batch = new List<Example>();
            for (var epoch = 0; epoch < 40; epoch++)
                for (var start = 0; start < train.Count; start += 16)
                {
                    batch.Clear();
                    batch.AddRange(train.Skip(start).Take(16));
                    trainer.Step(batch, (ulong)(epoch * 100 + start));
                }

            var probs = trainer.Predict(test);
            var correct = test.Where((e, i) => Array.IndexOf(probs[i], probs[i].Max()) == e.Label).Count();
            Assert.GreaterOrEqual(correct, 29, $"{correct}/{test.Count} correct");
        }
    }
}
