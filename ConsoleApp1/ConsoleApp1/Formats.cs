namespace Wcc;

// a single conversion target - basically just the extension, what to show in the menu,
// whether its video or audio, and what ffmpeg args to use
internal sealed record TargetFormat(
    string Id,            // extension without dot, e.g. "mp4" - also used as registry subkey name
    string DisplayName,   // what shows up in the right-click menu
    FormatKind Kind,      // video or audio - used to decide what to show for a given source file
    string FfmpegArgs     // the args we pass to ffmpeg before the output path
);

internal enum FormatKind
{
    Video,
    Audio
}

internal static class Formats
{
    // these are all the extensions we treat as video files
    // if a file has one of these extensions, it gets the full menu (video + audio targets)
    public static readonly string[] VideoExtensions =
    {
        ".mp4", ".mkv", ".avi", ".mov", ".webm", ".wmv",
        ".flv", ".m4v", ".mpeg", ".mpg", ".ts", ".3gp",
        ".ogv", ".gif"
    };

    // these are treated as audio-only, so they only get audio conversion targets
    // showing "To MP4" on an mp3 doesnt make sense since theres no video track
    public static readonly string[] AudioExtensions =
    {
        ".mp3", ".wav", ".flac", ".ogg", ".m4a",
        ".wma", ".aac", ".opus", ".ac3", ".aiff",
        ".aif", ".mka"
    };

