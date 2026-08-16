# Patches against `sxm-player` @ `470b35b`

`SamoExtras.cs` and `DecorationsGenre.cs` are *additions* — new files plus one
anchored `sed` line, which is why they need no diff. The changes here are
different in kind: they edit upstream's streaming logic, so they ship as a
patch.

The `sxm-player/` checkout on disk stays pristine. The patch is applied inside
the build container only (see `Dockerfile`), guarded with `git apply --check`
so upstream drift fails the build loudly rather than producing an image that
streams but still cuts out.

## `samo-stability.patch`

Fixes samo's playout cutting in and out. Cause 0 below is why the audio broke;
causes 1–3 are connection churn found in the same investigation, all of which
also generated far more SiriusXM traffic than a person listening to the radio
would.

### 0. The stream was delivered with no prebuffer at all

`RunProducerAsync` synced a fresh producer to the live edge with:

```csharp
lastMediaSequence = currentMediaSequence + lines.Count(s => s.Trim().EndsWith(".aac")) - 2;
```

SiriusXM's playlist window is very deep — Elvis Radio returns **1845 segments**,
about five hours — and this discards all but the final one. The client then
received exactly one ~10s segment every ~10s, in lockstep with its own playback
and with zero cushion. Measured on the wire:

```
duration 60.0s  total 1890657 bytes = 252.1 kbps
t=5.6s  gap=5.56s     ← first audio 5.6s after connect
t=16.6s gap=11.08s
t=28.0s gap=11.35s
t=39.5s gap=11.04s
t=50.9s gap=11.44s
```

Correct average bitrate, delivered as a 356KB burst every 11.3s with dead air
between. A browser's `<audio>` element buffers tens of seconds ahead and rides
that out, which is why the `/ui` preview always worked and samo never did.
ffmpeg playing out in real time starves the first time a fetch runs slightly
late — heard as the stream cutting in and out.

The start position is now `ComputeStartSequence`, which begins
`PrebufferSegments` (default 4, ~40s) back from the live edge and never rewinds
past the start of the window actually served. Steady state is unchanged at one
segment per segment duration; only the startup burst differs. This costs no
extra SiriusXM traffic beyond those few segments fetched once, and they are
already listed in the playlist SiriusXM returned — Apple's own guidance is to
start at least three target durations from the end of a live playlist, so it is
the more ordinary-looking client behaviour, not the less.

**That alone is not enough**, and shipping it alone did not fix the audio. The
start position only applies when the *producer* starts cold — `lastMediaSequence
== -1`. The producer is shared and long-lived (more so with the linger from
cause 2), so a client attaching to a running one joins mid-flight with an empty
queue and no cushion whatsoever. That is nearly every connection samo makes.
Measured after deploying the start-position fix on its own:

```
t=3.9s  gap=3.91s
t=16.5s gap=12.61s
t=28.0s gap=11.46s
t=44.3s gap=15.85s
first-40s delivered: 1575520 bytes     ← ~4.4 segments in 45s = exactly real time
```

So `SegmentFanoutHub` now also keeps a rolling backlog of the last
`BacklogSegments` broadcast segments and replays them into a new subscriber's
queue at `Register` time, before it joins the live fanout. This is precisely
Icecast's own `burst-on-connect`, for precisely the same reason. Backlog depth
is driven by `PrebufferSegments` so the two halves cannot drift apart.

Registration and broadcast share one lock so a segment can never slip between
"replay the backlog" and "join the fanout" — a newcomer missing one segment
mid-stream is an audible discontinuity, which is worse than a slow start. Writes
use `TryWrite`; subscriber queues are unbounded, so the fan-out never blocks
inside that lock. Memory is bounded at roughly `BacklogSegments × segment size`
(~1.4MB at the default) and segments are shared by reference, not copied per
subscriber. It costs nothing against SiriusXM: those segments were already
fetched for the clients that came before.

### 4. Output was delivered in bursts, and samo-server threw most of each away

Causes 0–3 all made the proxy behave better without making the audio work,
because none of them addressed what the wire actually looked like. The producer
hands the response loop a whole segment at once, and the loop wrote it to the
socket as fast as the socket would take it, then went quiet for ten seconds.
Average bitrate correct; instantaneous delivery nothing like a radio stream.

samo-server does not tolerate that. From `internal/channels/streamer.go`:

```go
listenerBuffer       = 64                       // chunks
streamChunk          = 16 * 1024                // 16 KB
listenerJitterTarget = 2500 * time.Millisecond
```

`listener.send()` queues audio then calls `catchUp()`, which *drops the oldest
queued audio* to hold a live listener within the jitter target. So of every
ten-second slug it kept about **2.5 seconds and discarded the rest** — heard as
a couple of seconds of audio followed by eight of silence, on repeat. Every
other internet station in that setup works because a normal Icecast stream
trickles bytes continuously and never lets the queue get deep enough to trim.

