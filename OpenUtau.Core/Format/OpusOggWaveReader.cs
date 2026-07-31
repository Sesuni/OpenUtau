using System;
using System.IO;
using Concentus.Oggfile;
using Concentus.Structs;
using NAudio.Wave;

namespace OpenUtau.Core.Format {
    // Preliminary blocking Opus reader.
    public class OpusOggWaveReader : WaveStream {
        private readonly byte[] wavData;

        public override WaveFormat WaveFormat { get; } = new WaveFormat(48000, 16, 2);
        public override long Length => wavData.LongLength;
        public override long Position { get; set; }

        // Constructor: Decode locally to save memory
        public OpusOggWaveReader(string oggFile) {
            using var fileStream = File.OpenRead(oggFile);
            using var oggStream = new MemoryStream();
            fileStream.CopyTo(oggStream);
            oggStream.Position = 0;
            wavData = Decode(oggStream);
        }

        // Decode oggStream to wavData
        private static byte[] Decode(Stream oggStream) {
            var decoder = new OpusDecoder(48000, 2);
            var oggIn = new OpusOggReadStream(decoder, oggStream);
            using var wavStream = new MemoryStream();

            while (oggIn.HasNextPacket) {
                short[] packet = oggIn.DecodeNextPacket();
                if (packet != null) {
                    byte[] binary = ShortsToBytes(packet);
                    wavStream.Write(binary, 0, binary.Length);
                }
            }
            return wavStream.ToArray();
        }


        public override int Read(byte[] buffer, int offset, int count) {
            int read = (int)Math.Min(wavData.Length - Position, count);
            if (read <= 0) { return 0; }

            Array.Copy(wavData, Position, buffer, offset, read);
            Position += read;
            return read;
        }


        static byte[] ShortsToBytes(short[] input) {
            byte[] output = new byte[input.Length * sizeof(short)];
            Buffer.BlockCopy(input, 0, output, 0, output.Length);
            return output;
        }

    } // class OpusOggWaveReader
} // namespace .Core.Format
