using System;
using System.Diagnostics;
using System.IO;
using SentisModels;
using UnityEngine;

namespace VoiceAgent
{
    /// <summary>
    /// jevlike option scorer (github.com/vinnylarouge/jevlike, MIT) over frozen Gemma3 token states from the layer the
    /// head was trained on:
    /// each command's option vector queries the context tokens, a shared dot product gives one logit per
    /// command, and a softmax runs across commands: one encoder pass, no decoding. With several heads the
    /// probabilities are averaged; <see cref="PolarityGuard"/> then settles on/off pairs by their direction words.
    /// Weights come from VoiceAgent/Train Command Head (StreamingAssets/Intent/jevlike_head.json).
    /// </summary>
    public sealed class JevlikeRanker
    {
        [Serializable]
        sealed class HeadWeights
        {
            public float[] context_norm_weight, context_norm_bias, option_norm_weight, option_norm_bias;
            public float[] query, key, value;
        }

        [Serializable]
        sealed class Weights
        {
            public int width, rank, max_tokens;
            /// <summary>Gemma3 layer of the token states (<see cref="Gemma3Model.Encode"/>); 0 is the final norm.</summary>
            public int layer;
            public float eps;
            public string[] intents;
            public float[] options;
            public HeadWeights[] heads;
            public float val_accuracy;
        }

        /// <summary>One head with its option queries folded in: q/(Wk x) = (Wkᵀ q)/x and q/(Wv x) = (Wvᵀ q)/x.</summary>
        sealed class Head
        {
            public HeadWeights W;
            public float[] KeyFold, ValueFold;
        }

        readonly Gemma3Model m_Gemma;
        readonly Weights m_W;
        readonly Head[] m_Heads;
        readonly PolarityGuard m_Guard;
        readonly float m_InvSqrtRank;

        public double LastEncoderMs { get; private set; }
        public double LastHeadMs { get; private set; }
        public float ValidationAccuracy => m_W.val_accuracy;
        public int Layer => m_W.layer;

        public JevlikeRanker(Gemma3Model gemma, string weightsPath, CommandCatalog catalog)
        {
            m_Gemma = gemma;
            m_W = JsonUtility.FromJson<Weights>(File.ReadAllText(weightsPath));
            if (m_W.width != Gemma3Model.Width) throw new InvalidDataException($"head width {m_W.width} != {Gemma3Model.Width}");
            if (m_W.layer < Gemma3Model.FinalNorm || m_W.layer > Gemma3Model.NumLayers) throw new InvalidDataException($"head layer {m_W.layer} is not a Gemma3 layer");
            if (m_W.heads == null || m_W.heads.Length == 0) throw new InvalidDataException("jevlike_head.json has no heads; retrain it with VoiceAgent/Train Command Head");
            var upToDate = m_W.intents != null && m_W.intents.Length == catalog.Count;
            for (var i = 0; upToDate && i < catalog.Count; i++) upToDate = m_W.intents[i] == catalog.Commands[i].id;
            if (!upToDate) throw new InvalidDataException("jevlike_head.json is out of date with Commands.json; retrain it with VoiceAgent/Train Command Head");
            m_InvSqrtRank = 1f / MathF.Sqrt(m_W.rank);
            m_Heads = Array.ConvertAll(m_W.heads, Fold);
            m_Guard = PolarityGuard.Load(m_W.intents);
        }

        Head Fold(HeadWeights w)
        {
            int n = m_W.intents.Length, width = m_W.width, rank = m_W.rank;
            var head = new Head { W = w, KeyFold = new float[n * width], ValueFold = new float[n * width] };
            var normed = new float[width];
            var q = new float[rank];
            for (var o = 0; o < n; o++)
            {
                LayerNorm(m_W.options.AsSpan(o * width, width), w.option_norm_weight, w.option_norm_bias, normed);
                for (var r = 0; r < rank; r++) q[r] = Dot(w.query.AsSpan(r * width, width), normed);
                for (var r = 0; r < rank; r++)
                    for (var i = 0; i < width; i++)
                    {
                        head.KeyFold[o * width + i] += w.key[r * width + i] * q[r];
                        head.ValueFold[o * width + i] += w.value[r * width + i] * q[r];
                    }
            }
            return head;
        }

