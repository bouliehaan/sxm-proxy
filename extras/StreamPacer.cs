// Paces the Icecast response to real time.
//
// Compiled into SXMPlayer.Client at image build time (see Dockerfile), the same
// way DecorationsGenre.cs is. A new file needs no patch against upstream.
//
// WHY THIS EXISTS
//
// The HLS producer hands the response loop a whole segment at once — about ten
// seconds of audio in one 356KB slug — and the loop wrote it to the socket as
// fast as the socket would take it, then went quiet until the next segment. The
// average bitrate was correct; the instantaneous delivery was not.
//
// samo-server does not tolerate that. Its per-listener queue
// (internal/channels/streamer.go) keeps a live listener within
// listenerJitterTarget — 2500ms — of the live edge, and catchUp() DROPS the
// oldest queued audio to hold that. So of every ten-second slug it kept about
// two and a half seconds and discarded the rest, which is heard as a couple of
// seconds of audio followed by eight seconds of silence, over and over. Every
// other internet station in that setup works because a normal Icecast stream
// trickles bytes continuously and never lets the queue get deep enough to trim.
//
// So this proxy has to trickle too. The producer keeps its segment-sized
// buffer — that is what absorbs SiriusXM's own jitter — and the pacer decides
// when those bytes reach the socket: a small burst up front so the client has
// something to start on, then real time.
//
// It costs SiriusXM nothing. Pacing is purely about the proxy's output side;
// the rate segments are fetched upstream is unchanged.

using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace SXMPlayer;

/// <summary>
/// A virtual clock that releases bytes to one client at the rate the audio is
/// actually consumed, after an initial burst. One instance per connection.
/// </summary>
public sealed class StreamPacer
{
    // AAC sampling frequencies, indexed by the ADTS header's 4-bit
    // sampling_frequency_index.
    private static readonly int[] SamplingFrequencies =
    {
        96000, 88200, 64000, 48000, 44100, 32000,
        24000, 22050, 16000, 12000, 11025, 8000, 7350
    };

    // One AAC-LC frame carries this many samples per raw data block.
    private const int SamplesPerRawDataBlock = 1024;

    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private double _secondsReleased;
    private double _bytesPerSecond;

    /// <summary>
    /// How far ahead of real time the client is allowed to run.
    /// <para>
    /// This is the burst a client gets on connect, and it must stay under the
    /// consumer's own jitter tolerance or the consumer will simply throw the
    /// excess away — samo-server trims anything beyond 2.5s. Two seconds is
    /// enough for a decoder to start cleanly and still inside that budget.
    /// </para>
    /// </summary>
    public TimeSpan Burst { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Audio seconds handed to the socket so far. Diagnostics and tests.
    /// </summary>
    public double SecondsReleased => _secondsReleased;

    /// <summary>
    /// True once a usable rate is known; before that, writes are not paced.
    /// </summary>
    public bool HasRate => _bytesPerSecond > 0;

    /// <summary>
    /// Sets the byte rate for the segment about to be written, derived from
    /// that segment's own size and duration so it tracks the real encoding
    /// rather than a nominal bitrate.
    /// </summary>
    public void SetRate(int segmentBytes, double segmentSeconds)
    {
        if (segmentBytes <= 0 || segmentSeconds <= 0)
        {
            return;
        }

        _bytesPerSecond = segmentBytes / segmentSeconds;
    }

    /// <summary>
    /// Accounts for <paramref name="byteCount"/> bytes just written and waits
    /// until real time has caught up to within <see cref="Burst"/>.
    /// </summary>
    public Task PaceAsync(int byteCount, CancellationToken cancellationToken)
    {
        if (byteCount <= 0 || _bytesPerSecond <= 0)
        {
            return Task.CompletedTask;
        }

        _secondsReleased += byteCount / _bytesPerSecond;

        var aheadBy = _secondsReleased - Burst.TotalSeconds - _clock.Elapsed.TotalSeconds;
        if (aheadBy <= 0)
        {
            // Behind or level: never sleep, and never try to "catch up" by
            // writing faster either — the deficit is already reflected in the
            // clock, so the next chunks go out immediately until level again.
            return Task.CompletedTask;
        }

        return Task.Delay(TimeSpan.FromSeconds(aheadBy), cancellationToken);
    }

    /// <summary>
    /// Measures the playing time of an ADTS AAC buffer by walking its frame
    /// headers.
    /// <para>
    /// Derived from the audio itself rather than the playlist's #EXTINF so it
    /// stays correct if a segment is short, truncated, or a different bitrate;
    /// getting this wrong in the fast direction would reintroduce the very
    /// dropouts the pacer exists to remove.
    /// </para>
    /// </summary>
    /// <returns>Duration in seconds, or 0 when no valid frames were found.</returns>
    public static double MeasureDurationSeconds(ReadOnlySpan<byte> adts)
    {
        int offset = 0;
        long samples = 0;
        int sampleRate = 0;

        while (offset < adts.Length)
        {
            var remaining = adts.Slice(offset);
            int frameSize = AacFrameAnalyzer.TryDetectFrameSize(remaining);

            if (frameSize <= 0)
            {
                // Not a frame boundary. Resync rather than abandoning the
                // buffer: a segment may carry a stray byte between frames.
                int boundary = AacFrameAnalyzer.FindNextFrameBoundary(remaining);
                if (boundary <= 0 || boundary >= remaining.Length)
                {
                    break;
                }

                offset += boundary;
                continue;
            }

            if (sampleRate == 0)
            {
                int index = (remaining[2] & 0x3C) >> 2;
                if (index < 0 || index >= SamplingFrequencies.Length)
                {
                    break;
                }

                sampleRate = SamplingFrequencies[index];
            }

            // ADTS may pack several raw data blocks into one frame.
            int blocks = (remaining[6] & 0x03) + 1;
            samples += (long)SamplesPerRawDataBlock * blocks;

            offset += frameSize;
        }

        if (sampleRate <= 0 || samples <= 0)
        {
            return 0;
        }

        return (double)samples / sampleRate;
    }
}
