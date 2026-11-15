using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using YARG.Core.Audio;
using YARG.Core.IO;
using YARG.Core.Logging;

namespace YARG.Core.Song
{
    /// <summary>
    /// Reads stems made by an outside tool (such as YARG Demucs) for songs that only ship a full mix.
    /// YARG never writes these files.
    ///
    /// Layout, under each song folder the user added to YARG (the library root):
    ///   {root}/demucs/.yarg-stems                marks the folder so the song scan skips it
    ///   {root}/demucs/{id}/.yarg-stems           one folder per song, with song/vocals/bass/drums audio
    ///   {root}/demucs/missing-stems.txt          Rock Band short names whose drum, bass, and vocal
    ///                                            channels are silent (one per line)
    /// {id} is the Rock Band short name for CON songs, or <see cref="SngId"/> for .sng files.
    /// Unpacked song folders get their stems in place, with a .yarg-stems file in the song folder.
    /// The older marker name .yarg-demucs is read too.
    /// </summary>
    internal static class CustomStems
    {
        public const long PlaceholderMaxBytes = 4096;
        public const string FolderName = "demucs";
        public const string MissingListName = "missing-stems.txt";
        private static readonly string[] MarkerFileNames = { ".yarg-stems", ".yarg-demucs" };

        private static readonly object MissingLock = new();
        private static readonly Dictionary<string, HashSet<string>> MissingByRoot = new(StringComparer.OrdinalIgnoreCase);
        private static string[] _libraryRoots = Array.Empty<string>();

