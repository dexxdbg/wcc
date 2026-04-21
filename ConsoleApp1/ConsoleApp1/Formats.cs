namespace Wcc;

/// <summary>
/// Describes a target output format and how ffmpeg should produce it.
/// </summary>
internal sealed record TargetFormat(
    string Id,            // file extension without dot, also registry subkey name
    string DisplayName,   // what shows in the context menu
    FormatKind Kind,      // video vs audio
    string FfmpegArgs     // extra args inserted before the output path
);

internal enum FormatKind
{
    Video,
    Audio
}

internal static class Formats
{
    // Extensions recognised as video sources (dot included).
    public static readonly string[] VideoExtensions =
    {
        ".mp4", ".mkv", ".avi", ".mov", ".webm", ".wmv",
        ".flv", ".m4v", ".mpeg", ".mpg", ".ts", ".3gp",
        ".ogv", ".gif"
    };

    // Extensions recognised as audio sources (dot included).
    public static readonly string[] AudioExtensions =
    {
        ".mp3", ".wav", ".flac", ".ogg", ".m4a",
        ".wma", ".aac", ".opus", ".ac3", ".aiff",
        ".aif", ".mka"
    };

    // Notes on the FFmpeg presets below:
    // - No quoted arguments (spaces split with Converter.SplitArgs) - keep filter
    //   strings comma-separated with no internal spaces.
    // - Defaults aim for "good looking, reasonable size" rather than archival.
    // - Video codecs used (libx264, libvpx-vp9, libtheora, mpeg2video, mpeg4,
    //   wmv2) and audio codecs (aac, libmp3lame, libvorbis, libopus, flac,
    //   wmav2, ac3, pcm_s16le/be) are all present in the Gyan "essentials" build.
    public static readonly TargetFormat[] Targets =
    {
        // ---- Video ----
        new("mp4",  "To MP4",          FormatKind.Video,
            "-c:v libx264 -preset medium -crf 20 -c:a aac -b:a 192k -movflags +faststart"),
        new("mkv",  "To MKV",          FormatKind.Video,
            "-c:v libx264 -preset medium -crf 20 -c:a aac -b:a 192k"),
        new("webm", "To WebM (VP9)",   FormatKind.Video,
            "-c:v libvpx-vp9 -crf 32 -b:v 0 -c:a libopus -b:a 128k"),
        new("mov",  "To MOV",          FormatKind.Video,
            "-c:v libx264 -preset medium -crf 20 -c:a aac -b:a 192k -movflags +faststart"),
        new("avi",  "To AVI",          FormatKind.Video,
            "-c:v mpeg4 -qscale:v 4 -c:a libmp3lame -q:a 4"),
        new("flv",  "To FLV",          FormatKind.Video,
            "-c:v libx264 -preset medium -crf 23 -c:a aac -b:a 128k -ar 44100 -f flv"),
        new("wmv",  "To WMV",          FormatKind.Video,
            "-c:v wmv2 -b:v 2M -c:a wmav2 -b:a 192k"),
        new("m4v",  "To M4V",          FormatKind.Video,
            "-c:v libx264 -preset medium -crf 20 -c:a aac -b:a 192k -movflags +faststart"),
        new("mpg",  "To MPG (MPEG-2)", FormatKind.Video,
            "-c:v mpeg2video -q:v 5 -c:a mp2 -b:a 192k"),
        new("ts",   "To TS (MPEG-TS)", FormatKind.Video,
            "-c:v libx264 -preset medium -crf 23 -c:a aac -b:a 128k -f mpegts"),
        new("3gp",  "To 3GP",          FormatKind.Video,
            "-c:v libx264 -profile:v baseline -level 3.0 -s 352x288 -c:a aac -b:a 64k -ar 44100"),
        new("ogv",  "To OGV (Theora)", FormatKind.Video,
            "-c:v libtheora -q:v 7 -c:a libvorbis -q:a 5"),
        new("gif",  "To GIF (animated)", FormatKind.Video,
            "-vf fps=15,scale=480:-1:flags=lanczos,split[s0][s1];[s0]palettegen[p];[s1][p]paletteuse -loop 0"),

        // ---- Audio ----
        new("mp3",  "To MP3",          FormatKind.Audio,
            "-vn -c:a libmp3lame -q:a 2"),
        new("wav",  "To WAV",          FormatKind.Audio,
            "-vn -c:a pcm_s16le"),
        new("flac", "To FLAC",         FormatKind.Audio,
            "-vn -c:a flac"),
        new("ogg",  "To OGG (Vorbis)", FormatKind.Audio,
            "-vn -c:a libvorbis -q:a 5"),
        new("opus", "To Opus",         FormatKind.Audio,
            "-vn -c:a libopus -b:a 128k"),
        new("m4a",  "To M4A (AAC)",    FormatKind.Audio,
            "-vn -c:a aac -b:a 192k"),
        new("aac",  "To AAC",          FormatKind.Audio,
            "-vn -c:a aac -b:a 192k"),
        new("wma",  "To WMA",          FormatKind.Audio,
            "-vn -c:a wmav2 -b:a 192k"),
        new("ac3",  "To AC3",          FormatKind.Audio,
            "-vn -c:a ac3 -b:a 192k"),
        new("aiff", "To AIFF",         FormatKind.Audio,
            "-vn -c:a pcm_s16be"),
        new("mka",  "To MKA (Matroska audio)", FormatKind.Audio,
            "-vn -c:a libopus -b:a 128k")
    };

    /// <summary>Targets offered for a given source extension.</summary>
    public static IEnumerable<TargetFormat> TargetsFor(string sourceExt)
    {
        sourceExt = sourceExt.ToLowerInvariant();
        bool isVideo = VideoExtensions.Contains(sourceExt);
        bool isAudio = AudioExtensions.Contains(sourceExt);
        var srcId = sourceExt.TrimStart('.');

        // .aif and .aiff both map to the "aiff" target; treat equivalently.
        if (srcId == "aif") srcId = "aiff";

        foreach (var t in Targets)
        {
            // Don't offer converting a file to its own format.
            if (string.Equals(t.Id, srcId, StringComparison.OrdinalIgnoreCase))
                continue;

            // Video sources: show everything (audio targets extract the audio track).
            if (isVideo)
                yield return t;
            // Audio sources: only audio targets.
            else if (isAudio && t.Kind == FormatKind.Audio)
                yield return t;
        }
    }

    public static TargetFormat? FindById(string id) =>
        Targets.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase));
}
