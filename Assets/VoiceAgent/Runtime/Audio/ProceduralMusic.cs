using UnityEngine;

namespace VoiceAgent
{
    /// <summary>A soft four-chord pad loop, generated once, standing in for a music player.</summary>
    [RequireComponent(typeof(AudioSource))]
    public sealed class ProceduralMusic : MonoBehaviour
    {
        const int PadRate = 22050;
        static readonly float[][] s_Chords =
        {
            new[] { 261.63f, 329.63f, 392.00f }, // C
            new[] { 220.00f, 261.63f, 329.63f }, // Am
            new[] { 174.61f, 220.00f, 261.63f }, // F
            new[] { 196.00f, 246.94f, 293.66f }, // G
        };

        AudioSource m_Source;
        float m_Duck = 1f;

        public float Volume { get; set; } = 0.5f;
        public bool Ducked { get; set; }

        void Awake()
        {
            m_Source = GetComponent<AudioSource>();
            m_Source.loop = true;
            m_Source.playOnAwake = false;
            m_Source.clip = Build();
        }

        public void SetPlaying(bool playing)
        {
            if (playing && !m_Source.isPlaying) m_Source.Play();
            else if (!playing && m_Source.isPlaying) m_Source.Stop();
        }

        void Update()
        {
            m_Duck = Mathf.MoveTowards(m_Duck, Ducked ? 0.15f : 1f, Time.deltaTime * 3f);
            m_Source.volume = Volume * m_Duck * 0.35f;
        }

        void OnDestroy()
        {
            if (m_Source != null && m_Source.clip != null) Destroy(m_Source.clip);
        }

        static AudioClip Build()
        {
            var data = BuildPad();
            var clip = AudioClip.Create("Pad", data.Length, 1, PadRate, false);
            clip.SetData(data, 0);
            return clip;
        }

        /// <summary>One 8-second loop of the pad, mono at <see cref="PadRate"/>.</summary>
        static float[] BuildPad()
        {
            const float chordSeconds = 2f;
            var perChord = (int)(PadRate * chordSeconds);
            var data = new float[perChord * s_Chords.Length];
            for (var c = 0; c < s_Chords.Length; c++)
                for (var i = 0; i < perChord; i++)
                {
                    var t = i / (float)PadRate;
                    var envelope = Mathf.Min(1f, t * 4f) * Mathf.Min(1f, (chordSeconds - t) * 4f);
                    var s = 0f;
                    foreach (var f in s_Chords[c])
                        s += Mathf.Sin(2f * Mathf.PI * f * t) * 0.6f + Mathf.Sin(2f * Mathf.PI * f * 2f * t) * 0.15f;
                    data[c * perChord + i] = s / 3f * envelope * 0.5f;
                }
            return data;
        }
    }
}