    // all the presets - one entry per output format
    // a few things to keep in mind here:
    //   - no spaces inside filter strings (the arg splitter just splits on space)
    //   - all these codecs are in the BtbN GPL build so nothing will randomly fail at runtime
    //   - settings are tuned for "looks good, reasonable size" not archival quality
    public static readonly TargetFormat[] Targets =
    {
        // ---- Video ----

        // mp4 is the most compatible format, pretty much plays everywhere
        new("mp4",  "To MP4",          FormatKind.Video,
            "-c:v libx264 -preset medium -crf 20 -c:a aac -b:a 192k -movflags +faststart"),

        // mkv is great if you want subtitles or multiple audio tracks later
        new("mkv",  "To MKV",          FormatKind.Video,
            "-c:v libx264 -preset medium -crf 20 -c:a aac -b:a 192k"),

        // webm is what you use for the web, vp9+opus is the modern combo
        new("webm", "To WebM (VP9)",   FormatKind.Video,
            "-c:v libvpx-vp9 -crf 32 -b:v 0 -c:a libopus -b:a 128k"),

        // mov is basically apple's mp4, same quality just different container
        new("mov",  "To MOV",          FormatKind.Video,
            "-c:v libx264 -preset medium -crf 20 -c:a aac -b:a 192k -movflags +faststart"),

        // avi is old but some old software still wants it
        new("avi",  "To AVI",          FormatKind.Video,
            "-c:v mpeg4 -qscale:v 4 -c:a libmp3lame -q:a 4"),

        // flv used to be the flash format, still used by some streaming stuff
        new("flv",  "To FLV",          FormatKind.Video,
            "-c:v libx264 -preset medium -crf 23 -c:a aac -b:a 128k -ar 44100 -f flv"),

        // wmv is windows media video, old microsoft format
        new("wmv",  "To WMV",          FormatKind.Video,
            "-c:v wmv2 -b:v 2M -c:a wmav2 -b:a 192k"),

        // m4v is itunes/apple devices basically, same as mp4 inside
        new("m4v",  "To M4V",          FormatKind.Video,
            "-c:v libx264 -preset medium -crf 20 -c:a aac -b:a 192k -movflags +faststart"),

        // mpeg-2 for dvds and legacy broadcast stuff
        new("mpg",  "To MPG (MPEG-2)", FormatKind.Video,
            "-c:v mpeg2video -q:v 5 -c:a mp2 -b:a 192k"),

        // ts is the transport stream format used for broadcast / iptv
        new("ts",   "To TS (MPEG-TS)", FormatKind.Video,
            "-c:v libx264 -preset medium -crf 23 -c:a aac -b:a 128k -f mpegts"),

        // 3gp is for old mobile phones, capped at low res on purpose
        new("3gp",  "To 3GP",          FormatKind.Video,
            "-c:v libx264 -profile:v baseline -level 3.0 -s 352x288 -c:a aac -b:a 64k -ar 44100"),

        // ogv uses the theora codec, open source alternative to h264
        new("ogv",  "To OGV (Theora)", FormatKind.Video,
            "-c:v libtheora -q:v 7 -c:a libvorbis -q:a 5"),

        // gif - palette trick makes it look way better than naive conversion
        // warning: gifs get huge fast, dont use on long videos
        new("gif",  "To GIF (animated)", FormatKind.Video,
            "-vf fps=15,scale=480:-1:flags=lanczos,split[s0][s1];[s0]palettegen[p];[s1][p]paletteuse -loop 0"),

        // ---- Audio ----

        // mp3 is still the most compatible audio format basically everywhere
        new("mp3",  "To MP3",          FormatKind.Audio,
            "-vn -c:a libmp3lame -q:a 2"),

        // wav is uncompressed, huge files but zero quality loss
        new("wav",  "To WAV",          FormatKind.Audio,
            "-vn -c:a pcm_s16le"),

        // flac is lossless but compressed, best of both worlds for archiving
        new("flac", "To FLAC",         FormatKind.Audio,
            "-vn -c:a flac"),

        // ogg vorbis - open source, good quality at low bitrates
        new("ogg",  "To OGG (Vorbis)", FormatKind.Audio,
            "-vn -c:a libvorbis -q:a 5"),

        // opus is the modern standard, beats mp3 at the same bitrate easily
        new("opus", "To Opus",         FormatKind.Audio,
            "-vn -c:a libopus -b:a 128k"),

        // m4a is just aac in an mp4 container, what itunes uses
        new("m4a",  "To M4A (AAC)",    FormatKind.Audio,
            "-vn -c:a aac -b:a 192k"),

        // raw aac file without the m4a wrapper
        new("aac",  "To AAC",          FormatKind.Audio,
            "-vn -c:a aac -b:a 192k"),

        // wma is windows media audio, old microsoft format
        new("wma",  "To WMA",          FormatKind.Audio,
            "-vn -c:a wmav2 -b:a 192k"),

        // ac3 is dolby digital, common in dvd audio tracks
        new("ac3",  "To AC3",          FormatKind.Audio,
            "-vn -c:a ac3 -b:a 192k"),

        // aiff is basically the apple version of wav, uncompressed pcm
        new("aiff", "To AIFF",         FormatKind.Audio,
            "-vn -c:a pcm_s16be"),

        // mka is just matroska audio container, good for opus streams
        new("mka",  "To MKA (Matroska audio)", FormatKind.Audio,
            "-vn -c:a libopus -b:a 128k")
    };

    // figures out which targets to show for a given source file extension
    // video files get everything, audio files only get audio targets
    // also filters out the same format as the source so u dont see "mp4 -> mp4"
    public static IEnumerable<TargetFormat> TargetsFor(string sourceExt)
    {
        sourceExt = sourceExt.ToLowerInvariant();
        bool isVideo = VideoExtensions.Contains(sourceExt);
        bool isAudio = AudioExtensions.Contains(sourceExt);
        var srcId = sourceExt.TrimStart('.');

        // .aif and .aiff are the same thing, normalize so we dont show aiff->aiff
        if (srcId == "aif") srcId = "aiff";

        foreach (var t in Targets)
        {
            // dont offer the same format the file is already in
            if (string.Equals(t.Id, srcId, StringComparison.OrdinalIgnoreCase))
                continue;

            // video files show all formats (audio targets extract the audio track)
            if (isVideo)
                yield return t;
            // audio files only get audio targets, no point showing "To MP4" for an mp3
            else if (isAudio && t.Kind == FormatKind.Audio)
                yield return t;
        }
    }

    // simple lookup by id, used by Converter to find the right preset
    public static TargetFormat? FindById(string id) =>
        Targets.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase));
}
