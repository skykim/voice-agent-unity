using System;
using UnityEngine;

namespace VoiceAgent
{
    /// <summary>
    /// Polls <see cref="Microphone"/> into 16 kHz mono chunks (linear resampling when the device can't open at 16 kHz).
    /// Call <see cref="Poll"/> once per frame; <see cref="Samples"/> fires with each new chunk and its stream index.
    /// </summary>
    public sealed class MicrophoneStream : IDisposable
    {
        public const int SampleRate = 16000;
        const int ClipSeconds = 4;

        readonly string m_Device;
        AudioClip m_Clip;
        int m_DeviceRate, m_ReadPosition;
        float[] m_Raw = new float[4096];
        float[] m_Out = new float[4096];
        double m_Phase;
        float m_Previous;
        long m_Sequence;

        public event Action<float[], int, long> Samples;

        public MicrophoneStream(string device = null) => m_Device = device;

        public bool IsRunning => m_Clip != null && Microphone.IsRecording(m_Device);
        public string DeviceName => m_Device ?? (Microphone.devices.Length > 0 ? Microphone.devices[0] : "(none)");
        public float Level { get; private set; }

        public bool Start()
        {
            if (IsRunning) return true;
            if (Microphone.devices.Length == 0) return false;
            // The device went away since the last start: its clip is dead.
            if (m_Clip != null) UnityEngine.Object.Destroy(m_Clip);
            Microphone.GetDeviceCaps(m_Device, out var min, out var max);
            m_DeviceRate = min == 0 && max == 0 ? SampleRate : Mathf.Clamp(SampleRate, min, max);
            m_Clip = Microphone.Start(m_Device, true, ClipSeconds, m_DeviceRate);
            m_ReadPosition = 0;
            m_Phase = 0;
            m_Previous = 0;
            return m_Clip != null;
        }

        public void Stop()
        {
            if (m_Clip == null) return;
            Microphone.End(m_Device);
            UnityEngine.Object.Destroy(m_Clip);
            m_Clip = null;
        }

        public void Poll()
        {
            if (!IsRunning) return;
            var position = Microphone.GetPosition(m_Device);
            var available = (position - m_ReadPosition + m_Clip.samples) % m_Clip.samples;
            if (available == 0) return;
            if (m_Raw.Length < available) m_Raw = new float[Mathf.NextPowerOfTwo(available)];
            m_Clip.GetData(m_Raw.AsSpan(0, available), m_ReadPosition);
            m_ReadPosition = (m_ReadPosition + available) % m_Clip.samples;

            var count = m_DeviceRate == SampleRate ? Copy(available) : Resample(available);
            if (count == 0) return;
            var peak = 0f;
            for (var i = 0; i < count; i++) peak = Mathf.Max(peak, Mathf.Abs(m_Out[i]));
            Level = Mathf.Lerp(Level, peak, 0.5f);
            Samples?.Invoke(m_Out, count, m_Sequence);
            m_Sequence += count;
        }

        int Copy(int count)
        {
            if (m_Out.Length < count) m_Out = new float[m_Raw.Length];
            Array.Copy(m_Raw, m_Out, count);
            return count;
        }

        int Resample(int count)
        {
            var step = (double)m_DeviceRate / SampleRate;
            var needed = (int)(count / step) + 2;
            if (m_Out.Length < needed) m_Out = new float[Mathf.NextPowerOfTwo(needed)];
            var written = 0;
            while (m_Phase < count - 1)
            {
                // A phase in [-1, 0) falls between the previous chunk's last sample and this chunk's first.
                var i = (int)Math.Floor(m_Phase);
                var from = i < 0 ? m_Previous : m_Raw[i];
                var to = m_Raw[i + 1];
                m_Out[written++] = from + (to - from) * (float)(m_Phase - i);
                m_Phase += step;
            }
            m_Phase -= count;
            m_Previous = m_Raw[count - 1];
            return written;
        }

        public void Dispose() => Stop();
    }
}
