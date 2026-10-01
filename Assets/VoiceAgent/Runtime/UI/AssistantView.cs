using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace VoiceAgent.UI
{
    public enum MicState { Off, Idle, Listening, Thinking, Speaking }

    /// <summary>
    /// The assistant screen: chat log above the "Ask…" bar (mic orb, status line) and a suggestion panel listing
    /// the likeliest commands, best first. Chat bubbles are copies of the two disabled message templates in the log.
    /// </summary>
    public sealed class AssistantView : MonoBehaviour
    {
        [SerializeField] Text m_Status, m_OrbLabel;
        [SerializeField] RectTransform m_Content, m_Log;
        [SerializeField] ScrollRect m_Scroll;
        [Tooltip("Suggestion panel rows, top to bottom; the most likely command fills the first.")]
        [SerializeField] ChipView[] m_Chips;
        [SerializeField] GameObject m_ChipsHint;
        [SerializeField] InputField m_Input;
        [SerializeField] Image m_OrbRing;
        [SerializeField] Button m_OrbButton;
        [SerializeField] MessageView m_UserTemplate, m_AssistantTemplate;

        public event Action<string> TextChanged;
        public event Action<string> Submitted;
        public event Action<Command> ChipClicked;
        public event Action MicClicked;

        /// <summary>Whether Enter may submit now; the controller says no while a turn is still answering.</summary>
        public Func<bool> CanSubmit;

        public const string BusyNotice = "Still answering the last question…";

        MicState m_Mic = MicState.Off;
        bool m_ShowingBusyNotice;
        float m_Level;

        public int MaxChips => m_Chips.Length;
        public RectTransform Content => m_Content;
        public string InputText => m_Input.text;

        void Awake()
        {
            m_Input.onValueChanged.AddListener(v => TextChanged?.Invoke(v));
            m_Input.onSubmit.AddListener(OnSubmit);
            m_OrbButton.onClick.AddListener(() => MicClicked?.Invoke());
            foreach (var chip in m_Chips) chip.Clicked += c => ChipClicked?.Invoke(c);
            SetChips(null);
            SetMicState(MicState.Off);
        }

        void OnSubmit(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            // While a turn is still answering, the text stays in the field for another Enter instead of being dropped.
            if (CanSubmit != null && !CanSubmit())
            {
                SetStatus(BusyNotice);
                m_ShowingBusyNotice = true;
                m_Input.ActivateInputField();
                return;
            }
            if (m_ShowingBusyNotice) SetStatus(string.Empty);
            m_ShowingBusyNotice = false;
            m_Input.SetTextWithoutNotify(string.Empty);
            Submitted?.Invoke(value.Trim());
            m_Input.ActivateInputField();
        }

        public void FocusInput() => m_Input.ActivateInputField();
        public void SetInputText(string value) => m_Input.SetTextWithoutNotify(value ?? string.Empty);
        public void SetStatus(string value) => m_Status.text = value;
        public void SetInteractable(bool value) => m_Input.interactable = value;

        public void SetChips(IReadOnlyList<RankedCommand> ranked)
        {
            for (var rank = 0; rank < m_Chips.Length; rank++)
            {
                if (ranked != null && rank < ranked.Count) m_Chips[rank].Show(ranked[rank], rank == 0);
                else m_Chips[rank].Hide();
            }
            m_ChipsHint.SetActive(ranked == null || ranked.Count == 0);
        }

        public MessageView AddMessage(string text, bool fromUser)
        {
            var message = Instantiate(fromUser ? m_UserTemplate : m_AssistantTemplate, m_Log);
            message.gameObject.SetActive(true);
            message.SetText(text);
            ScrollToEnd();
            return message;
        }

        public void UpdateMessage(MessageView message, string text)
        {
            message.SetText(text);
            ScrollToEnd();
        }

        void ScrollToEnd()
        {
            Canvas.ForceUpdateCanvases();
            m_Scroll.verticalNormalizedPosition = 0;
        }

        public void SetMicState(MicState state, float level = 0f)
        {
            m_Mic = state;
            m_Level = level;
            m_OrbLabel.text = state switch { MicState.Off => "…", MicState.Listening => "●", MicState.Thinking => "…", MicState.Speaking => "♪", _ => "MIC" };
        }

        void Update()
        {
            var color = m_Mic switch
            {
                MicState.Off => new Color(0.6f, 0.6f, 0.65f),
                MicState.Listening => Color.Lerp(new Color(1f, 0.3f, 0.4f), new Color(1f, 0.6f, 0.2f), Mathf.PingPong(Time.time * 2f, 1f)),
                MicState.Thinking => Color.Lerp(new Color(0.55f, 0.4f, 1f), new Color(0.3f, 0.8f, 1f), Mathf.PingPong(Time.time * 1.5f, 1f)),
                MicState.Speaking => new Color(0.3f, 0.85f, 0.7f),
                _ => new Color(0.95f, 0.4f, 0.5f),
            };
            m_OrbRing.color = color;
            var scale = m_Mic == MicState.Listening ? 1f + Mathf.Clamp01(m_Level * 6f) * 0.25f : 1f;
            m_OrbRing.rectTransform.localScale = Vector3.Lerp(m_OrbRing.rectTransform.localScale, Vector3.one * scale, Time.deltaTime * 12f);
        }
    }
}
