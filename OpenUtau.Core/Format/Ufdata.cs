using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using OpenUtau.Core.Ustx;
//reference: https://github.com/sdercolin/utaformatix-data/blob/main/lib/csharp/UtaFormatix.Data

namespace OpenUtau.Core.Format
{
    public readonly struct UfNote
    {
        public int key { get; init; }
        public int tickOn { get; init; }
        public int tickOff { get; init; }
        public string lyric { get; init; }
        public string? phoneme { get; init; }
    }

    public readonly struct UfPitch
    {
        public int[] ticks { get; init; }
        public double?[] values { get; init; }
        public bool isAbsolute { get; init; }
    }

    public readonly struct UfTempo
    {
        public int tickPosition { get; init; }
        public double bpm { get; init; }
    }

    public readonly struct UfTimeSignature
    {
        public int measurePosition { get; init; }
        public int numerator { get; init; }
        public int denominator { get; init; }
    }

    public readonly struct UfTrack
    {
        public string name { get; init; }
        public UfNote[] notes { get; init; }
        public UfPitch? pitch { get; init; }
    }

    public readonly struct UfProject
    {
        public string name { get; init; }
        public UfTrack[] tracks { get; init; }
        public UfTimeSignature[]? timeSignatures { get; init; }
        public UfTempo[] tempos { get; init; }
        public int measurePrefix { get; init; }
    }

    public readonly struct UfFile
    {
        public UfProject project { get; init; }
        public int formatVersion { get; init; }
    }


    public static class Ufdata
    {
        private static UVoicePart ParsePart(UfTrack ufTrack, UProject project)
        {
            var part = new UVoicePart
            {
                name = ufTrack.name,
                position = 0
            };
            
            foreach (var ufNote in ufTrack.notes)
            {
                var note = project.CreateNote(
                    ufNote.key,
                    ufNote.tickOn,
                    ufNote.tickOff - ufNote.tickOn
                );
                
                note.lyric = ufNote.lyric == "-" ? "+~" : ufNote.lyric;
                part.notes.Add(note);
            }
            
            if (ufTrack.notes.Length > 0)
            {
                part.Duration = ufTrack.notes[^1].tickOff;
            }
                
            return part;     
        } // Ufdata.ParsePart


        public static UProject Load(string file)
        {
            var project = new UProject { FilePath = file };
            Ustx.AddDefaultExpressions(project);

            var jsonText = File.ReadAllText(file, Encoding.UTF8);
            var ufFile = JsonConvert.DeserializeObject<UfFile>(jsonText);
            var ufProject = ufFile.project;
            
            project.tempos=ufProject.tempos
                ?.Select(t => new UTempo(t.tickPosition, t.bpm))
                .ToList() ?? new();
                
            project.timeSignatures=ufProject.timeSignatures
                ?.Select(t => new UTimeSignature(t.measurePosition, t.numerator, t.denominator))
                .ToList() ?? new();
            
            var validTracks = ufProject.tracks?.Where(tr => tr.notes?.Length > 0) ?? Enumerable.Empty<UfTrack>();
            
            foreach (var tr in validTracks) 
            {
                var part = ParsePart(tr, project);
                var track = new UTrack(project) { TrackNo = project.tracks.Count };
                
                part.trackNo = track.TrackNo;
                part.AfterLoad(project, track);
                project.tracks.Add(track);
                project.parts.Add(part);
            }
            
            project.ValidateFull();
            return project;
        } // Ufdata.Load
        
    } // class Ufdata
} // namespace OpenUtau.Core.Format