        public static void SetLibraryRoots(IEnumerable<string> roots)
        {
            var list = new List<string>();
            if (roots != null)
            {
                foreach (string root in roots)
                {
                    if (string.IsNullOrWhiteSpace(root))
                    {
                        continue;
                    }

                    try
                    {
                        list.Add(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                    }
                    catch (Exception ex)
                    {
                        YargLogger.LogException(ex, "Failed to read a song folder for custom stems");
                    }
                }
            }

            _libraryRoots = list.ToArray();
            lock (MissingLock)
            {
                MissingByRoot.Clear();
            }
        }

        public static bool HasMarker(string? directory)
        {
            if (string.IsNullOrEmpty(directory))
            {
                return false;
            }

            foreach (var name in MarkerFileNames)
            {
                if (File.Exists(Path.Combine(directory, name)))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>The stem folder for this song id, whether or not it exists.</summary>
        public static string GetStemDirectory(string songLocation, string id)
        {
            return Path.Combine(FindLibraryRoot(songLocation), FolderName, SanitizeId(id));
        }

        /// <summary>
        /// Id of a .sng file: "sng_" plus its path inside the library root, with folder separators
        /// turned into "__". Tools writing stems must build the same id.
        /// </summary>
        public static string SngId(string sngLocation)
        {
            string root = FindLibraryRoot(sngLocation);
            string relative;
            try
            {
                relative = Path.GetRelativePath(root, Path.GetFullPath(sngLocation));
            }
            catch (Exception)
            {
                relative = Path.GetFileName(sngLocation);
            }

            return "sng_" + relative.Replace(Path.DirectorySeparatorChar.ToString(), "__")
                .Replace(Path.AltDirectorySeparatorChar.ToString(), "__");
        }

        /// <summary>
        /// True when the stem tool listed this Rock Band song as having silent drum, bass, and vocal channels.
        /// </summary>
        public static bool IsListedMissing(string songLocation, string shortName)
        {
            if (string.IsNullOrWhiteSpace(shortName))
            {
                return false;
            }

            string root = FindLibraryRoot(songLocation);
            HashSet<string>? names;
            lock (MissingLock)
            {
                if (!MissingByRoot.TryGetValue(root, out names))
                {
                    names = LoadMissingList(root);
                    MissingByRoot[root] = names;
                }
            }

            return names.Contains(shortName.Trim());
        }

        public static bool FolderMissesCoreStems(string directory)
        {
            if (!Directory.Exists(directory))
            {
                return true;
            }

            bool vocals = false;
            bool bass = false;
            bool drums = false;
            try
            {
                foreach (var file in Directory.EnumerateFiles(directory))
                {
                    var info = new FileInfo(file);
                    if (info.Length <= PlaceholderMaxBytes || !IsSupportedAudio(info.Name))
                    {
                        continue;
                    }

                    NoteStem(Path.GetFileNameWithoutExtension(info.Name), ref vocals, ref bass, ref drums);
                    if (vocals && bass && drums)
                    {
                        return false;
                    }
                }
            }
            catch (Exception ex)
            {
                YargLogger.LogException(ex, "Failed to check stem files");
                return false;
            }

            return !vocals && !bass && !drums;
        }

        public static bool ArchiveMissesCoreStems(Dictionary<string, SngFileListing> listings)
        {
            bool vocals = false;
            bool bass = false;
            bool drums = false;
            foreach (var pair in listings)
            {
                if (pair.Value.Length <= PlaceholderMaxBytes)
                {
                    continue;
                }

                NoteStem(Path.GetFileNameWithoutExtension(pair.Key), ref vocals, ref bass, ref drums);
                if (vocals && bass && drums)
                {
                    return false;
                }
            }

            return !vocals && !bass && !drums;
        }

        /// <summary>
        /// Loads the stem folder into a mixer. Returns false when the folder has no stems, so the
        /// caller loads the song's own audio. Any audio format YARG plays is accepted.
        /// </summary>
        public static bool TryLoadMixer(string directory, string name, float speed, double volume, bool clampStemVolume,
            SongStem[] ignoreStems, out StemMixer? mixer)
        {
            mixer = null;
            if (!HasMarker(directory))
            {
                return false;
            }

            mixer = GlobalAudioHandler.CreateMixer(name, speed, volume, clampStemVolume, normalize: true);
            if (mixer == null)
            {
                YargLogger.LogError("Failed to create mixer for custom stems");
                return true;
            }

            TryAdd(mixer, directory, "song", SongStem.Song, ignoreStems);
            TryAdd(mixer, directory, "vocals", SongStem.Vocals, ignoreStems);
            TryAdd(mixer, directory, "bass", SongStem.Bass, ignoreStems);
            TryAdd(mixer, directory, "drums", SongStem.Drums1, ignoreStems);

            if (mixer.Channels.Count == 0)
            {
                YargLogger.LogError("Custom stem files could not be loaded");
                mixer.Dispose();
                mixer = null;
            }

            return true;
        }

        private static void TryAdd(StemMixer mixer, string directory, string stemName, SongStem stem, SongStem[] ignoreStems)
        {
            if (ignoreStems != null && Array.IndexOf(ignoreStems, stem) >= 0)
            {
                return;
            }

            foreach (var format in IniAudio.SupportedFormats)
            {
                string path = Path.Combine(directory, stemName + format);
                if (!File.Exists(path))
                {
                    continue;
                }

                var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1);
                if (!mixer.AddChannel(stream, stem))
                {
                    stream.Dispose();
                    YargLogger.LogFormatError("Failed to load custom stem {0}", path);
                }

                return;
            }
        }

        private static HashSet<string> LoadMissingList(string root)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string path = Path.Combine(root, FolderName, MissingListName);
            if (!File.Exists(path))
            {
                return names;
            }

            try
            {
                foreach (var line in File.ReadLines(path, Encoding.UTF8))
                {
                    string name = line.Trim();
                    if (name.Length > 0 && name[0] != '#')
                    {
                        names.Add(name);
                    }
                }
            }
            catch (Exception ex)
            {
                YargLogger.LogException(ex, "Failed to read " + path);
            }

            return names;
        }

        private static string FindLibraryRoot(string songLocation)
        {
            string full;
            try
            {
                full = Path.GetFullPath(songLocation);
            }
            catch (Exception)
            {
                full = songLocation ?? string.Empty;
            }

            string[] roots = _libraryRoots;
            string? best = null;
            int bestLength = -1;
            for (int i = 0; i < roots.Length; i++)
            {
                string root = roots[i];
                if (root.Length > bestLength && IsUnderRoot(full, root))
                {
                    best = root;
                    bestLength = root.Length;
                }
            }

            if (best != null)
            {
                return best;
            }

            if (Directory.Exists(full))
            {
                return full;
            }

            string? parent = Path.GetDirectoryName(full);
            return string.IsNullOrEmpty(parent) ? full : parent!;
        }

        private static bool IsUnderRoot(string fullPath, string root)
        {
            if (fullPath.Equals(root, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return fullPath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }

        private static string SanitizeId(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                return "song";
            }

            char[] invalid = Path.GetInvalidFileNameChars();
            var builder = new StringBuilder(id.Length);
            for (int i = 0; i < id.Length; i++)
            {
                char c = id[i];
                builder.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
            }

            string name = builder.ToString().Trim().Trim('.');
            return name.Length == 0 ? "song" : name;
        }

        private static bool IsSupportedAudio(string fileName)
        {
            foreach (var format in IniAudio.SupportedFormats)
            {
                if (fileName.EndsWith(format, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static void NoteStem(string stem, ref bool vocals, ref bool bass, ref bool drums)
        {
            if (stem.Equals("vocal", StringComparison.OrdinalIgnoreCase)
                || stem.Equals("vocals", StringComparison.OrdinalIgnoreCase)
                || stem.StartsWith("vocals_", StringComparison.OrdinalIgnoreCase))
            {
                vocals = true;
            }
            else if (stem.Equals("bass", StringComparison.OrdinalIgnoreCase))
            {
                bass = true;
            }
            else if (stem.Equals("drums", StringComparison.OrdinalIgnoreCase) ||
                     stem.StartsWith("drums_", StringComparison.OrdinalIgnoreCase))
            {
                drums = true;
            }
        }
    }
}
