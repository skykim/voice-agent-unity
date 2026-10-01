using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace VoiceAgent.Editor
{
    /// <summary>One training sentence: frozen token states [Length × width] without the &lt;bos&gt; row, and its command.</summary>
    public sealed class Example
    {
        public string Text;
        public float[] States;
        public int Length;
        public int Label;
    }

    /// <summary>
    /// jevlike's option-attention head (github.com/vinnylarouge/jevlike, MIT) with a hand-written backward pass, trained
    /// with AdamW, dropout on the token states, label smoothing and gradient clipping. The option
    /// queries are folded into the key and value weights as in <see cref="JevlikeRanker"/>, so a sentence costs a few
    /// dot products per token and command; the batch is split into fixed chunks so results don't depend on threading.
    /// </summary>
    public sealed class HeadTrainer
    {
        public const float LayerNormEps = 1e-5f;
        const float Beta1 = 0.9f, Beta2 = 0.999f, AdamEps = 1e-8f;
        const int Chunks = 8;

        public float LearningRate = 2e-3f, WeightDecay = 1e-2f, LabelSmoothing = 0.1f, Dropout = 0.1f, ClipNorm = 1f;

        public readonly int Width, Rank, Count;

        readonly float[] m_Options;
        readonly float[] m_P, m_G, m_M, m_V;
        readonly int m_ContextGamma, m_ContextBeta, m_OptionGamma, m_OptionBeta, m_Query, m_Key, m_Value;
        readonly float m_Scale;
        int m_Step;

        // Recomputed from the parameters before every pass.
        readonly float[] m_OptionHat, m_OptionNorm, m_Queries, m_KeyFold, m_ValueFold;
        readonly Scratch[] m_Scratch = new Scratch[Chunks];

        /// <param name="options">One vector per command [count × width], the mean of its option phrases' token states.</param>
        public HeadTrainer(int width, int rank, float[] options, Random random)
        {
            Width = width;
            Rank = rank;
            Count = options.Length / width;
            m_Options = options;
            m_Scale = 1f / MathF.Sqrt(rank);

            m_ContextGamma = 0;
            m_ContextBeta = width;
            m_OptionGamma = 2 * width;
            m_OptionBeta = 3 * width;
            m_Query = 4 * width;
            m_Key = m_Query + rank * width;
            m_Value = m_Key + rank * width;
            var size = m_Value + rank * width;
            m_P = new float[size];
            m_G = new float[size];
            m_M = new float[size];
            m_V = new float[size];

            // torch defaults: LayerNorm weight 1 and bias 0, Linear weight U(-1/√fan_in, 1/√fan_in).
            Array.Fill(m_P, 1f, m_ContextGamma, width);
            Array.Fill(m_P, 1f, m_OptionGamma, width);
            var bound = 1f / MathF.Sqrt(width);
            for (var i = m_Query; i < size; i++) m_P[i] = (float)(random.NextDouble() * 2 - 1) * bound;

            m_OptionHat = new float[Count * width];
            m_OptionNorm = new float[Count * width];
            m_Queries = new float[Count * rank];
            m_KeyFold = new float[Count * width];
            m_ValueFold = new float[Count * width];
            for (var c = 0; c < Chunks; c++) m_Scratch[c] = new Scratch(Count, width);
        }

        public float[] Parameters => m_P;
        public float[] Gradients => m_G;

        public float[] Snapshot() => (float[])m_P.Clone();
        public void Restore(float[] snapshot) => Array.Copy(snapshot, m_P, m_P.Length);

        /// <summary>Gradients of the mean loss over <paramref name="batch"/> into <see cref="Gradients"/>, then clipping and one AdamW update.</summary>
        public double Step(IReadOnlyList<Example> batch, ulong seed)
        {
            var loss = ComputeGradients(batch, seed);
            ClipGradients();
            AdamW();
            return loss;
        }

        /// <summary>Mean cross-entropy (with label smoothing) over <paramref name="batch"/>; fills <see cref="Gradients"/>.</summary>
        public double ComputeGradients(IReadOnlyList<Example> batch, ulong seed)
        {
            Fold();
            var chunks = Math.Min(Chunks, batch.Count);
            var invBatch = 1f / batch.Count;
            Parallel.For(0, chunks, c =>
            {
                var s = m_Scratch[c];
                s.Clear();
                for (var i = c * batch.Count / chunks; i < (c + 1) * batch.Count / chunks; i++)
                    s.Loss += Pass(batch[i], s, true, seed, (ulong)i, invBatch);
            });

            Array.Clear(m_G, 0, m_G.Length);
            var dKeyFold = m_Scratch[0].KeyFold;
            var dValueFold = m_Scratch[0].ValueFold;
            double loss = m_Scratch[0].Loss;
            for (var c = 1; c < chunks; c++)
            {
                var s = m_Scratch[c];
                loss += s.Loss;
                for (var j = 0; j < dKeyFold.Length; j++)
                {
                    dKeyFold[j] += s.KeyFold[j];
                    dValueFold[j] += s.ValueFold[j];
                }
                for (var i = 0; i < Width; i++)
                {
                    m_Scratch[0].Gamma[i] += s.Gamma[i];
                    m_Scratch[0].Beta[i] += s.Beta[i];
                }
            }
            Array.Copy(m_Scratch[0].Gamma, 0, m_G, m_ContextGamma, Width);
            Array.Copy(m_Scratch[0].Beta, 0, m_G, m_ContextBeta, Width);
            Unfold(dKeyFold, dValueFold);
            return loss / batch.Count;
        }

        /// <summary>Command probabilities per example, without dropout.</summary>
        public float[][] Predict(IReadOnlyList<Example> examples)
        {
            Fold();
            var probs = new float[examples.Count][];
            var chunks = Math.Max(1, Math.Min(Chunks, examples.Count));
            Parallel.For(0, chunks, c =>
            {
                var s = m_Scratch[c];
                for (var i = c * examples.Count / chunks; i < (c + 1) * examples.Count / chunks; i++)
                {
                    Pass(examples[i], s, false, 0, 0, 0f);
                    probs[i] = (float[])s.Probs.Clone();
                }
            });
            return probs;
        }

        /// <summary>LayerNorm the option vectors, project them to queries and fold the queries into the key/value weights.</summary>
        void Fold()
        {
            Parallel.For(0, Count, o =>
            {
                LayerNorm(m_Options, o * Width, m_OptionGamma, m_OptionBeta, m_OptionHat, m_OptionNorm, o * Width);
                for (var r = 0; r < Rank; r++) m_Queries[o * Rank + r] = Dot(m_P, m_Query + r * Width, m_OptionNorm, o * Width, Width);
                Array.Clear(m_KeyFold, o * Width, Width);
                Array.Clear(m_ValueFold, o * Width, Width);
                for (var r = 0; r < Rank; r++)
                {
                    var q = m_Queries[o * Rank + r];
                    Axpy(q, m_P, m_Key + r * Width, m_KeyFold, o * Width, Width);
                    Axpy(q, m_P, m_Value + r * Width, m_ValueFold, o * Width, Width);
                }
            });
        }

        /// <summary>Backward through <see cref="Fold"/>: folded gradients → query/key/value weights and the option LayerNorm.</summary>
        void Unfold(float[] dKeyFold, float[] dValueFold)
        {
            var dQueries = new float[Count * Rank];
            Parallel.For(0, Rank, r =>
            {
                for (var o = 0; o < Count; o++)
                {
                    dQueries[o * Rank + r] = Dot(m_P, m_Key + r * Width, dKeyFold, o * Width, Width) + Dot(m_P, m_Value + r * Width, dValueFold, o * Width, Width);
                    var q = m_Queries[o * Rank + r];
                    Axpy(q, dKeyFold, o * Width, m_G, m_Key + r * Width, Width);
                    Axpy(q, dValueFold, o * Width, m_G, m_Value + r * Width, Width);
                }
            });
            var dNorm = new float[Count * Width];
            Parallel.For(0, Rank, r =>
            {
                for (var o = 0; o < Count; o++) Axpy(dQueries[o * Rank + r], m_OptionNorm, o * Width, m_G, m_Query + r * Width, Width);
            });
            Parallel.For(0, Count, o =>
            {
                for (var r = 0; r < Rank; r++) Axpy(dQueries[o * Rank + r], m_P, m_Query + r * Width, dNorm, o * Width, Width);
            });
            for (var o = 0; o < Count; o++)
                for (var i = 0; i < Width; i++)
                {
                    m_G[m_OptionGamma + i] += dNorm[o * Width + i] * m_OptionHat[o * Width + i];
                    m_G[m_OptionBeta + i] += dNorm[o * Width + i];
                }
        }

        /// <summary>Forward one sentence into <c>s.Probs</c>; with <paramref name="backward"/>, adds its gradients to the chunk's sums.</summary>
        double Pass(Example e, Scratch s, bool backward, ulong seed, ulong index, float invBatch)
        {
            int length = e.Length, n = Count, w = Width;
            s.Reserve(length);
            if (length <= 0)
            {
                Array.Fill(s.Probs, 1f / n);
                return -MathF.Log(1f / n);
            }

            var keep = 1f - (backward ? Dropout : 0f);
            var rng = Mix(seed * 0x9E3779B97F4A7C15UL + index + 1);
            for (var t = 0; t < length; t++)
            {
                var row = t * w;
                if (keep < 1f)
                    for (var i = 0; i < w; i++)
                    {
                        rng = Mix(rng);
                        s.Dropped[row + i] = (rng >> 40) * (1f / (1 << 24)) < keep ? e.States[row + i] / keep : 0f;
                    }
                else Array.Copy(e.States, row, s.Dropped, row, w);
                LayerNorm(s.Dropped, row, m_ContextGamma, m_ContextBeta, s.Hat, s.X, row);
            }

            for (var o = 0; o < n; o++)
            {
                var max = float.NegativeInfinity;
                for (var t = 0; t < length; t++)
                {
                    var score = Dot(m_KeyFold, o * w, s.X, t * w, w) * m_Scale;
                    s.Attention[o * length + t] = score;
                    s.Values[o * length + t] = Dot(m_ValueFold, o * w, s.X, t * w, w);
                    max = MathF.Max(max, score);
                }
                float sum = 0f, weighted = 0f;
                for (var t = 0; t < length; t++)
                {
                    var a = MathF.Exp(s.Attention[o * length + t] - max);
                    s.Attention[o * length + t] = a;
                    sum += a;
                }
                for (var t = 0; t < length; t++)
                {
                    s.Attention[o * length + t] /= sum;
                    weighted += s.Attention[o * length + t] * s.Values[o * length + t];
                }
                s.Logits[o] = weighted * m_Scale;
            }
            Softmax(s.Logits, s.Probs);

            double loss = 0;
            var off = LabelSmoothing / n;
            for (var o = 0; o < n; o++)
            {
                var target = (o == e.Label ? 1f - LabelSmoothing : 0f) + off;
                loss -= target * Math.Log(Math.Max(s.Probs[o], 1e-30f));
            }
            if (!backward) return loss;

            Array.Clear(s.DX, 0, length * w);
            for (var o = 0; o < n; o++)
            {
                var target = (o == e.Label ? 1f - LabelSmoothing : 0f) + off;
                var g = (s.Probs[o] - target) * invBatch * m_Scale;
                var mean = 0f;
                for (var t = 0; t < length; t++) mean += s.Attention[o * length + t] * g * s.Values[o * length + t];
                for (var t = 0; t < length; t++)
                {
                    var a = s.Attention[o * length + t];
                    var dValue = g * a;
                    var dScore = a * (g * s.Values[o * length + t] - mean) * m_Scale;
                    Axpy(dScore, s.X, t * w, s.KeyFold, o * w, w);
                    Axpy(dValue, s.X, t * w, s.ValueFold, o * w, w);
                    Axpy(dScore, m_KeyFold, o * w, s.DX, t * w, w);
                    Axpy(dValue, m_ValueFold, o * w, s.DX, t * w, w);
                }
            }
            for (var t = 0; t < length; t++)
                for (var i = 0; i < w; i++)
                {
                    var d = s.DX[t * w + i];
                    s.Gamma[i] += d * s.Hat[t * w + i];
                    s.Beta[i] += d;
                }
            return loss;
        }

        void ClipGradients()
        {
            double total = 0;
            foreach (var g in m_G) total += g * g;
            var norm = Math.Sqrt(total);
            var coefficient = ClipNorm / (norm + 1e-6);
            if (coefficient >= 1) return;
            for (var j = 0; j < m_G.Length; j++) m_G[j] *= (float)coefficient;
        }

        /// <summary>torch.optim.AdamW: decoupled weight decay on every parameter, bias-corrected moments.</summary>
        void AdamW()
        {
            m_Step++;
            var correction1 = 1f - MathF.Pow(Beta1, m_Step);
            var sqrtCorrection2 = MathF.Sqrt(1f - MathF.Pow(Beta2, m_Step));
            var decay = 1f - LearningRate * WeightDecay;
            var stepSize = LearningRate / correction1;
            for (var j = 0; j < m_P.Length; j++)
            {
                var g = m_G[j];
                m_M[j] = Beta1 * m_M[j] + (1f - Beta1) * g;
                m_V[j] = Beta2 * m_V[j] + (1f - Beta2) * g * g;
                m_P[j] = m_P[j] * decay - stepSize * m_M[j] / (MathF.Sqrt(m_V[j]) / sqrtCorrection2 + AdamEps);
            }
        }

        void LayerNorm(float[] x, int offset, int gamma, int beta, float[] hat, float[] output, int outOffset)
        {
            var mean = 0f;
            for (var i = 0; i < Width; i++) mean += x[offset + i];
            mean /= Width;
            var variance = 0f;
            for (var i = 0; i < Width; i++)
            {
                var d = x[offset + i] - mean;
                variance += d * d;
            }
            var inv = 1f / MathF.Sqrt(variance / Width + LayerNormEps);
            for (var i = 0; i < Width; i++)
            {
                var h = (x[offset + i] - mean) * inv;
                hat[outOffset + i] = h;
                output[outOffset + i] = h * m_P[gamma + i] + m_P[beta + i];
            }
        }

        static float Dot(float[] a, int aOffset, float[] b, int bOffset, int count)
        {
            var s = 0f;
            for (var i = 0; i < count; i++) s += a[aOffset + i] * b[bOffset + i];
            return s;
        }

        static void Axpy(float alpha, float[] x, int xOffset, float[] y, int yOffset, int count)
        {
            for (var i = 0; i < count; i++) y[yOffset + i] += alpha * x[xOffset + i];
        }

        static void Softmax(float[] logits, float[] probs)
        {
            var max = float.NegativeInfinity;
            foreach (var l in logits) max = MathF.Max(max, l);
            var sum = 0f;
            for (var i = 0; i < logits.Length; i++) sum += probs[i] = MathF.Exp(logits[i] - max);
            for (var i = 0; i < probs.Length; i++) probs[i] /= sum;
        }

        /// <summary>SplitMix64: a per-sentence dropout mask that doesn't depend on which thread runs it.</summary>
        static ulong Mix(ulong z)
        {
            z += 0x9E3779B97F4A7C15UL;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }

        /// <summary>Per-chunk buffers: activations of the current sentence and the chunk's gradient sums.</summary>
        sealed class Scratch
        {
            public readonly float[] Logits, Probs, KeyFold, ValueFold, Gamma, Beta;
            public float[] Dropped = Array.Empty<float>(), Hat = Array.Empty<float>(), X = Array.Empty<float>(), DX = Array.Empty<float>();
            public float[] Attention = Array.Empty<float>(), Values = Array.Empty<float>();
            public double Loss;
            readonly int m_Count, m_Width;

            public Scratch(int count, int width)
            {
                m_Count = count;
                m_Width = width;
                Logits = new float[count];
                Probs = new float[count];
                KeyFold = new float[count * width];
                ValueFold = new float[count * width];
                Gamma = new float[width];
                Beta = new float[width];
            }

            public void Reserve(int length)
            {
                if (X.Length >= length * m_Width) return;
                Dropped = new float[length * m_Width];
                Hat = new float[length * m_Width];
                X = new float[length * m_Width];
                DX = new float[length * m_Width];
                Attention = new float[length * m_Count];
                Values = new float[length * m_Count];
            }

            public void Clear()
            {
                Array.Clear(KeyFold, 0, KeyFold.Length);
                Array.Clear(ValueFold, 0, ValueFold.Length);
                Array.Clear(Gamma, 0, Gamma.Length);
                Array.Clear(Beta, 0, Beta.Length);
                Loss = 0;
            }
        }

        /// <summary>Writes the heads in the format <see cref="JevlikeRanker"/> reads.</summary>
        public static void WriteJson(string path, IReadOnlyList<HeadTrainer> heads, IReadOnlyList<string> intents, int layer, int maxTokens, float valAccuracy, float valEce)
        {
            var first = heads[0];
            var sb = new StringBuilder(first.m_P.Length * heads.Count * 12 + first.m_Options.Length * 12);
            sb.Append($"{{\"width\": {first.Width}, \"rank\": {first.Rank}, \"eps\": {Number(LayerNormEps)}, \"layer\": {layer}, \"max_tokens\": {maxTokens}, \"intents\": [");
            for (var i = 0; i < intents.Count; i++) sb.Append(i == 0 ? "" : ", ").Append('"').Append(intents[i]).Append('"');
            sb.Append("], \"options\": ");
            AppendArray(sb, first.m_Options, 0, first.m_Options.Length);
            sb.Append(", \"heads\": [");
            for (var h = 0; h < heads.Count; h++)
            {
                var t = heads[h];
                int w = t.Width, rw = t.Rank * t.Width;
                sb.Append(h == 0 ? "{" : ", {");
                sb.Append("\"context_norm_weight\": "); AppendArray(sb, t.m_P, t.m_ContextGamma, w);
                sb.Append(", \"context_norm_bias\": "); AppendArray(sb, t.m_P, t.m_ContextBeta, w);
                sb.Append(", \"option_norm_weight\": "); AppendArray(sb, t.m_P, t.m_OptionGamma, w);
                sb.Append(", \"option_norm_bias\": "); AppendArray(sb, t.m_P, t.m_OptionBeta, w);
                sb.Append(", \"query\": "); AppendArray(sb, t.m_P, t.m_Query, rw);
                sb.Append(", \"key\": "); AppendArray(sb, t.m_P, t.m_Key, rw);
                sb.Append(", \"value\": "); AppendArray(sb, t.m_P, t.m_Value, rw);
                sb.Append('}');
            }
            sb.Append($"], \"val_accuracy\": {Number(valAccuracy)}, \"val_ece\": {Number(valEce)}}}");
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            File.WriteAllText(path, sb.ToString());
        }

        static void AppendArray(StringBuilder sb, float[] values, int offset, int count)
        {
            sb.Append('[');
            for (var i = 0; i < count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(Number(values[offset + i]));
            }
            sb.Append(']');
        }

        static string Number(float value) => value.ToString("G9", CultureInfo.InvariantCulture);
    }
}
