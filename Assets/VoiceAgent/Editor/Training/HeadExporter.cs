using System.Collections.Generic;
using System.IO;
using System.Linq;
using Unity.InferenceEngine;
using UnityEngine;

namespace VoiceAgent.Editor
{
    /// <summary>
    /// Writes trained heads as the one file <see cref="DecisionAIRanker"/> loads: a Sentis graph from token states
    /// [length × width] to command probabilities (the heads averaged), with the head's settings as constant outputs.
    /// </summary>
    public static class HeadExporter
    {
        public static void Write(string path, IReadOnlyList<HeadTrainer> heads, IReadOnlyList<string> intents, int layer, int maxTokens, float valAccuracy, float valEce)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            ModelWriter.Save(path, Graph(heads, intents, layer, maxTokens, valAccuracy, valEce));
        }

        /// <summary>
        /// Per head: LayerNorm the token states, score them against each command's folded key and value weights, softmax
        /// over the tokens, and softmax the value sums over the commands, as <see cref="HeadTrainer"/> does without dropout.
        /// The other outputs are constants: the Gemma3 layer, the token limit, the command ids (UTF-16 code units, one id per
        /// line) and the validation accuracy and calibration error.
        /// </summary>
        public static Model Graph(IReadOnlyList<HeadTrainer> heads, IReadOnlyList<string> intents, int layer, int maxTokens, float valAccuracy, float valEce)
        {
            int width = heads[0].Width, count = heads[0].Count;
            var scale = 1f / Mathf.Sqrt(heads[0].Rank);
            var graph = new FunctionalGraph();
            var states = graph.AddInput<float>(new DynamicTensorShape(-1, width), DecisionAIRanker.InputName);
            FunctionalTensor sum = null;
            foreach (var head in heads)
            {
                var (gamma, beta, keyFold, valueFold) = head.Folded();
                var x = Functional.LayerNorm(states, Functional.Constant(new TensorShape(width), gamma), Functional.Constant(new TensorShape(width), beta), HeadTrainer.LayerNormEps);
                var scores = Functional.MatMul(x, Columns(keyFold, count, width)) * scale;
                var values = Functional.MatMul(x, Columns(valueFold, count, width));
                var logits = Functional.ReduceSum(Functional.Softmax(scores, 0) * values, 0) * scale;
                var probs = Functional.Softmax(logits);
                sum = sum == null ? probs : sum + probs;
            }
            var outputs = new (string Name, FunctionalTensor Tensor)[]
            {
                (DecisionAIRanker.ProbsName, heads.Count == 1 ? sum : sum * (1f / heads.Count)),
                (DecisionAIRanker.LayerName, Functional.Constant(new[] { layer })),
                (DecisionAIRanker.MaxTokensName, Functional.Constant(new[] { maxTokens })),
                (DecisionAIRanker.IntentsName, Functional.Constant(string.Join("\n", intents).Select(c => (int)c).ToArray())),
                (DecisionAIRanker.ValidationName, Functional.Constant(new[] { valAccuracy, valEce })),
            };
            var model = graph.Compile(outputs.Select(o => o.Tensor).ToArray());
            for (var i = 0; i < outputs.Length; i++) model.outputs[i] = new Model.Output { name = outputs[i].Name, index = model.outputs[i].index };
            return model;
        }

        /// <summary>A [count × width] row-major matrix as a [width × count] constant, so token states multiply it directly.</summary>
        static FunctionalTensor Columns(float[] rows, int count, int width)
        {
            var columns = new float[rows.Length];
            for (var o = 0; o < count; o++)
                for (var i = 0; i < width; i++)
                    columns[i * count + o] = rows[o * width + i];
            return Functional.Constant(new TensorShape(width, count), columns);
        }
    }
}
