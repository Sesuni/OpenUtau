using Melanchall.DryWetMidi.Common;
using System.Collections.Generic;
using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Interaction;
using OpenUtau.Core.Ustx;
using OpenUtau.Core.Util;
using System.Text;
using System.IO;
using UtfUnknown;
using System.Linq;

namespace OpenUtau.Core.Format {

    // Detect uncommon encodings other than UTF-8
    public class EncodingDetector {
        private readonly MemoryStream stream = new MemoryStream();

        public void ReadFile(string file) {
            // Clean in case of reuse
            stream.Position = 0;
            stream.SetLength(0);

            var settings = MidiWriter.BaseReadingSettings();

            // Intercept raw byte array as MIDI is parsed
            settings.DecodeTextCallback = (bytes, _) => {
                stream.Write(bytes, 0, bytes.Length);
                return string.Empty;            
            };

            MidiFile.Read(file, settings);
        }


        public Encoding Detect() {
            stream.Seek(0, SeekOrigin.Begin);
            var result = CharsetDetector.DetectFromStream(stream);
            // Analyze collected bytes to detect encoding
            return result.Detected?.Confidence > 0.5 ? result.Detected.Encoding : null;
        }

    } // class EncodingDetector



    public static class MidiWriter {

        // Private helper for duplicate logics
        private static (MidiFile midi, short ppq) ReadMidi(string file) {
            var detector = new EncodingDetector();
            detector.ReadFile(file);

            var settings = BaseReadingSettings();
            settings.TextEncoding = detector.Detect() ?? Encoding.UTF8;    

            var midi = MidiFile.Read(file, settings);
            TicksPerQuarterNoteTimeDivision timeDivision = midi.TimeDivision as TicksPerQuarterNoteTimeDivision;
            
            return (midi, timeDivision?.TicksPerQuarterNote ?? 480); 
        } // ReadMidi()

        // Create a blank new project and import data from midi files, including tempo
        static public UProject LoadProject(string file) {
            var (midi, ppq) = ReadMidi(file);

            var project = new UProject { FilePath = file };
            Ustx.AddDefaultExpressions(project);

            var tempoMap = midi.GetTempoMap();
            project.timeSignatures = ParseTimeSignatures(tempoMap, ppq);
            project.tempos = ParseTempos(tempoMap, ppq);
            project.tracks = new List<UTrack>();

            // Convert MIDI tracks into OpenUtau UTrack and UPart
            foreach (var part in ParseParts(midi, ppq, project)) {
                var track = new UTrack(project) {
                    TrackNo = project.tracks.Count
                };

                part.trackNo = track.TrackNo;
                if(part.name != "New Part"){
                    track.TrackName = part.name;
                }

                part.AfterLoad(project, track);
                project.tracks.Add(track);
                project.parts.Add(part);
            }

            project.ValidateFull();
            return project;
        } // LoadProject()


        public static ReadingSettings BaseReadingSettings() => new() {
            InvalidChannelEventParameterValuePolicy = InvalidChannelEventParameterValuePolicy.ReadValid,
            InvalidChunkSizePolicy = InvalidChunkSizePolicy.Ignore,
            InvalidMetaEventParameterValuePolicy = InvalidMetaEventParameterValuePolicy.SnapToLimits,
            MissedEndOfTrackPolicy = MissedEndOfTrackPolicy.Ignore,
            NoHeaderChunkPolicy = NoHeaderChunkPolicy.Ignore,
            NotEnoughBytesPolicy = NotEnoughBytesPolicy.Ignore,
            UnexpectedTrackChunksCountPolicy = UnexpectedTrackChunksCountPolicy.Ignore,
            UnknownChannelEventPolicy = UnknownChannelEventPolicy.SkipStatusByteAndOneDataByte,
            UnknownChunkIdPolicy = UnknownChunkIdPolicy.ReadAsUnknownChunk,
            UnknownFileFormatPolicy = UnknownFileFormatPolicy.Ignore
        };
        
        
        // Import tracks to an existing project
        static public List<UVoicePart> Load(string file, UProject project) {
            var (midi, ppq) = ReadMidi(file);
            return ParseParts(midi, ppq, project);
        }


        public static List<UTempo> ParseTempos(TempoMap tempoMap, short ppq) {
            var tempos = new List<UTempo>();
            var changes = tempoMap.GetTempoChanges().ToList();

            // Fallback to baseline 120 BPM
            if (!changes.Any() || changes[0].Time > 0) {
                tempos.Add(new UTempo { position = 0, bpm = 120.0 });    
            }

            foreach (var change in changes) {
                tempos.Add(new UTempo {
                    position = (int)(change.Time * 480 / ppq),
                    bpm = 60.0 / change.Value.MicrosecondsPerQuarterNote * 1000000.0
                });
            }

            return tempos;
        } // ParseTempos()


