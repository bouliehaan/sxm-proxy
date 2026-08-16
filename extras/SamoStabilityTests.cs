// Tests for the three fixes that stopped samo's playout cutting in and out.
//
// Compiled into SXMPlayer.Tests at image build time (see Dockerfile), the same
// way SamoExtras.cs is compiled into the Proxy. Kept here rather than in the
// sxm-player checkout so the checkout stays pristine and `git pull` is clean.
//
// EVERYTHING HERE RUNS AGAINST FAKES. Not one test opens a socket to SiriusXM.
// That is deliberate and load-bearing: the bug being fixed is that the proxy
// generated far more SiriusXM traffic than a person listening to the radio
// would, so a test suite that hammered the real API to prove it would be
// self-defeating. LocalTests.cs (upstream) DOES talk to the live API and is
// tagged [Trait("Category", "Local")] — always run with
// `--filter Category!=Local`.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Moq;

namespace SXMPlayer.Tests;

/// <summary>
/// Listener identity is per-connection, not per-IP.
/// </summary>
public class ListenerIdentityTests
{
    private static SegmentWorkItem CreateItem() =>
        new("segment.aac", "v1", 1, new Memory<byte>(new byte[] { 1, 2, 3 }));

    [Fact]
    public void ListenersFromOneIp_WithDifferentConnectionIds_AreNotEqual()
    {
        // Everything samo runs reaches the proxy from the docker bridge
        // gateway, so this is the real-world shape: one address, many clients.
        var ip = IPAddress.Parse("172.21.0.1");

        var ffmpeg = new SXMListener(ip, "conn-ffmpeg");
        var icyProbe = new SXMListener(ip, "conn-icyprobe");

        Assert.NotEqual(ffmpeg, icyProbe);
    }

    [Fact]
    public void ListenersFromOneIp_WithSameConnectionId_AreEqual()
    {
        var ip = IPAddress.Parse("172.21.0.1");

        Assert.Equal(new SXMListener(ip, "conn-a"), new SXMListener(ip, "conn-a"));
    }

    /// <summary>
    /// The regression this whole change exists to prevent: retiring one of
    /// samo's connections must not take the live playout off the air with it.
    /// </summary>
    [Fact]
    public async Task UnregisterByListener_LeavesSiblingConnectionFromSameIpSubscribed()
    {
        var hub = new SegmentFanoutHub(() => { });
        var ip = IPAddress.Parse("172.21.0.1");
        var playout = new SXMListener(ip, "conn-ffmpeg");
        var probe = new SXMListener(ip, "conn-icyprobe");

        var playoutChannel = System.Threading.Channels.Channel.CreateUnbounded<SegmentWorkItem>();
        var probeChannel = System.Threading.Channels.Channel.CreateUnbounded<SegmentWorkItem>();

        hub.Register(playout, playoutChannel.Writer, CancellationToken.None);
        hub.Register(probe, probeChannel.Writer, CancellationToken.None);

        // The ICY probe finishes and gets swept as inactive.
        hub.Unregister(probe);

        // The playout must still be receiving audio.
        await hub.BroadcastAsync(CreateItem(), CancellationToken.None);

        var received = await playoutChannel.Reader.ReadAsync();
        Assert.Equal("segment.aac", received.SegmentName);
        Assert.True(hub.HasSubscribers);
        await Assert.ThrowsAsync<ChannelClosedException>(() => probeChannel.Reader.ReadAsync().AsTask());
    }

    /// <summary>
    /// Documents the old failure mode. With IP-only identity both connections
    /// are the same record, so unregistering either killed both — which is
    /// exactly what cut samo's audio every time a probe finished.
    /// </summary>
    [Fact]
    public async Task UnregisterByListener_WithLegacyIpOnlyIdentity_DropsEverySubscriptionFromThatIp()
    {
        var hub = new SegmentFanoutHub(() => { });
        var ip = IPAddress.Parse("172.21.0.1");

        // No connection id — the pre-fix shape.
        var playout = new SXMListener(ip);
        var probe = new SXMListener(ip);

        var playoutChannel = System.Threading.Channels.Channel.CreateUnbounded<SegmentWorkItem>();
        var probeChannel = System.Threading.Channels.Channel.CreateUnbounded<SegmentWorkItem>();

        hub.Register(playout, playoutChannel.Writer, CancellationToken.None);
        hub.Register(probe, probeChannel.Writer, CancellationToken.None);

        hub.Unregister(probe);

        Assert.False(hub.HasSubscribers);
        await Assert.ThrowsAsync<ChannelClosedException>(() => playoutChannel.Reader.ReadAsync().AsTask());
    }
}

