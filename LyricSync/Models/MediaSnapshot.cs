namespace LyricSync.Models;

/// <summary>
/// Immutable snapshot of the media session state at <see cref="PositionTimestamp"/>.
/// The playback position is interpolated from here between polls.
/// </summary>
public sealed record MediaSnapshot(
    TrackInfo Track,
    bool IsPlaying,
    double PlaybackRate,
    TimeSpan Position,
    DateTimeOffset PositionTimestamp)
{
    public TimeSpan EstimatePosition(DateTimeOffset now)
    {
        if (!IsPlaying)
        {
            return Position;
        }

        var rate = PlaybackRate <= 0 ? 1.0 : PlaybackRate;
        var estimated = Position + TimeSpan.FromSeconds((now - PositionTimestamp).TotalSeconds * rate);
        if (estimated < TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        if (Track.DurationSeconds > 0 && estimated.TotalSeconds > Track.DurationSeconds)
        {
            return TimeSpan.FromSeconds(Track.DurationSeconds);
        }

        return estimated;
    }
}
