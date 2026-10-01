using UnityEngine;
using UnityEngine.UI;

namespace VoiceAgent.UI
{
    /// <summary>
    /// The assistant's backdrop: a vertical gradient that takes the light's color while the light is on, as if the room
    /// glowed, and eases back to near black when it goes off. The texture is drawn here, so it replaces the
    /// image the scene shows at edit time.
    /// </summary>
    [RequireComponent(typeof(RawImage))]
    public sealed class AmbientBackground : MonoBehaviour
    {
        const int Height = 256;

        [Tooltip("Bottom to top, while the light is off: near black (the colors of background_assistant.png).")]
        [SerializeField] Color[] m_Off =
        {
            new(0.16f, 0.16f, 0.18f), new(0.11f, 0.11f, 0.13f), new(0.07f, 0.07f, 0.09f), new(0.04f, 0.04f, 0.05f),
        };
        [SerializeField] float m_FadeSeconds = 0.8f;

        SmartHome m_Home;
        Texture2D m_Texture;
        Color32[] m_Pixels;
        Color[] m_Shown, m_From, m_To;
        float m_Fade = 1f;

        void Awake()
        {
            m_Texture = new Texture2D(1, Height, TextureFormat.RGBA32, false) { name = "Ambient", wrapMode = TextureWrapMode.Clamp };
            m_Pixels = new Color32[Height];
            m_Shown = (Color[])m_Off.Clone();
            m_To = m_Shown;
            Paint();
            GetComponent<RawImage>().texture = m_Texture;
        }

        public void Bind(SmartHome home)
        {
            if (m_Home != null) m_Home.Changed -= Retarget;
            m_Home = home;
            m_Home.Changed += Retarget;
            Retarget();
        }

        /// <summary>The gradient for a lit room: the light's color at the bottom, darkening into the dark top of the off gradient.</summary>
        public Color[] Lit(Color light) => new[]
        {
            Color.Lerp(light, Color.black, 0.1f),
            Color.Lerp(light, m_Off[1], 0.55f),
            Color.Lerp(light, m_Off[2], 0.8f),
            m_Off[3],
        };

        void Retarget()
        {
            var target = m_Home.LightOn ? Lit(m_Home.CurrentColor.Value) : m_Off;
            if (Same(target, m_To)) return;
            m_From = (Color[])m_Shown.Clone();
            m_To = target;
            m_Fade = 0f;
        }

        void Update()
        {
            if (m_Fade >= 1f) return;
            m_Fade = Mathf.Min(1f, m_Fade + Time.deltaTime / Mathf.Max(0.01f, m_FadeSeconds));
            var t = Mathf.SmoothStep(0f, 1f, m_Fade);
            for (var i = 0; i < m_Shown.Length; i++) m_Shown[i] = Color.Lerp(m_From[i], m_To[i], t);
            Paint();
        }

        /// <summary>Evenly spaced color stops from the bottom row up, blended linearly.</summary>
        void Paint()
        {
            for (var y = 0; y < Height; y++)
            {
                var t = y / (float)(Height - 1) * (m_Shown.Length - 1);
                var i = Mathf.Min((int)t, m_Shown.Length - 2);
                m_Pixels[y] = Color.Lerp(m_Shown[i], m_Shown[i + 1], t - i);
            }
            m_Texture.SetPixels32(m_Pixels);
            m_Texture.Apply(false);
        }

        static bool Same(Color[] a, Color[] b)
        {
            if (a.Length != b.Length) return false;
            for (var i = 0; i < a.Length; i++)
                if (a[i] != b[i]) return false;
            return true;
        }

        void OnDestroy()
        {
            if (m_Home != null) m_Home.Changed -= Retarget;
            if (m_Texture != null) Destroy(m_Texture);
        }
    }
}
