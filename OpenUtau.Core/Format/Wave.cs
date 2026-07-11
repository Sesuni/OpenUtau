using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NAudio.Flac;
using NAudio.Vorbis;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using NLayer.NAudioSupport;
using NWaves.Signals;

namespace OpenUtau.Core.Format {
    public static class Wave {
        // Hook custom mp3 decoder if desired
        public static Func<string, WaveStream> OverrideMp3Reader;

        // Method: Read first few bytes to check actual format
        public static WaveStream OpenFile(string filepath) {
            var ext = Path.GetExtension(filepath);
            byte[] buffer = new byte[128];
            string tag = "";
            
            // Store first bytes on tag
            using (var stream = File.Open(filepath, FileMode.Open, FileAccess.Read, FileShare.Read)) {
                if (stream.CanSeek) {
                    stream.Read(buffer, 0, 128);
                    tag = System.Text.Encoding.UTF8.GetString(buffer.AsSpan(0, 4));
                }
            }
            
            // If RIFF, standard WAV file
            if (tag == "RIFF") {
                return new WaveFileReader(filepath);
            }

            // Either Ogg Vorbis or modern Opus
            if (tag == "OggS") {
                string text = System.Text.Encoding.ASCII.GetString(buffer);
                if (text.Contains("vorbis")) {
                    return new VorbisWaveReader(filepath);
                }
                if (text.Contains("OpusHead")) {
                    return new OpusOggWaveReader(filepath);
                }
            }
            
            // Lossless FLAC file
            if (tag == "fLaC") {
                return new FlacReader(filepath);
            }
            
            // Trivial extension checks
            if (ext == ".mp3") {
                if (OverrideMp3Reader != null) {
                    return OverrideMp3Reader(filepath);
                }
                return new Mp3FileReaderBase(filepath, wf => new Mp3FrameDecompressor(wf));
            }
          
            if (ext == ".aiff" || ext == ".aif" || ext == ".aifc") {
                return new AiffFileReader(filepath);
            }
            
            // What the hell are you using?
            throw new Exception("Unsupported audio file format.");
        }

        // Method: Convert sample to standard 44.1kHz Stereo float array
        public static float[] GetStereoSamples(WaveStream waveStream) {
            ISampleProvider provider = waveStream.ToSampleProvider();
            
            // Resample audio to 44,100Hz
            if (provider.WaveFormat.SampleRate != 44100) {
                provider = new WdlResamplingSampleProvider(provider, 44100);
            }
            
            // Convert to stereo if multi-channel
            if (provider.WaveFormat.Channels > 2) {
                provider = provider.ToStereo();
            }
            
            // Pass to extractor for raw float values
            return GetSamples(provider);
        }

        // Method: Extract raw float from audio provider at 44.1kHz
        public static float[] GetSamples(ISampleProvider sampleProvider) {
            List<float> samples = new List<float>();
            float[] buffer = new float[44100];
            int n;
            
            // Ensure 44.1kHz sample rate
            if (sampleProvider.WaveFormat.SampleRate != 44100) {
                sampleProvider = new WdlResamplingSampleProvider(sampleProvider, 44100);
            }
            
            // Read data from stream into list
            while ((n = sampleProvider.Read(buffer, 0, buffer.Length)) > 0) {
                samples.AddRange(buffer.Take(n));
            }
            
            return samples.ToArray();
        }

        // Method: Extract raw float from audio provider at its native sample rate
        public static DiscreteSignal GetSignal(ISampleProvider sampleProvider) {
            List<float> samples = new List<float>();
            float[] buffer = new float[sampleProvider.WaveFormat.SampleRate];
            int n;
            
            // Read data from stream into list
            while ((n = sampleProvider.Read(buffer, 0, buffer.Length)) > 0) {
                samples.AddRange(buffer.Take(n));
            }
            
            // Pack into DS object: prepare for DSP tasks
            return new DiscreteSignal(sampleProvider.WaveFormat.SampleRate, samples.ToArray());
        }

