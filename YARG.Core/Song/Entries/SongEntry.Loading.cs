using System;
using System.IO;
using YARG.Core.Audio;
using YARG.Core.Chart;
using YARG.Core.IO;
using YARG.Core.Venue;

namespace YARG.Core.Song
{
    public class BackgroundResult : IDisposable
    {
        private YARGImage?     _image;
        public  BackgroundType Type   { get; }
        public  Stream?        Stream { get; }

        public YARGImage? Image => _image;

        public string? VenueHint { get; }

        public BackgroundResult(BackgroundType type, Stream stream)
        {
            _image = null;
            Type = type;
            Stream = stream;
        }

        public BackgroundResult(YARGImage image)
        {
            _image = image;
            Type = BackgroundType.Image;
            Stream = null;
        }

        public BackgroundResult(string venueHint)
        {
            VenueHint = venueHint;
            Type = BackgroundType.Yarground;
            Stream = null;
        }

        public void Dispose()
        {
            _image?.Dispose();
            Stream?.Dispose();
        }
    }

    public abstract partial class SongEntry
    {
        public abstract SongChart? LoadChart();
        public abstract StemMixer? LoadAudio(float speed, double volume, bool enableCensoring, params SongStem[] ignoreStems);
        public abstract StemMixer? LoadPreviewAudio(float speed, bool enableCensoring);
        public abstract YARGImage? LoadAlbumData();
        public abstract BackgroundResult? LoadBackground(bool enableCensoring, bool excludeYarground = false);
        public abstract FixedArray<byte>? LoadMiloData();
        public abstract FixedArray<byte>? LoadVocData();

        // -1 = not checked yet, 0 = no, 1 = yes
        private int _hasVideoBackground = -1;

        /// <summary>
        /// Whether the song ships a background video. Only checks that a file exists (nothing is
        /// opened or decoded), and the result is cached after the first call.
        /// </summary>
        public bool HasVideoBackground()
        {
            int value = _hasVideoBackground;
            if (value < 0)
            {
                try
                {
                    value = ComputeHasVideoBackground() ? 1 : 0;
                }
                catch (Exception)
                {
                    value = 0;
                }
                _hasVideoBackground = value;
            }
            return value == 1;
        }

        protected virtual bool ComputeHasVideoBackground()
        {
            return false;
        }

        /// <summary>
        /// Every file name <see cref="LoadBackground"/> accepts as a video for the given base name,
        /// in either censoring mode.
        /// </summary>
        protected static System.Collections.Generic.IEnumerable<string> GetVideoFileNames(string baseName)
        {
            foreach (var ext in VIDEO_EXTENSIONS)
            {
                yield return baseName + ext;
                yield return baseName + CLEAN_BACKGROUND_SUFFIX + ext;
                yield return baseName + EXPLICIT_BACKGROUND_SUFFIX + ext;
            }
        }
    }
}
