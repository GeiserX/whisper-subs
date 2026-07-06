using System;

namespace WhisperSubs.Controller.Workers
{
    /// <summary>
    /// Builds a tiny in-memory 16 kHz mono s16le WAV of silence for the worker "Test connection" probe
    /// (v4.0). Every OpenAI-compatible endpoint can transcribe it, so the test exercises the real
    /// transcribe path (URL + auth + model) without shipping a fixture file or touching the media library.
    /// Pure + unit-testable — the HTTP POST that sends it lives in the (excluded) controller.
    /// </summary>
    public static class SyntheticAudio
    {
        private const int SampleRate = 16000;   // matches the plugin's ffmpeg extraction
        private const short Channels = 1;
        private const short BitsPerSample = 16;

        /// <summary>
        /// A canonical 44-byte-header WAV containing <paramref name="milliseconds"/> of silence
        /// (clamped to 10..2000 ms). Layout is little-endian PCM, exactly what whisper.cpp / faster-whisper
        /// expect.
        /// </summary>
        public static byte[] SilentWav16kMono(int milliseconds = 100)
        {
            if (milliseconds < 10) milliseconds = 10;
            if (milliseconds > 2000) milliseconds = 2000;

            var samples = SampleRate * milliseconds / 1000;
            var dataBytes = samples * Channels * (BitsPerSample / 8);
            var buffer = new byte[44 + dataBytes];

            void PutAscii(int offset, string s)
            {
                for (var i = 0; i < s.Length; i++) buffer[offset + i] = (byte)s[i];
            }
            void PutInt32(int offset, int value) => BitConverter.GetBytes(value).CopyTo(buffer, offset);
            void PutInt16(int offset, short value) => BitConverter.GetBytes(value).CopyTo(buffer, offset);

            var byteRate = SampleRate * Channels * (BitsPerSample / 8);
            var blockAlign = (short)(Channels * (BitsPerSample / 8));

            PutAscii(0, "RIFF");
            PutInt32(4, 36 + dataBytes);      // ChunkSize = 36 + Subchunk2Size
            PutAscii(8, "WAVE");
            PutAscii(12, "fmt ");
            PutInt32(16, 16);                 // Subchunk1Size (PCM)
            PutInt16(20, 1);                  // AudioFormat = PCM
            PutInt16(22, Channels);
            PutInt32(24, SampleRate);
            PutInt32(28, byteRate);
            PutInt16(32, blockAlign);
            PutInt16(34, BitsPerSample);
            PutAscii(36, "data");
            PutInt32(40, dataBytes);          // Subchunk2Size
            // Samples are left zeroed = silence.

            return buffer;
        }
    }
}
