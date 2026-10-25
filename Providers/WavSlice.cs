using System;
using System.IO;
using System.Text;

namespace WhisperSubs.Providers
{
    /// <summary>
    /// Cuts a stretch out of a PCM WAV by copying bytes, with no ffmpeg run: the extracted audio is 16 kHz
    /// mono pcm_s16le, so a slice is a fresh 44-byte header and the data bytes in between. akou's language
    /// detection sends one 30 s slice at a time instead of the whole 600 s window.
    /// </summary>
    internal static class WavSlice
    {
        /// <summary>The size of the canonical header this writes.</summary>
        internal const int HeaderBytes = 44;

        /// <summary>
        /// Copies <paramref name="seconds"/> of audio starting at <paramref name="startSeconds"/> from
        /// <paramref name="source"/> to <paramref name="destination"/> as a canonical WAV. Returns the data
        /// bytes written: fewer than asked when the file ends first, 0 when it ends before the start. Throws
        /// <see cref="InvalidDataException"/> for anything but an uncompressed PCM WAV. Pure over the streams.
        /// </summary>
        internal static long Copy(Stream source, Stream destination, double startSeconds, double seconds)
        {
            var (format, dataOffset, dataLength) = ReadLayout(source);
            var blockAlign = Math.Max(1, (int)format.BlockAlign);
            long Align(double bytes) => (long)Math.Floor(bytes / blockAlign) * blockAlign;
            var start = Math.Min(dataLength, Align(Math.Max(0, startSeconds) * format.ByteRate));
            var length = Math.Min(dataLength - start, Align(Math.Max(0, seconds) * format.ByteRate));

            WriteHeader(destination, format, length);
            source.Seek(dataOffset + start, SeekOrigin.Begin);
            var buffer = new byte[81920];
            var left = length;
            while (left > 0)
            {
                var read = source.Read(buffer, 0, (int)Math.Min(buffer.Length, left));
                if (read <= 0) break;
                destination.Write(buffer, 0, read);
                left -= read;
            }
            if (left > 0)
            {
                // The data chunk said more than the file holds: fix the sizes to what was copied.
                var copied = length - left;
                destination.Seek(0, SeekOrigin.Begin);
                WriteHeader(destination, format, copied);
                destination.Seek(0, SeekOrigin.End);
                return copied;
            }
            return length;
        }

        /// <summary>The same, from one file to another. Returns the data bytes written.</summary>
        internal static long CopyFile(string sourcePath, string destinationPath, double startSeconds, double seconds)
        {
            using var source = File.OpenRead(sourcePath);
            using var destination = File.Create(destinationPath);
            return Copy(source, destination, startSeconds, seconds);
        }

        internal readonly record struct PcmFormat(short Channels, int SampleRate, int ByteRate, short BlockAlign, short BitsPerSample);

        private static (PcmFormat Format, long DataOffset, long DataLength) ReadLayout(Stream source)
        {
            if (!source.CanSeek) throw new InvalidDataException("The WAV stream must be seekable.");
            source.Seek(0, SeekOrigin.Begin);
            using var reader = new BinaryReader(source, Encoding.ASCII, leaveOpen: true);
            if (source.Length < 12 || new string(reader.ReadChars(4)) != "RIFF")
                throw new InvalidDataException("Not a RIFF file.");
            reader.ReadUInt32();
            if (new string(reader.ReadChars(4)) != "WAVE") throw new InvalidDataException("Not a WAVE file.");

            PcmFormat? format = null;
            while (source.Position + 8 <= source.Length)
            {
                var id = new string(reader.ReadChars(4));
                var size = reader.ReadUInt32();
                var bodyStart = source.Position;
                if (id == "fmt ")
                {
                    var audioFormat = reader.ReadInt16();
                    var channels = reader.ReadInt16();
                    var sampleRate = reader.ReadInt32();
                    var byteRate = reader.ReadInt32();
                    var blockAlign = reader.ReadInt16();
                    var bits = reader.ReadInt16();
                    // 1 is PCM; 0xFFFE (extensible) carries PCM too in what ffmpeg writes.
                    if (audioFormat != 1 && audioFormat != unchecked((short)0xFFFE))
                        throw new InvalidDataException("Only uncompressed PCM WAV can be sliced.");
                    if (byteRate <= 0) throw new InvalidDataException("The WAV header has no byte rate.");
                    format = new PcmFormat(channels, sampleRate, byteRate, blockAlign, bits);
                }
                else if (id == "data")
                {
                    if (format is null) throw new InvalidDataException("The WAV data chunk comes before its format.");
                    // A streamed WAV can carry 0 or 0xFFFFFFFF here; what is in the file is what counts.
                    var available = source.Length - bodyStart;
                    var length = size == 0 || size > available ? available : size;
                    return (format.Value, bodyStart, length);
                }
                source.Seek(bodyStart + size + (size % 2), SeekOrigin.Begin);
            }
            throw new InvalidDataException("The WAV file has no data chunk.");
        }

        private static void WriteHeader(Stream destination, PcmFormat format, long dataLength)
        {
            using var writer = new BinaryWriter(destination, Encoding.ASCII, leaveOpen: true);
            writer.Write(Encoding.ASCII.GetBytes("RIFF"));
            writer.Write((uint)(36 + dataLength));
            writer.Write(Encoding.ASCII.GetBytes("WAVE"));
            writer.Write(Encoding.ASCII.GetBytes("fmt "));
            writer.Write(16u);
            writer.Write((short)1);
            writer.Write(format.Channels);
            writer.Write(format.SampleRate);
            writer.Write(format.ByteRate);
            writer.Write(format.BlockAlign);
            writer.Write(format.BitsPerSample);
            writer.Write(Encoding.ASCII.GetBytes("data"));
            writer.Write((uint)dataLength);
        }
    }
}
