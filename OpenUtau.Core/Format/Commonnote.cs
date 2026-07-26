using Newtonsoft.Json;
using OpenUtau.Core.Ustx;
using OpenUtau.Core.Util;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using TextCopy;

//Commonnote format definition: https://github.com/ExpressiveLabs/commonnote
namespace OpenUtau.Core.Format {

    // Universal, JSON-friendly note-data format
    public struct CommonnoteNote {
        public long start;
        public long length;
        public string label;
        public int pitch;
    }
    
    public struct CommonnoteHeader {
        public long resolution;
        public string origin;
    }
    
    public struct CommonnoteData {
        public string identifier;
        public CommonnoteHeader header;
        public List<CommonnoteNote> notes;
    }


    public static class Commonnote {
        // Convert OpenUtau note to JSON-friendly format
        static CommonnoteNote DumpNote(UNote uNote) {
            return new CommonnoteNote {
                start = uNote.position,
                length = uNote.duration,
                label = uNote.lyric,
                pitch = uNote.tone,
            };
        }

        // Convert CommonNote to OpenUtau note
        static UNote LoadNote(CommonnoteNote cNote, int resolution, UProject project) {
            // Scale timing to match current project's resolution
            int position = (int)(cNote.start * project.resolution / resolution);
            int duration = (int)((cNote.start + cNote.length) * project.resolution / resolution - position);

            var note = project.CreateNote(cNote.pitch, position, duration);
            // Fall back to default lyric if cNote.label is faulty
            note.lyric = string.IsNullOrEmpty(cNote.label) ? NotePresets.Default.DefaultLyric : cNote.label;            
            return note;
        }


        // Convert OpenUtau note to raw JSON string
        public static string Dumps(List<UNote> uNotes, UProject project) {
            var data = new CommonnoteData {
                // Fill in metadata
                identifier = "commonnote",
                header = new CommonnoteHeader {
                    resolution = project.resolution,
                    origin = "openutau",
                },
                // Run through above 'DumpNote' to convert to JSON-friendly format
                notes = uNotes.Select(DumpNote).ToList(),
            };
            
            // Return final JSON string
            return JsonConvert.SerializeObject(data);
        }


        // Reconstruct OpenUtau note from raw JSON string
        public static List<UNote> Loads(string text, UProject project) {
            // Convert JSON into CommonNote object
            var data = JsonConvert.DeserializeObject<CommonnoteData>(text);
            
            // If not commonnote object, raise error
            if (data.identifier != "commonnote") {
                Log.Error($"Clipboard is missing commonnote header");
                return null;
            }
            
            int resolution = (int)(data.header.resolution > 0 ? data.header.resolution : project.resolution);
            return data.notes.Select(n => LoadNote(n, resolution, project)).ToList();
        }

        // Helper function for user actions (Ctrl + C)
        public static void CopyToClipboard(List<UNote> uNotes, UProject project) {
            var text = Dumps(uNotes, project);
            ClipboardService.SetText(text);
        }

        // Helper function for user actions (Ctrl + V)
        public static List<UNote>? LoadFromClipboard(UProject project) {
            var text = ClipboardService.GetText();
            if (String.IsNullOrEmpty(text)) {
                return null;
            }
            return Loads(text, project);
        }

    } // class Commonnote
} // namespace OpenUtau.Core.Format
