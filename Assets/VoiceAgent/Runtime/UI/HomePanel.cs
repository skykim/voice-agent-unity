using UnityEngine;
using UnityEngine.UI;

namespace VoiceAgent.UI
{
    /// <summary>Tiles showing the simulated home so each command has a visible effect.</summary>
    public sealed class HomePanel : MonoBehaviour
    {
        [SerializeField] HomeTile m_Light, m_Tv, m_Computer, m_Vacuum, m_Music;

        SmartHome m_Home;

        public void Bind(SmartHome home)
        {
            if (m_Home != null) m_Home.Changed -= Refresh;
            m_Home = home;
            m_Home.Changed += Refresh;
            Refresh();
        }

        void Update()
        {
            if (m_Home == null) return;
            var vacuuming = m_Home.Vacuuming;
            m_Vacuum.Set(vacuuming, new Color(0.5f, 0.85f, 0.2f), vacuuming ? $"Cleaning {Mathf.CeilToInt(m_Home.VacuumUntil - Time.time)}s" : "Idle");
            m_Vacuum.Icon.rectTransform.localRotation = vacuuming ? Quaternion.Euler(0, 0, -Time.time * 180f) : Quaternion.identity;
            m_Music.Icon.rectTransform.localScale = Vector3.one * (m_Home.MusicOn ? 1f + 0.08f * Mathf.Sin(Time.time * 8f) : 1f);
        }

        void Refresh()
        {
            var c = m_Home.CurrentColor;
            m_Light.Set(m_Home.LightOn, c.Value, m_Home.LightOn ? c.Name : "Off");
            m_Tv.Set(m_Home.TvOn, new Color(0.3f, 0.55f, 0.95f), m_Home.TvOn ? "On" : "Off");
            m_Computer.Set(m_Home.ComputerOn, new Color(0.2f, 0.75f, 0.6f), m_Home.ComputerOn ? "On" : "Off");
            m_Music.Set(m_Home.MusicOn, new Color(1f, 0.35f, 0.45f), $"{(m_Home.MusicOn ? "Playing" : "Stopped")} / volume {m_Home.Volume}%");
        }

        void OnDestroy()
        {
            if (m_Home != null) m_Home.Changed -= Refresh;
        }
    }
}
