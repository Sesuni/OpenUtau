using System.Collections.Generic;
using System.IO;
using System.Linq;
using OpenUtau.Classic;
using OpenUtau.Core.Ustx;

namespace OpenUtau.Core.Format {
    public enum ProjectFormats { Unknown, Vsq3, Vsq4, Ust, Ustx, Midi, Ufdata, Musicxml };

    public static class Formats {
    
        public static ProjectFormats DetectProjectFormat(string file) {
            // Read the first 10 lines, search for format signatures 
            string contents = string.Join("\n", File.ReadLines(file).Take(10));
   
            // Return appropriate format enumerators according to signature         
            if (contents.Contains("[#SETTING]")) return ProjectFormats.Ust;
            if (contents.Contains("\"ustxVersion\":") || contents.Contains("ustx_version:")) return ProjectFormats.Ustx;
            if (contents.Contains(VSQx.vsq3NameSpace)) return ProjectFormats.Vsq3;
            if (contents.Contains(VSQx.vsq4NameSpace)) return ProjectFormats.Vsq4;
            if (contents.Contains("MThd")) return ProjectFormats.Midi;
            if (contents.Contains("\"formatVersion\":")) return ProjectFormats.Ufdata;
            if (contents.Contains("score-partwise")) return ProjectFormats.Musicxml;
            
            return ProjectFormats.Unknown;
        }


        // Read project from files to a new UProject object, used by LoadProject and ImportTracks.
        public static UProject? ReadProject(string[] files){
            if (files == null || files.Length < 1) {return null;}
            
            // Send file to appropriate parser according to detected format
            return DetectProjectFormat(files[0]) switch {
                ProjectFormats.Ustx     => Ustx.Load(files[0]),
                ProjectFormats.Vsq3 or ProjectFormats.Vsq4     => VSQx.Load(files[0]),
                ProjectFormats.Ust      => Ust.Load(files),
                ProjectFormats.Midi     => MidiWriter.LoadProject(files[0]),
                ProjectFormats.Ufdata   => Ufdata.Load(files[0]),
                ProjectFormats.Musicxml => MusicXML.LoadProject(files[0]),
                _                       => throw new FileFormatException("Unknown file format")
            };
        }


        // Load project from files.
        public static void LoadProject(string[] files) {
            UProject project = ReadProject(files);
            // Display it as new active project
            if (project != null) {
                DocManager.Inst.ExecuteCmd(new LoadProjectNotification(project));
            }
        }


        // Read multiple projects for importing tracks
        public static UProject[] ReadProjects(string[]? files){
            // Compiler turns [] into return type UProject[]
            if (files == null || files.Length < 1) {return [];}
            
            return files
                .Select(f => ReadProject([f])) // Each file into UProject object
                .OfType<UProject>()            // Discard null items failed to load
                .ToArray();                    // All valid item into UProject[] array
        }


        // Load project from backup file.
        /// <param name="files">Names of the files to be loaded</param>
        public static void RecoveryProject(string[] files) {
    
            // External Guard clause   
            var project = ReadProject(files);
            if (project is null) 
                return;
        
            // Derive original path safely        
            string originalPath = (project.FilePath ?? string.Empty)
                .Replace("-autosave.ustx", ".ustx")
                .Replace("-backup.ustx", ".ustx");
            
            // Update attributes
            bool exists = File.Exists(originalPath);
            project.FilePath = exists ? originalPath : string.Empty;
            if (!exists) 
                project.Saved = false;
                
            DocManager.Inst.ExecuteCmd(new LoadProjectNotification(project));
        }


        // Import tracks from files to the current existing editing project.
        public static void ImportTracks(UProject project, UProject[] loadedProjects, bool importTempo = true) {
        
            // External Guard clause
            if (loadedProjects == null || loadedProjects.Length < 1) {return;}
            
            int initialTracks = project.tracks.Count;
            int initialParts = project.parts.Count;
            
            // Import project contents
            foreach (var loaded in loadedProjects) {
                int trackCount = project.tracks.Count;
                
                foreach (var (abbr, descriptor) in loaded.expressions) {
                    project.expressions.TryAdd(abbr, descriptor);
                }
                
                foreach (var track in loaded.tracks) {
                    track.TrackNo = project.tracks.Count;
                    project.tracks.Add(track);
                }
                
                foreach (var part in loaded.parts) {
                    project.parts.Add(part);
                    part.trackNo += trackCount;
                }
            }
            
            // Import tempo data
            if (importTempo) {
                var loaded = loadedProjects[0];
                project.timeSignatures.Clear();
                project.timeSignatures.AddRange(loaded.timeSignatures);
                project.tempos.Clear();
                project.tempos.AddRange(loaded.tempos);
            }
            
            // Post-processing
            foreach (var track in project.tracks.Skip(initialTracks)) {
                track.AfterLoad(project);
            }
            foreach (var part in project.parts.Skip(initialParts)) {
                part.AfterLoad(project, project.tracks[part.trackNo]);
            }
            
            project.ValidateFull();
            DocManager.Inst.ExecuteCmd(new LoadProjectNotification(project));
        }


        // Import tracks from files to the current existing editing project.
        public static void ImportTracks(UProject project, string[] files, bool importTempo = true) {
            ImportTracks(project, ReadProjects(files), importTempo);
        }
        
    } // class Formats
} // namespace Core.Format
