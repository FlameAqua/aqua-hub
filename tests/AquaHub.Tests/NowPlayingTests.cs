using AquaHub.Core.Media;

namespace AquaHub.Tests;

/// <summary>Now Playing: what counts as something playing, and when a picture belongs to an earlier title.</summary>
public class NowPlayingTests
{
    [Theory]
    [InlineData("Lo-fi beats", PlaybackState.Playing, false, false, NowPlayingDecision.Show)]
    [InlineData("", PlaybackState.Playing, false, false, NowPlayingDecision.Show)]                 // "Playing in Chrome"
    [InlineData("Lo-fi beats", PlaybackState.Paused, true, true, NowPlayingDecision.Show)]           // can be resumed
    [InlineData("Lo-fi beats", PlaybackState.Paused, false, true, NowPlayingDecision.Show)]
    [InlineData("Lo-fi beats", PlaybackState.Paused, false, false, NowPlayingDecision.Hide)]         // closed video: nothing to resume
    [InlineData("Lo-fi beats", PlaybackState.Stopped, false, false, NowPlayingDecision.Hide)]
    [InlineData("Lo-fi beats", PlaybackState.Closed, true, true, NowPlayingDecision.Hide)]
    [InlineData("", PlaybackState.Paused, true, true, NowPlayingDecision.Hide)]                      // an idle tab
    [InlineData("Next track", PlaybackState.Changing, true, true, NowPlayingDecision.Keep)]
    public void OnlyWhatCanBeControlledIsShown(string title, PlaybackState state, bool canPlay, bool canToggle, NowPlayingDecision expected) =>
        Assert.Equal(expected, NowPlayingRules.Decide(title, state, canPlay, canToggle));

    [Fact]
    public void TheWindowsStatesLineUp()
    {
        // Mirrors GlobalSystemMediaTransportControlsSessionPlaybackStatus, which the app casts from by number.
        Assert.Equal(new[] { 0, 1, 2, 3, 4, 5 },
            new[] { PlaybackState.Closed, PlaybackState.Opened, PlaybackState.Changing, PlaybackState.Stopped, PlaybackState.Playing, PlaybackState.Paused }.Select(s => (int)s));
    }

    [Fact]
    public void ThePreviousTitlesPictureIsStale()
    {
        byte[] streamerA = { 1, 2, 3, 4 }, streamerB = { 9, 8, 7 };
        // Switching streams: the new title arrives with the old picture first, then its own.
        Assert.True(NowPlayingRules.IsStaleArtwork(streamerA, streamerA, null, null));
        Assert.False(NowPlayingRules.IsStaleArtwork(streamerB, streamerA, null, null));
        // The next track of the same album rightly keeps the cover.
        Assert.False(NowPlayingRules.IsStaleArtwork(streamerA, streamerA, "Random Access Memories", "Random Access Memories"));
        Assert.True(NowPlayingRules.IsStaleArtwork(streamerA, streamerA, "Discovery", "Random Access Memories"));
        // Nothing to compare with: whatever arrives is shown.
        Assert.False(NowPlayingRules.IsStaleArtwork(streamerA, Array.Empty<byte>(), null, null));
        Assert.False(NowPlayingRules.IsStaleArtwork(Array.Empty<byte>(), streamerA, null, null));
    }
}