/// <summary>
/// Output pacing — the fix for the dropouts that were actually audible.
/// <para>
/// samo-server holds each live listener within 2500ms of the live edge and
/// <c>catchUp()</c> DROPS the oldest queued audio to keep it there. A ten-second
/// segment delivered instantly therefore lost about seven and a half seconds of
/// itself, heard as a couple of seconds of audio then eight of silence. Pacing
/// makes this proxy trickle like the ordinary Icecast stations that already
/// work in that setup.
/// </para>
/// </summary>
public class StreamPacerTests
{
    /// <summary>
    /// Builds a synthetic ADTS AAC buffer: 44.1kHz, one raw data block per
    /// frame, so each frame is 1024/44100 = 23.22ms.
    /// </summary>
    private static byte[] BuildAdts(int frames, int frameSize = 400, int samplingIndex = 4)
    {
        var buffer = new byte[frames * frameSize];
        for (var f = 0; f < frames; f++)
        {
            var o = f * frameSize;
            buffer[o + 0] = 0xFF;
            buffer[o + 1] = 0xF1;                                  // sync, layer 0, no CRC
            buffer[o + 2] = (byte)((samplingIndex << 2) & 0x3C);
            buffer[o + 3] = (byte)((frameSize >> 11) & 0x03);
            buffer[o + 4] = (byte)((frameSize >> 3) & 0xFF);
            buffer[o + 5] = (byte)(((frameSize & 0x07) << 5) | 0x1F);
            buffer[o + 6] = 0xFC;                                  // 1 raw data block
        }
        return buffer;
    }

    [Fact]
    public void MeasureDuration_CountsAdtsFramesAtTheHeadersSampleRate()
    {
        // 431 frames x 1024 samples / 44100 Hz = 10.008s, one SiriusXM segment.
        var seconds = StreamPacer.MeasureDurationSeconds(BuildAdts(431));

        Assert.Equal(431 * 1024d / 44100d, seconds, 3);
    }

    [Fact]
    public void MeasureDuration_HonoursTheSampleRateIndex()
    {
        // Index 3 is 48kHz, so the same frame count is a shorter buffer.
        var at48k = StreamPacer.MeasureDurationSeconds(BuildAdts(100, samplingIndex: 3));

        Assert.Equal(100 * 1024d / 48000d, at48k, 4);
    }

