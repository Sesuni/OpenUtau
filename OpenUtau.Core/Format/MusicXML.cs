using System;
using System.Collections.Generic;
using System.IO;
using System.Xml;
using System.Xml.Serialization;
using System.Linq;
using System.Text;
using OpenUtau.Core.Format.MusicXMLSchema;
using OpenUtau.Core.Ustx;
using Serilog;
using UtfUnknown;

namespace OpenUtau.Core.Format
{
    public static class MusicXML
    {
        static StartStopContinue? NoteTieStatus(MusicXMLSchema.Note note)
        {
            if (note.Tie == null) { return null; }
            
            bool hasStart = note.Tie.Any(t => t.Type == StartStop.Start);
            bool hasStop = note.Tie.Any(t => t.Type == StartStop.Stop);
    
            return (hasStart, hasStop) switch
            {
                (true, true)   => StartStopContinue.Continue,
                (true, false)  => StartStopContinue.Start,
                (false, true)  => StartStopContinue.Stop,
                (false, false) => null
            };
        }
        
        
        static StartStopContinue? NoteSlurStatus(MusicXMLSchema.Note note)
        {
            if (note.Notations == null) { return null; }
            
            var slurs = note.Notations.SelectMany(n => n.Slur);
            bool hasStart = slurs.Any(s => s.Type == StartStopContinue.Start || s.Type == StartStopContinue.Continue);
            bool hasStop  = slurs.Any(s => s.Type == StartStopContinue.Stop  || s.Type == StartStopContinue.Continue);
            
            return (hasStart, hasStop) switch
            {
                (true, true)   => StartStopContinue.Continue,
                (true, false)  => StartStopContinue.Start,
                (false, true)  => StartStopContinue.Stop,
                (false, false) => null
            };
        }


        static Syllabic SyllabicStatus(MusicXMLSchema.Lyric lyric)
        {
            if (lyric.Syllabic == null || lyric.Syllabic.Count == 0)
                return Syllabic.Single;
            
            return lyric.Syllabic[0];
        }