        /// <summary>
        /// Lowercase without punctuation, as every training sentence is (TrainingData), so STT's
        /// "Switch the music off." and a typed "switch the music off" score the same.
        /// </summary>
        public static string Normalize(string text) => TextScan.CollapseSpaces(TextScan.ReplaceRuns(text.ToLowerInvariant(), TextScan.Punctuation, " "));

        public float[] Score(string text)
        {
            var clock = Stopwatch.StartNew();
            var normalized = Normalize(text ?? string.Empty);
            // A partial transcript can be only punctuation ("." or "?"): nothing is left to encode, and the tokenizer
            // throws on an empty string. Every command is then equally likely.
            if (normalized.Length == 0)
            {
                LastEncoderMs = LastHeadMs = 0;
                return ScoreStates(ReadOnlySpan<float>.Empty, 0);
            }
            var ids = m_Gemma.Tokenize(normalized, m_W.max_tokens);
            var states = m_Gemma.Encode(ids, m_W.layer);
            LastEncoderMs = clock.Elapsed.TotalMilliseconds;
            clock.Restart();
            // Row 0 is <bos>, an attention sink with huge activations; training drops it too.
            var probs = m_Guard.Apply(normalized, ScoreStates(states.AsSpan(m_W.width), ids.Length - 1));
            LastHeadMs = clock.Elapsed.TotalMilliseconds;
            return probs;
        }

        /// <summary>Heads only: token states [length × width] → command probabilities, averaged over the heads.</summary>
        public float[] ScoreStates(ReadOnlySpan<float> states, int length)
        {
            var n = m_W.intents.Length;
            var mean = new float[n];
            if (length <= 0)
            {
                Array.Fill(mean, 1f / n);
                return mean;
            }
            foreach (var head in m_Heads)
            {
                var p = ScoreHead(head, states, length);
                for (var i = 0; i < n; i++) mean[i] += p[i] / m_Heads.Length;
            }
            return mean;
        }

        float[] ScoreHead(Head head, ReadOnlySpan<float> states, int length)
        {
            int width = m_W.width, n = m_W.intents.Length;
            var normed = new float[length * width];
            var row = new float[width];
            for (var t = 0; t < length; t++)
            {
                LayerNorm(states.Slice(t * width, width), head.W.context_norm_weight, head.W.context_norm_bias, row);
                row.CopyTo(normed, t * width);
            }

            var logits = new float[n];
            var scores = new float[length];
            var values = new float[length];
            for (var o = 0; o < n; o++)
            {
                var keyFold = head.KeyFold.AsSpan(o * width, width);
                var valueFold = head.ValueFold.AsSpan(o * width, width);
                var max = float.NegativeInfinity;
                for (var t = 0; t < length; t++)
                {
                    var x = normed.AsSpan(t * width, width);
                    scores[t] = Dot(keyFold, x) * m_InvSqrtRank;
                    values[t] = Dot(valueFold, x);
                    max = MathF.Max(max, scores[t]);
                }
                float sum = 0f, weighted = 0f;
                for (var t = 0; t < length; t++)
                {
                    var w = MathF.Exp(scores[t] - max);
                    sum += w;
                    weighted += w * values[t];
                }
                logits[o] = weighted / sum * m_InvSqrtRank;
            }
            return Softmax(logits);
        }

        void LayerNorm(ReadOnlySpan<float> x, float[] gamma, float[] beta, float[] output)
        {
            var mean = 0f;
            foreach (var v in x) mean += v;
            mean /= x.Length;
            var variance = 0f;
            foreach (var v in x) variance += (v - mean) * (v - mean);
            variance /= x.Length;
            var inv = 1f / MathF.Sqrt(variance + m_W.eps);
            for (var i = 0; i < x.Length; i++) output[i] = (x[i] - mean) * inv * gamma[i] + beta[i];
        }

        static float Dot(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
        {
            var s = 0f;
            for (var i = 0; i < a.Length; i++) s += a[i] * b[i];
            return s;
        }

        static float[] Softmax(float[] logits)
        {
            var max = float.NegativeInfinity;
            foreach (var l in logits) max = MathF.Max(max, l);
            var sum = 0f;
            var p = new float[logits.Length];
            for (var i = 0; i < p.Length; i++) sum += p[i] = MathF.Exp(logits[i] - max);
            for (var i = 0; i < p.Length; i++) p[i] /= sum;
            return p;
        }
    }
}