    [Fact]
    public void MeasureDuration_WithNoValidFrames_ReturnsZero()
    {
        Assert.Equal(0, StreamPacer.MeasureDurationSeconds(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }));
    }

    [Fact]
    public async Task WithoutARate_DoesNotPaceAtAll()
    {
        var pacer = new StreamPacer();

        Assert.False(pacer.HasRate);

        var started = Stopwatch.StartNew();
        await pacer.PaceAsync(64 * 1024, CancellationToken.None);

        Assert.True(started.ElapsedMilliseconds < 200, $"unpaced write slept {started.ElapsedMilliseconds}ms");
    }

    [Fact]
    public async Task TheBurstIsReleasedImmediately()
    {
        var pacer = new StreamPacer { Burst = TimeSpan.FromSeconds(2) };
        pacer.SetRate(segmentBytes: 320_000, segmentSeconds: 10);   // 32 KB/s

        // 2 seconds of audio == 64KB, exactly the burst allowance.
        var started = Stopwatch.StartNew();
        await pacer.PaceAsync(64_000, CancellationToken.None);

        Assert.True(started.ElapsedMilliseconds < 200, $"burst slept {started.ElapsedMilliseconds}ms");
        Assert.Equal(2.0, pacer.SecondsReleased, 2);
    }

    /// <summary>
    /// The invariant that matters: once past the burst, the client can never be
    /// more than Burst ahead of real time — which must stay under
    /// samo-server's 2500ms trim threshold or audio gets discarded.
    /// </summary>
    [Fact]
    public async Task PastTheBurst_TheClientNeverRunsFurtherAheadThanTheBurst()
    {
        var pacer = new StreamPacer { Burst = TimeSpan.FromMilliseconds(300) };
        pacer.SetRate(segmentBytes: 320_000, segmentSeconds: 10);   // 32 KB/s

        var wall = Stopwatch.StartNew();
        for (var i = 0; i < 24; i++)
        {
            await pacer.PaceAsync(3_200, CancellationToken.None);   // 0.1s of audio
        }

        var releasedAhead = pacer.SecondsReleased - wall.Elapsed.TotalSeconds;

        Assert.Equal(2.4, pacer.SecondsReleased, 2);
        Assert.True(releasedAhead <= 0.45, $"ran {releasedAhead:F2}s ahead, past the 0.3s burst");
    }

    /// <summary>
    /// The whole point, stated as samo's constraint: a full ten-second segment
    /// must take about ten seconds to reach the wire, not arrive at once.
    /// </summary>
    [Fact]
    public async Task AFullSegment_TakesRoughlyItsOwnDurationToRelease()
    {
        var pacer = new StreamPacer { Burst = TimeSpan.FromMilliseconds(200) };
        pacer.SetRate(segmentBytes: 32_000, segmentSeconds: 1.0);   // 32 KB/s, 1s segment

        var wall = Stopwatch.StartNew();
        foreach (var _ in Enumerable.Range(0, 10))
        {
            await pacer.PaceAsync(3_200, CancellationToken.None);   // 10 x 0.1s
        }
        wall.Stop();

        // 1s of audio, minus the 200ms burst that goes out free.
        Assert.InRange(wall.Elapsed.TotalMilliseconds, 600, 1400);
    }

    [Fact]
    public async Task WhenBehindRealTime_ItDoesNotSleepAtAll()
    {
        var pacer = new StreamPacer { Burst = TimeSpan.Zero };
        pacer.SetRate(segmentBytes: 320_000, segmentSeconds: 10);

        // Let real time run past what has been released.
        await Task.Delay(400);

        var started = Stopwatch.StartNew();
        await pacer.PaceAsync(3_200, CancellationToken.None);

        Assert.True(started.ElapsedMilliseconds < 150, $"slept {started.ElapsedMilliseconds}ms while behind");
    }
}

/// <summary>
/// Burst-on-connect: every client gets a cushion, not just the first one.
/// <para>
/// This is the case the first attempt at the prebuffer missed entirely.
/// Starting a fresh producer further back in the playlist helps only the client
/// that started it — the producer is shared and long-lived (more so with the
/// linger), so everyone attaching afterwards joined an in-flight stream with an
/// empty queue and no cushion at all.
/// </para>
/// </summary>
public class BurstOnConnectTests
{
    private static SegmentWorkItem Item(string name) =>
        new(name, "v1", 1, new Memory<byte>(new byte[] { 1, 2, 3 }));

    private static SXMListener Listener(string connectionId) =>
        new(IPAddress.Parse("172.21.0.1"), connectionId);

    /// <summary>
    /// The exact failure that was still audible after the first fix deployed.
    /// </summary>
    [Fact]
    public async Task ClientJoiningAnInFlightStream_ImmediatelyReceivesTheBacklog()
    {
        var hub = new SegmentFanoutHub(() => { }) { BacklogSegments = 4 };

        // A stream already running, with an earlier client long since attached.
        var existing = System.Threading.Channels.Channel.CreateUnbounded<SegmentWorkItem>();
        hub.Register(Listener("conn-existing"), existing.Writer, CancellationToken.None);
        foreach (var name in new[] { "seg1.aac", "seg2.aac", "seg3.aac", "seg4.aac" })
        {
            await hub.BroadcastAsync(Item(name), CancellationToken.None);
        }

        // samo reconnects mid-stream.
        var latecomer = System.Threading.Channels.Channel.CreateUnbounded<SegmentWorkItem>();
        hub.Register(Listener("conn-latecomer"), latecomer.Writer, CancellationToken.None);

        // It must have audio in hand right now, not whenever the next segment
        // happens to land.
        var received = new List<string>();
        while (latecomer.Reader.TryRead(out var item))
        {
            received.Add(item.SegmentName);
        }

        Assert.Equal(new[] { "seg1.aac", "seg2.aac", "seg3.aac", "seg4.aac" }, received);
    }

