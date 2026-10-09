using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace VoiceAgent
{
    /// <summary>
    /// The frozen encoder sees Korean "turn the sound up" and "turn the sound down" as nearly the same sentence, so the head sometimes picks the
    /// wrong side of an on/off pair. When the head's top pick is one side of a pair (lights, TV, computer, music, volume)
    /// and the sentence names exactly one direction, that side gets the pair's whole probability. The head still decides
    /// what the sentence is about; the words only decide the direction. Resources/Polarity.json lists the pairs and
    /// words; the trainer scores its on/off check with the same rule.
    /// </summary>
    public sealed class PolarityGuard
    {
        [Serializable] sealed class PairSpec { public string on, off, family; }
        [Serializable] sealed class FamilySpec { public string name; public string[] on, off, skip; }
        [Serializable] sealed class Spec { public PairSpec[] pairs; public FamilySpec[] families; }

        readonly List<(int On, int Off, FamilySpec Family)> m_Pairs = new();

        PolarityGuard(Spec spec, string[] intents)
        {
            foreach (var p in spec.pairs)
            {
                int on = Array.IndexOf(intents, p.on), off = Array.IndexOf(intents, p.off);
                var family = spec.families.FirstOrDefault(f => f.name == p.family);
                if (on >= 0 && off >= 0 && family != null) m_Pairs.Add((on, off, family));
            }
        }

        public static PolarityGuard Load(string[] intents) =>
            new(JsonUtility.FromJson<Spec>(Resources.Load<TextAsset>("Polarity").text), intents);

        /// <summary><paramref name="normalized"/> is <see cref="DecisionAIRanker.Normalize"/>d text; returns adjusted probabilities.</summary>
        public float[] Apply(string normalized, float[] probs)
        {
            var top = 0;
            for (var i = 1; i < probs.Length; i++)
                if (probs[i] > probs[top]) top = i;
            foreach (var (on, off, family) in m_Pairs)
            {
                if ((top != on && top != off) || Names(normalized, family.skip)) continue;
                bool hasOn = Names(normalized, family.on), hasOff = Names(normalized, family.off);
                if (hasOn == hasOff) continue;
                probs = (float[])probs.Clone();
                var total = probs[on] + probs[off];
                probs[on] = hasOn ? total : 0f;
                probs[off] = hasOn ? 0f : total;
            }
            return probs;
        }

        /// <summary>English cues match whole words; Korean cues match inside words, because Korean attaches endings to verb stems.</summary>
        static bool Names(string text, string[] cues)
        {
            var padded = $" {text} ";
            foreach (var cue in cues)
                if (cue.All(c => c < 128) ? padded.Contains($" {cue} ", StringComparison.Ordinal) : text.Contains(cue, StringComparison.Ordinal))
                    return true;
            return false;
        }
    }
}
