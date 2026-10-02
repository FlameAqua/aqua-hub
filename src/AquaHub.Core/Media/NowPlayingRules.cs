namespace AquaHub.Core.Media;

/// <summary>Windows' playback states for a media session (GlobalSystemMediaTransportControlsSessionPlaybackStatus).</summary>
public enum PlaybackState { Closed, Opened, Changing, Stopped, Playing, Paused }

/// <summary>What Now Playing does with a session.</summary>
public enum NowPlayingDecision { Show, Hide, Keep }

/// <summary>
/// What Now Playing shows for the media session Windows reports, apart from WinRT so the rules can be tested.
/// </summary>
public static class NowPlayingRules
{
    /// <summary>
    /// Show a session that's playing, or one with a title that can be resumed. A closed session, or one that can't be
    /// resumed (a video or song you closed), is nothing playing: showing it would leave a play button that does
    /// nothing. While a player switches tracks ("changing") the last state stays on screen.
    /// </summary>
    public static NowPlayingDecision Decide(string? title, PlaybackState state, bool canPlay, bool canToggle) => state switch
    {
        PlaybackState.Closed => NowPlayingDecision.Hide,
        PlaybackState.Changing => NowPlayingDecision.Keep,
        PlaybackState.Playing => NowPlayingDecision.Show,
        _ => !string.IsNullOrWhiteSpace(title) && (canPlay || canToggle) ? NowPlayingDecision.Show : NowPlayingDecision.Hide,
    };

    /// <summary>
    /// Browsers announce a new title before its artwork, so for a moment the new title comes with the previous one's
    /// picture. Artwork identical to the previous title's is stale (no artwork yet), unless both titles are from the
    /// same album, where one picture is right for both.
    /// </summary>
    public static bool IsStaleArtwork(ReadOnlySpan<byte> artwork, ReadOnlySpan<byte> previousTitlesArtwork, string? album, string? previousAlbum) =>
        !artwork.IsEmpty && !previousTitlesArtwork.IsEmpty && artwork.SequenceEqual(previousTitlesArtwork) &&
        !(!string.IsNullOrWhiteSpace(album) && string.Equals(album, previousAlbum, StringComparison.Ordinal));
}