    [Fact]
    public async Task BacklogIsBoundedToTheConfiguredDepth()
    {
        var hub = new SegmentFanoutHub(() => { }) { BacklogSegments = 2 };

        for (var i = 1; i <= 6; i++)
        {
            await hub.BroadcastAsync(Item($"seg{i}.aac"), CancellationToken.None);
        }

        Assert.Equal(2, hub.BufferedSegmentCount);

        var latecomer = System.Threading.Channels.Channel.CreateUnbounded<SegmentWorkItem>();
        hub.Register(Listener("conn-late"), latecomer.Writer, CancellationToken.None);

        var received = new List<string>();
        while (latecomer.Reader.TryRead(out var item))
        {
            received.Add(item.SegmentName);
        }

        // The two most recent, oldest first.
        Assert.Equal(new[] { "seg5.aac", "seg6.aac" }, received);
    }

    /// <summary>
    /// The backlog must not double-deliver: a segment broadcast after the
    /// newcomer joins should arrive exactly once, in order, after the cushion.
    /// </summary>
    [Fact]
    public async Task SegmentsAfterJoining_ArriveOnceAndInOrder()
    {
        var hub = new SegmentFanoutHub(() => { }) { BacklogSegments = 3 };
        await hub.BroadcastAsync(Item("old1.aac"), CancellationToken.None);
        await hub.BroadcastAsync(Item("old2.aac"), CancellationToken.None);

        var latecomer = System.Threading.Channels.Channel.CreateUnbounded<SegmentWorkItem>();
        hub.Register(Listener("conn-late"), latecomer.Writer, CancellationToken.None);

        await hub.BroadcastAsync(Item("new1.aac"), CancellationToken.None);

        var received = new List<string>();
        while (latecomer.Reader.TryRead(out var item))
        {
            received.Add(item.SegmentName);
        }

        Assert.Equal(new[] { "old1.aac", "old2.aac", "new1.aac" }, received);
    }

    [Fact]
    public async Task FirstEverClient_GetsNoBacklogAndIsNotBlocked()
    {
        var hub = new SegmentFanoutHub(() => { }) { BacklogSegments = 4 };
        var first = System.Threading.Channels.Channel.CreateUnbounded<SegmentWorkItem>();

        hub.Register(Listener("conn-first"), first.Writer, CancellationToken.None);
        Assert.False(first.Reader.TryRead(out _));

        await hub.BroadcastAsync(Item("seg1.aac"), CancellationToken.None);
        Assert.True(first.Reader.TryRead(out var item));
        Assert.Equal("seg1.aac", item.SegmentName);
    }

    [Fact]
    public async Task ZeroBacklog_DisablesTheBurstEntirely()
    {
        var hub = new SegmentFanoutHub(() => { }) { BacklogSegments = 0 };
        await hub.BroadcastAsync(Item("seg1.aac"), CancellationToken.None);

        Assert.Equal(0, hub.BufferedSegmentCount);

        var latecomer = System.Threading.Channels.Channel.CreateUnbounded<SegmentWorkItem>();
        hub.Register(Listener("conn-late"), latecomer.Writer, CancellationToken.None);

        Assert.False(latecomer.Reader.TryRead(out _));
    }

    /// <summary>
    /// PrebufferSegments drives both halves of the cushion; they must not drift.
    /// </summary>
    [Fact]
    public void ProducerPrebufferSetting_FlowsThroughToTheBacklogDepth()
    {
        var producer = new HlsSegmentProducer(
            (SiriusXMPlayer)RuntimeHelpers.GetUninitializedObject(typeof(SiriusXMPlayer)),
            new Mock<ILogger>().Object);

        // Default must already be coupled, not just after an explicit set.
        Assert.Equal(producer.PrebufferSegments, producer.BacklogSegments);

        producer.PrebufferSegments = 7;

        Assert.Equal(7, producer.BacklogSegments);
    }
}

/// <summary>
/// Where a fresh producer starts in the playlist window.
/// <para>
/// This is the one that actually caused the audio to cut in and out. The old
/// calculation, <c>firstSequence + segmentCount - 2</c>, emitted only the final
/// segment no matter how deep the window was, so the client got one ~10s
/// segment every ~10s with no cushion and starved on the first late fetch.
/// </para>
/// </summary>
public class PrebufferStartSequenceTests
{
    /// <summary>
    /// Segments run firstSequence .. firstSequence + segmentCount - 1, and the
    /// producer emits everything strictly greater than the returned value, so
    /// this is how many the client receives up front.
    /// </summary>
    private static long SegmentsEmitted(long firstSequence, int segmentCount, int prebuffer)
    {
        var start = HlsSegmentProducer.ComputeStartSequence(firstSequence, segmentCount, prebuffer);
        var lastSequence = firstSequence + segmentCount - 1;
        return lastSequence - start;
    }