This also explains why cause 0's fix appeared to make things *worse*: the
burst-on-connect backlog quadrupled the burst size, so samo discarded even more
of each one, and every ffmpeg reconnect re-sent 40 seconds the listener had
already heard.

`StreamPacer` (in `extras/StreamPacer.cs`) fixes it. It is a virtual clock, one
per connection, that releases a small burst up front and then holds output to
real time. Per-segment rate comes from that segment's own byte count and its
playing time measured off the ADTS frame headers, so pacing follows the real
encoding rather than a nominal bitrate. The delay sits inside `IcyStreamWriter`'s
existing 16KB chunk loop, so the ICY metadata interval logic is untouched, and
each paced chunk is flushed — without the flush the chunks pile up in Kestrel's
output pipe and leave as one burst anyway.

`StreamPacer.Burst` defaults to 2s, deliberately under samo's 2500ms trim
threshold. Raising it past that reintroduces the dropouts.

Pacing is purely output-side. The rate segments are fetched from SiriusXM is
unchanged, and the producer keeps its segment buffer — that is what absorbs
SiriusXM's own jitter while the pacer keeps the wire smooth.

### 1. Listener identity was per-IP, not per-connection

`SXMListener` was a positional record on `IPAddress` alone, so record equality
was IP equality. Everything samo runs reaches this proxy from one address — the
docker bridge gateway — as four distinct clients:

| User-Agent | What it is |
| --- | --- |
| `Lavf/…` | ffmpeg, the actual playout |
| `samo-server/loudness` | loudness measurement, pulls flat out |
| `Samo Server/0.1 IcyProbe` | ICY metadata probe |
| `Samo Server/0.1` | stream fetch |

All four collapsed into a single `SXMListener`. `SegmentFanoutHub.Unregister`
matches on listener equality, so when `UpdateClientActivity` swept any one of
them as inactive it dropped **every** subscription from that IP — taking the
live playout off the air. In the logs that read as
`Removing inactive client 172.21.0.1 from HLS producer fanout` immediately
followed by a dead stream.

`SXMListener` now carries Kestrel's `ConnectionId`. Sibling connections are
distinct records and can no longer evict each other. Because client records are
now per-connection rather than per-IP, `UpdateClientActivity` also reaps
long-closed ones — otherwise the list would grow by one entry per reconnect
forever.

### 2. The shared producer died the instant its last subscriber left

A cold restart costs a fresh tune plus playlist fetch against SiriusXM, and
resyncs to the live edge (`lastMediaSequence` jumps to end-of-playlist minus
two), which is audible as a gap. ffmpeg reconnects within
`-reconnect_delay_max 5`, and samo's probe and loudness passes open and close
connections around the playout constantly, so the common case — a client that
comes straight back — paid full price every time, and each restart re-triggered
cause 1.

`HlsSegmentProducer` now lingers for 20s after its last subscriber leaves. A
reconnect inside that window re-attaches to the running producer: no re-tune, no
playlist fetch, no gap. The cost is that the producer keeps pulling segments
nobody is listening to for up to 20s, which is strictly less SiriusXM traffic
than the re-tune it avoids and looks like a listener who paused briefly.

### 3. Every connection re-tuned, and nothing rate-limited channel changes

`StreamIcecastAsync` called `GetStreamPlaylist(..., useCache: false)`
unconditionally, so even a same-channel reconnect to an already-running producer
burned a playlist request. It now skips that entirely when the requested channel
is already current and the producer is live.

A **minimum channel dwell** of 30s was also added. This is an account-safety
control, not a performance one: the proxy stays single-channel by design — one
subscription cannot plausibly be listening to two stations at once — and a
request to change channels inside the dwell window is refused with `429` plus
`Retry-After`, costing SiriusXM nothing. It exists so a misconfigured samo
schedule (two stations pointed at two GUIDs, alternating) cannot drive rapid
re-tuning, which is the single most flag-worthy pattern the old code produced.

Both decisions are made from local state before any SiriusXM call:
`GetCurrentChannelAsync` reads an in-memory field.

## Regenerating

Apply the patch to the checkout, edit, then diff it back out:

```bash
cd sxm-player && git apply ../extras/patches/samo-stability.patch
```

```bash
git -C sxm-player diff > extras/patches/samo-stability.patch && git -C sxm-player checkout -- .
```

Tests live in `extras/SamoStabilityTests.cs` and are a build gate. Run them the
way the Dockerfile does — `Category!=Local` excludes upstream's tests that talk
to the live SiriusXM API:

```bash
docker run --rm -v "$PWD/sxm-player":/src -w /src mcr.microsoft.com/dotnet/sdk:10.0 dotnet test sxmplayer.sln -c Release --filter "Category!=Local"
```