        public static List<UTimeSignature> ParseTimeSignatures(TempoMap tempoMap, short ppq) {
            var timeSignatures = new List<UTimeSignature>();
            var current = new UTimeSignature { barPosition = 0, beatPerBar = 4, beatUnit = 4 };
            var changes = tempoMap.GetTimeSignatureChanges().ToList();

            if (!changes.Any() || changes[0].Time > 0) {
                timeSignatures.Add(current);
            }

            int lastTimeInQuarters = 0;
            foreach (var change in changes) {
                int currentTimeInQuarters = (int)change.Time / ppq;
                int elapsedQuarters = currentTimeInQuarters - lastTimeInQuarters;
                int elapsedBars = elapsedQuarters * current.beatUnit / (4 * current.beatPerBar);

                current = new UTimeSignature {
                   barPosition = current.barPosition + elapsedBars,
                   beatPerBar = change.Value.Numerator,
                   beatUnit = change.Value.Denominator
                };

                timeSignatures.Add(current);
                lastTimeInQuarters = currentTimeInQuarters;
            }
            
            return timeSignatures;
        } // ParseTimeSignatures()


        static List<UVoicePart> ParseParts(MidiFile midi, short ppq, UProject project) {
            var resultParts = new List<UVoicePart>();
            var presets = NotePresets.Default;            

            foreach (TrackChunk trackChunk in midi.GetTrackChunks()) {
                var midiNotes = trackChunk.GetNotes().ToList();
                if (midiNotes.Count == 0) {continue;}

                var part = new UVoicePart();
                using var objectsManager = new TimedObjectsManager<TimedEvent>(trackChunk.Events);
                var events = objectsManager.Objects;

                // Extract lyrics dictionary
                var lyrics = events
                    .Select(e => (e.Time, Ev: e.Event as LyricEvent))
                    .Where(x => x.Ev != null)
                    .GroupBy(x => x.Time)
                    .ToDictionary(g => g.Key, g => g.First().Ev!.Text);


                var trackName = events
                    .Select(e => e.Event as SequenceTrackNameEvent)
                    .FirstOrDefault(e => e != null)?.Text;

                if (trackName != null) {
                    part.name = trackName;
                }


                foreach (var midiNote in midiNotes) {
                    int pos = (int)(midiNote.Time * project.resolution / ppq);
                    int len = (int)(midiNote.Length * project.resolution / ppq);
                    var note = project.CreateNote(midiNote.NoteNumber, pos, len);

                    string rawLyric = lyrics.GetValueOrDefault(midiNote.Time) ?? presets.DefaultLyric;
                    note.lyric = (rawLyric == "-") ? "+~" : rawLyric;


                    if (presets.AutoVibratoToggle && note.duration >= presets.AutoVibratoNoteDuration) {
                        note.vibrato.length = presets.DefaultVibrato.VibratoLength;
                    }
                    
                    part.notes.Add(note);
                }
                
                resultParts.Add(part);
            }

            return resultParts;
        } // ParseParts()


        static public void Save(string filePath, UProject project) {
            var midiFile = new MidiFile {
                TimeDivision = new TicksPerQuarterNoteTimeDivision((short)project.resolution)
            };

            // Build TempoMap and Time Signatures
            midiFile.Chunks.Add(new TrackChunk());
            using (var tempoMapManager = midiFile.ManageTempoMap()) {
                var lastSig = new UTimeSignature { barPosition = 0, beatPerBar = 4, beatUnit = 4 };
                int lastTime = 0;

                foreach (var sig in project.timeSignatures) {
                    int time = lastTime + (sig.barPosition - lastSig.barPosition) * lastSig.beatPerBar * 4 / lastSig.beatUnit * project.resolution;
                    tempoMapManager.SetTimeSignature(time, new TimeSignature(sig.beatPerBar, sig.beatUnit));
                    lastSig = sig;
                    lastTime = time;
                }

                foreach(var tempo in project.tempos){
                    tempoMapManager.SetTempo(tempo.position, Tempo.FromBeatsPerMinute(tempo.bpm));
                }
            }

            // Init Track Chunks
            var trackChunks = project.tracks.Select(track => {
                var chunk = new TrackChunk();
                using var manager = new TimedObjectsManager<TimedEvent>(chunk.Events);
                manager.Objects.Add(new TimedEvent(new SequenceTrackNameEvent(track.TrackName), 0));
                return chunk;
            }).ToList();

            // Export Voice notes and Lyrics
            const SevenBitNumber defaultVelocity = (SevenBitNumber)45;

            foreach (var voicePart in project.parts.OfType<UVoicePart>()) {
                if (voicePart.trackNo < 0 || voicePart.trackNo >= trackChunks.Count) {continue;}

                var chunk = trackChunks[voicePart.trackNo];
                using (var manager = new TimedObjectsManager<TimedEvent>(chunk.Events)) {
                  var events = manager.Objects;
                    int offset = voicePart.position;

                   foreach (var note in voicePart.notes) {
                        // Ignore notes with pitch out of midi range
                        if(note.tone is < 0 or > 127) {continue;}
                     
                        var tone = (SevenBitNumber)note.tone;
                        string lyric = (note.lyric is "+~" or "+*") ? "-" : note.lyric;
                        long start = note.position + offset;

                        events.Add(new TimedEvent(new LyricEvent(lyric), start));
                        events.Add(new TimedEvent(new NoteOnEvent(tone, defaultVelocity), start));
                        events.Add(new TimedEvent(new NoteOffEvent(tone, defaultVelocity), start + note.duration));
                    }
                }
            }
            
            midiFile.Chunks.AddRange(trackChunks);
            midiFile.Write(filePath, overwrite: true, settings: new WritingSettings {
                TextEncoding = Encoding.UTF8,
            });
        } // Save()

    } // class MidiWriter
} // namespace Core.Format