    /// <summary>
    /// The real numbers measured against Elvis Radio: a 1845-segment window.
    /// The old code handed over exactly one segment out of 1845.
    /// </summary>
    [Fact]
    public void DeepWindow_HandsOverThePrebufferNotASingleSegment()
    {
        Assert.Equal(4, SegmentsEmitted(firstSequence: 79375, segmentCount: 1845, prebuffer: 4));
    }

    [Fact]
    public void OldBehaviour_EmittedOnlyTheFinalSegment_Regardless()
    {
        // What the pre-fix arithmetic produced, kept as the thing we moved away
        // from: firstSequence + segmentCount - 2 is one below the last sequence.
        var oldStart = 79375L + 1845 - 2;
        var lastSequence = 79375L + 1845 - 1;

        Assert.Equal(1, lastSequence - oldStart);
    }

    [Fact]
    public void ShallowWindow_NeverRewindsPastTheStartOfTheWindow()
    {
        // Only two segments available but four wanted — take the two there are
        // rather than inventing sequence numbers SiriusXM never listed.
        Assert.Equal(2, SegmentsEmitted(firstSequence: 100, segmentCount: 2, prebuffer: 4));
    }

    [Fact]
    public void ExactlyEnoughSegments_HandsOverAllOfThem()
    {
        Assert.Equal(4, SegmentsEmitted(firstSequence: 500, segmentCount: 4, prebuffer: 4));
    }

    [Fact]
    public void EmptyPlaylist_YieldsSentinelSoNothingIsSkipped()
    {
        Assert.Equal(-1, HlsSegmentProducer.ComputeStartSequence(0, 0, 4));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void NonPositivePrebuffer_StillEmitsAtLeastOneSegment(int prebuffer)
    {
        Assert.Equal(1, SegmentsEmitted(firstSequence: 10, segmentCount: 50, prebuffer: prebuffer));
    }

    /// <summary>
    /// A missing #EXT-X-MEDIA-SEQUENCE parses as -1. The clamp must still bound
    /// the emission — without it the producer would dump the entire window,
    /// which on Elvis Radio is about five hours of audio in one burst.
    /// </summary>
    [Fact]
    public void MissingMediaSequence_StillBoundsTheBurst()
    {
        Assert.Equal(4, SegmentsEmitted(firstSequence: -1, segmentCount: 1845, prebuffer: 4));
    }
}

/// <summary>
/// The shared producer lingers after its last subscriber leaves, so a
/// reconnect re-attaches instead of forcing a cold start against SiriusXM.
/// </summary>
public class ProducerLingerTests
{
    /// <summary>
    /// HlsSegmentProducer requires a SiriusXMPlayer, whose constructor demands
    /// credentials and starts background timers. None of that is reachable in
    /// these tests: the channel provider below returns null, so
    /// RunProducerAsync throws before it ever touches the player. An
    /// uninitialised instance therefore satisfies the null check without
    /// authenticating, starting a timer, or opening a socket.
    /// </summary>
    private static SiriusXMPlayer PlayerThatIsNeverCalled() =>
        (SiriusXMPlayer)RuntimeHelpers.GetUninitializedObject(typeof(SiriusXMPlayer));

    /// <summary>
    /// Returning null makes the producer loop fail fast and retry, keeping the
    /// producer task alive without any SiriusXM interaction at all.
    /// </summary>
    private static Task<ChannelItemData?> NoChannel() => Task.FromResult<ChannelItemData?>(null);

    private static HlsSegmentProducer CreateProducer(TimeSpan linger)
    {
        var producer = new HlsSegmentProducer(PlayerThatIsNeverCalled(), new Mock<ILogger>().Object)
        {
            ProducerLinger = linger
        };
        return producer;
    }

    private static bool StartSubscriber(
        HlsSegmentProducer producer,
        string connectionId,
        CancellationToken disconnect)
    {
        var listener = new SXMListener(IPAddress.Parse("172.21.0.1"), connectionId);
        var queue = System.Threading.Channels.Channel.CreateUnbounded<SegmentWorkItem>();
        return producer.StartProducer(queue.Writer, NoChannel, listener, CancellationToken.None, disconnect);
    }