        // Method: Downsample to 4kHz, extract absolute high/low volume points
        // Used to draw visual audio waveform on screen
        public static float[] BuildPeaks(WaveStream stream, IProgress<int> progress) {
            const double peaksRate = 4000;
            float[] peaks;
            
            int channels = stream.WaveFormat.Channels;
            double peaksSamples = (int)((double)stream.Length / stream.WaveFormat.BlockAlign / stream.WaveFormat.SampleRate * peaksRate);
            peaks = new float[(int)(peaksSamples + 1) * channels];
            double blocksPerPixel = stream.Length / stream.WaveFormat.BlockAlign / peaksSamples;

            var sampleProvider = stream.ToSampleProvider();
            float[] buffer = new float[128 * 1024];

            int readPos = 0;
            int peaksPos = 0;
            double bufferPos = 0;
            float lmax = 0, lmin = 0, rmax = 0, rmin = 0;
            int lastProgress = 0;
            int n;
            
            // For all channels, find max/min volume points
            while ((n = sampleProvider.Read(buffer, 0, buffer.Length)) != 0) {
                // n is the number of actual audio samples successfully read into buffer[]
                // readPos is used to check how many bytes were parsed so far
                readPos += n;
                
                for (int i = 0; i < n; i += channels) {
                    lmax = Math.Max(lmax, buffer[i]);
                    lmin = Math.Min(lmin, buffer[i]);
                    
                    // Assume at most stereo: if multiple-channel, the second one is 'right'
                    if (channels > 1) {
                        rmax = Math.Max(rmax, buffer[i + 1]);
                        rmin = Math.Min(rmin, buffer[i + 1]);
                    }
                    
                    // After storing peak data for one pixel, reset counters for next
                    if (i > bufferPos) {
                        // Negate peaks to flip waveform
                        lmax = -lmax; lmin = -lmin; rmax = -rmax; rmin = -rmin;
                        
                        // Squeeze max/min values into single float to save space in peaks[]
                        if (lmax == 0) {
                            // If there was no positive peak, use the inverted negative peak
                            peaks[peaksPos * channels] = lmin; 
                            } 
                        else if (lmin == 0) {
                            // If there was no negative peak, use the inverted positive peak
                            peaks[peaksPos * channels] = lmax; 
                        } 
                        else {
                            // If both exist, find the midpoint between the inverted max and min
                            peaks[peaksPos * channels] = (lmin + lmax) / 2; 
                        }
                        
                        // Similar logic for right channel
                        if (rmax == 0) {
                            peaks[peaksPos * channels + 1] = rmin; 
                            } 
                        else if (rmin == 0) {
                            peaks[peaksPos * channels + 1] = rmax; 
                        } 
                        else {
                            peaks[peaksPos * channels + 1] = (rmin + rmax) / 2; 
                        }
                        
                        // After storing value in peaks[], advance pointer and reset counters
                        peaksPos++;
                        lmax = lmin = rmax = rmin = 0;
                        bufferPos += blocksPerPixel * stream.WaveFormat.Channels;
                    }
                }
                
                // If for loop ended before hitting next pixel, bufferPos becomes negative, triggering cleanup
                bufferPos -= n;
                
                // Calculate current completion percentage
                int newProgress = (int)((double)readPos * sizeof(float) * 100 / stream.Length);
                
                // Only report if change in percentage
                if (newProgress != lastProgress) {
                    progress.Report(newProgress);
                    lastProgress = newProgress;
                }
            }
            
            return peaks;
        }

        // Check maximum amplitude in array, scale between [-1.0, 1.0]
        public static void CorrectSampleScale(float[] samples) {
            float max = samples.Max();
            float scale = 1;
            
            // From max, find appropriate scale
            if (max > Math.Pow(2, 23)) {
                scale = (float)Math.Pow(0.5, 31); // 32 bit
            } else if (max > 8) {
                scale = (float)Math.Pow(0.5, 15); // 16 bit
            }
            
            // Scale each note
            for (int i = 0; i < samples.Length; i++) {
                samples[i] *= scale;
            }
        }
    }
}