        static public UProject LoadProject(string file) 
        {
            UProject uproject = new UProject();
            Ustx.AddDefaultExpressions(uproject);      
            uproject.tracks.Clear();
            uproject.parts.Clear();
            uproject.tempos.Clear();
            uproject.timeSignatures.Clear();

            var score = ReadXMLScore(file);
            foreach (var part in score.Part) {
                var utrack = new UTrack(uproject);
                utrack.TrackNo = uproject.tracks.Count;
                uproject.tracks.Add(utrack);
                
                var upart = new UVoicePart();
                upart.trackNo = utrack.TrackNo;
                uproject.parts.Add(upart);

                int divisions = (int)part.Measure[0].Attributes[0].Divisions;
                int prevPosTick = 0;
                int currPosTick = 0;

                var tiedNotes = new Dictionary<int, UNote>();
                UNote? incompletedLyricNote = null;
                
                foreach (var measure in part.Measure) {
                    // BPM
                    double? bpm;
                    if ((bpm = MeasureBPM(measure)).HasValue) {
                        uproject.tempos.Add(new UTempo(currPosTick, bpm.Value));
                        Log.Information($"Measure {measure.Number} BPM: {bpm.ToString()}");
                    }

                    // Time Signature
                    foreach (var time in measure.Attributes.SelectMany(a => a.Time)) {
                        if (time.Beats.Count > 0 && time.BeatType.Count > 0) {
                            uproject.timeSignatures.Add(new UTimeSignature {
                                barPosition = currPosTick,
                                beatPerBar = Int32.Parse(time.Beats[0]),
                                beatUnit = Int32.Parse(time.BeatType[0])
                            });
                            Log.Information($"Measure {measure.Number} Time Signature: {time.Beats[0]}/{time.BeatType[0]}");
                        }
                    }

                    // Note
                    foreach (var element in measure.Content) {
                        switch (element) {
                            case Note note:
                                int durTick = (int)note.Duration * uproject.resolution / divisions;
                                int posTick = note.Chord == null ? currPosTick : prevPosTick;

                                if (note.Rest != null) {
                                    prevPosTick = posTick;
                                    currPosTick = posTick + durTick;
                                    break;
                                } 
                                
                                var pitch = note.Pitch.Step.ToString() + note.Pitch.Octave.ToString();
                                int tone = MusicMath.NameToTone(pitch) + (int)note.Pitch.Alter;
                                
                                var tieStatus = NoteTieStatus(note);
                                var slurStatus = NoteSlurStatus(note);
                                var syllabicStatus = note.Lyric.Count > 0 ? SyllabicStatus(note.Lyric[0]) : Syllabic.Single;
                
                                UNote NewNote() {
                                    var unote = uproject.CreateNote(tone, posTick, durTick);
                                    upart.notes.Add(unote);
                                    
                                    if (note.Lyric.Count == 0) {
                                        if (slurStatus is StartStopContinue.Continue or StartStopContinue.Stop)
                                            unote.lyric = "+~";
                                        return unote;
                                    }
                                    
                                    string text = note.Lyric[0].Text[0].Value;
                                    if ((syllabicStatus == Syllabic.Middle || syllabicStatus == Syllabic.End) && incompletedLyricNote != null) {
                                        incompletedLyricNote.lyric += text;
                                        unote.lyric = "+";
                                    }
                                    else {
                                        unote.lyric = text;
                                        incompletedLyricNote = unote;
                                    }
                                        
                                    if (syllabicStatus == Syllabic.Single || syllabicStatus == Syllabic.End) {
                                        incompletedLyricNote = null;
                                    }
                                                 
                                    return unote;
                                } // NewNote;
                                
                                
                                bool isTied = tiedNotes.TryGetValue(tone, out var existingNote);
                                switch (tieStatus) {
                                    case StartStopContinue.Start:
                                        tiedNotes[tone] = NewNote();
                                        break;
                                    
                                    case StartStopContinue.Continue when isTied:
                                    case StartStopContinue.Stop when isTied:
                                        existingNote!.duration += durTick;
                                        if (tieStatus == StartStopContinue.Stop)
                                            tiedNotes.Remove(tone);
                                        break;
                                    
                                    case StartStopContinue.Continue:
                                        tiedNotes[tone] = NewNote();
                                        break;
                                        
                                    default:
                                        NewNote();
                                        break;
                                }
                                
                                prevPosTick = posTick;
                                currPosTick = posTick + durTick;
                                break;
                                
                            case MusicXMLSchema.Backup backup:
                                currPosTick -= (int)backup.Duration * uproject.resolution / divisions;
                                prevPosTick = currPosTick;
                                break;
                                
                            case MusicXMLSchema.Forward forward: 
                                currPosTick += (int)forward.Duration * uproject.resolution / divisions;
                                prevPosTick = currPosTick;
                                break;
                        }
                    }
                }
                upart.Duration = upart.GetMinDurTick(uproject);
            }
            
            if (uproject.tempos.Count == 0) {
                uproject.tempos.Add(new UTempo(0, 120));
            }
            
            if (uproject.tempos[0].position > 0) {
                uproject.tempos[0].position = 0;
            }
            
            uproject.AfterLoad();
            uproject.ValidateFull();
            return uproject;
        }


        static public System.Text.Encoding DetectXMLEncoding(string file)
        {
            var detected = CharsetDetector.DetectFromFile(file).Detected;
            return detected?.Confidence > 0.5 && detected.Encoding != null ? detected.Encoding : System.Text.Encoding.UTF8;
        }

        static public double? MeasureBPM(MusicXMLSchema.ScorePartwisePartMeasure measure)
            => (double?)measure.Directions?
                .FirstOrDefault(direction => direction.Sound != null)?
                .Sound.Tempo;

        static public MusicXMLSchema.ScorePartwise ReadXMLScore(string xmlFile)
        {
            var encoding = DetectXMLEncoding(xmlFile);
            Log.Information($"MusicXML Character Encoding: {encoding}");

            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Parse,
                MaxCharactersFromEntities = 1024
            };

            using var fs = new FileStream(xmlFile, FileMode.Open, FileAccess.Read);
            using var streamReader = new StreamReader(fs, encoding);
            using var xmlReader = XmlReader.Create(streamReader, settings);
            
            XmlSerializer s = new(typeof(MusicXMLSchema.ScorePartwise));
            return s.Deserialize(xmlReader) as MusicXMLSchema.ScorePartwise;
        }
        
    } // class MusicXML
} // namespace .Core.Format
