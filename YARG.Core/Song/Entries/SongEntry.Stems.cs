using System.Collections.Generic;

namespace YARG.Core.Song
{
    public enum StemSeparation
    {
        /// <summary>The song has its own vocals, bass, or drums.</summary>
        Present,
        /// <summary>Vocals, bass, and drums are all absent, so the full mix is the song stem.</summary>
        Missing,
        /// <summary>Stems made by an outside tool are installed and used instead of the full mix.</summary>
        Custom,
    }

    public abstract partial class SongEntry
    {
        private static int _stemGeneration;

        private StemSeparation _stemSeparation;
        private int _stemSeparationGeneration = -1;

        /// <summary>
        /// Whether this song is missing stems or uses custom ones. Checked again after
        /// <see cref="SetCustomStemLibraryRoots"/>, so stems added while YARG runs show up.
        /// </summary>
        public StemSeparation GetStemSeparation()
        {
            int generation = _stemGeneration;
            if (_stemSeparationGeneration != generation)
            {
                _stemSeparation = ComputeStemSeparation();
                _stemSeparationGeneration = generation;
            }

            return _stemSeparation;
        }

        /// <summary>
        /// Song folders selected in the music library. Custom stems are read from a demucs folder at each root.
        /// </summary>
        public static void SetCustomStemLibraryRoots(IEnumerable<string> roots)
        {
            CustomStems.SetLibraryRoots(roots);
            System.Threading.Interlocked.Increment(ref _stemGeneration);
        }

        protected virtual StemSeparation ComputeStemSeparation()
        {
            return StemSeparation.Present;
        }
    }
}
