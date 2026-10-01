using SentisModels;

namespace VoiceAgent
{
    /// <summary>
    /// SenseVoice detects five languages, but Nova understands English and Korean. Short utterances are sometimes
    /// labelled with a neighbour (Korean as Japanese, English as Chinese); those are decoded again with the language
    /// that was most likely meant.
    /// </summary>
    public static class SpeechLanguage
    {
        public static SenseVoiceLanguage? Retry(SenseVoiceLanguage detected) => detected switch
        {
            SenseVoiceLanguage.Ja => SenseVoiceLanguage.Ko,
            SenseVoiceLanguage.Zh or SenseVoiceLanguage.Yue => SenseVoiceLanguage.En,
            _ => null,
        };
    }
}