    [Fact]
    public void FirstSubscriber_StartsProducer()
    {
        var producer = CreateProducer(TimeSpan.FromSeconds(5));
        using var disconnect = new CancellationTokenSource();

        var wasAlreadyActive = StartSubscriber(producer, "conn-1", disconnect.Token);

        Assert.False(wasAlreadyActive);
        Assert.True(producer.IsActive);
    }

    [Fact]
    public async Task ProducerStaysActive_WhileLingering_AfterLastSubscriberLeaves()
    {
        var producer = CreateProducer(TimeSpan.FromSeconds(5));
        using var disconnect = new CancellationTokenSource();
        StartSubscriber(producer, "conn-1", disconnect.Token);

        await disconnect.CancelAsync();

        // Well inside the linger window.
        await Task.Delay(250);
        Assert.True(producer.IsActive);
    }

    /// <summary>
    /// The point of the linger: an ffmpeg reconnect re-attaches to the running
    /// producer. wasAlreadyActive == true means no restart, which means no
    /// re-tune and no playlist fetch against SiriusXM.
    /// </summary>
    [Fact]
    public async Task ReconnectDuringLinger_ReattachesWithoutRestartingProducer()
    {
        var producer = CreateProducer(TimeSpan.FromSeconds(5));
        using var firstConnection = new CancellationTokenSource();
        StartSubscriber(producer, "conn-1", firstConnection.Token);

        await firstConnection.CancelAsync();
        await Task.Delay(100);

        using var reconnect = new CancellationTokenSource();
        var wasAlreadyActive = StartSubscriber(producer, "conn-2", reconnect.Token);

        Assert.True(wasAlreadyActive);
        Assert.True(producer.IsActive);
    }

    [Fact]
    public async Task ProducerStops_OnceLingerElapsesWithNoSubscribers()
    {
        var producer = CreateProducer(TimeSpan.FromMilliseconds(200));
        using var disconnect = new CancellationTokenSource();
        StartSubscriber(producer, "conn-1", disconnect.Token);
        Assert.True(producer.IsActive);

        await disconnect.CancelAsync();

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (producer.IsActive && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
        }

        Assert.False(producer.IsActive);
    }

    /// <summary>
    /// A reconnect inside the linger must DISARM the pending teardown, not just
    /// rely on it declining to act.
    /// <para>
    /// The scheduled callback re-checks the fanout before stopping anything, so
    /// leaving it armed usually looks harmless. It is not: the callback and
    /// StartProducer contend for the same lock, and if the timer wins that race
    /// it cancels the producer a moment before the reconnecting client
    /// registers — handing it a producer that is already dying. Asserting on
    /// IsLingerPending is what distinguishes "cancelled" from "fired and
    /// happened to find a subscriber".
    /// </para>
    /// </summary>
    [Fact]
    public async Task ReconnectDuringLinger_DisarmsThePendingTeardown()
    {
        var producer = CreateProducer(TimeSpan.FromSeconds(5));
        using var firstConnection = new CancellationTokenSource();
        StartSubscriber(producer, "conn-1", firstConnection.Token);

        await firstConnection.CancelAsync();
        await Task.Delay(100);
        Assert.True(producer.IsLingerPending);

        using var reconnect = new CancellationTokenSource();
        StartSubscriber(producer, "conn-2", reconnect.Token);

        Assert.False(producer.IsLingerPending);
        Assert.True(producer.IsActive);
    }

    /// <summary>
    /// Leaving again after a reconnect must arm a fresh linger rather than
    /// inherit the first one's already-expired deadline.
    /// </summary>
    [Fact]
    public async Task LeavingAgainAfterAReconnect_ArmsAFreshLinger()
    {
        var producer = CreateProducer(TimeSpan.FromSeconds(5));
        using var firstConnection = new CancellationTokenSource();
        StartSubscriber(producer, "conn-1", firstConnection.Token);
        await firstConnection.CancelAsync();
        await Task.Delay(100);

        using var reconnect = new CancellationTokenSource();
        StartSubscriber(producer, "conn-2", reconnect.Token);
        Assert.False(producer.IsLingerPending);

        await reconnect.CancelAsync();
        await Task.Delay(100);

        Assert.True(producer.IsLingerPending);
        Assert.True(producer.IsActive);
    }
}
