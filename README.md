# sxm-proxy

A SiriusXM → Icecast bridge, deliberately siloed from `samo-server`.

Wraps [yob15662/sxm-player](https://github.com/yob15662/sxm-player) (Apache-2.0,
pinned at `470b35b`). It authenticates against SiriusXM's API with your own
subscriber credentials and re-exposes a channel as an Icecast stream with ICY
metadata — exactly the shape `samo-server`'s `internet-station` source consumes.

Nothing here is part of samo-server, and samo-server has no knowledge of it. The
only coupling is a URL.

## Why this repo builds from source

Upstream's `Dockerfile` copies a CI-built artifact (`.docker/app/`), so using it
means trusting a prebuilt binary from ghcr.io. Our `Dockerfile` is a multi-stage
build that compiles the checked-out source, so what runs is what we read.

The `sxm-player/` checkout stays pristine (and is gitignored). Our additions live
in `extras/SamoExtras.cs` and are injected with one anchored `sed` line at build
time — guarded, so if upstream moves the anchor the build fails loudly rather
than shipping an image silently missing `/channels` and `/ui`.

Changes that *edit* upstream logic rather than add to it ship as a diff in
`extras/patches/`, applied in the same build stage behind a `git apply --check`
guard. See [extras/patches/README.md](extras/patches/README.md) — currently one
patch, fixing the streaming stability problems described under
[One channel at a time](#one-channel-at-a-time) below.

## Credentials

Two files, both gitignored, both created by you:

```bash
printf 'SXM_USERNAME=you@example.com\n' > .env
```

```bash
printf 'your-password-here' > sxm_password.txt && chmod 600 sxm_password.txt
```

The password is mounted as a Docker secret at `/run/secrets/sxm_password` and
read into config at startup, so it never appears in `docker inspect`, the
container environment, or the process table. `deploy.sh` ships these separately
from the source bundle, never inside it.

## Run locally

```bash
docker compose up -d --build
```

Port defaults to **7717** (not 8080 — too commonly taken). Override with
`SXM_PORT` in `.env`.

## Deploy to the server

```bash
./deploy.sh
```

Knobs: `SXM_HOST` (default 192.168.1.10), `SXM_SSH_USER`, `SXM_PORT`,
`SXM_PROJECT_DIR` (default /opt/sxm-proxy). Safe to re-run — every run is a
rebuild and restart, and credentials already on the server are preserved.

It builds **on the server** rather than locally, because the server is x86_64
and dev machines here are arm64. Compiling remotely avoids cross-building.
It checks the port is free before compiling, reports (never modifies) ufw rules,
and waits for the container healthcheck before claiming success.

## Endpoints

| Route | Purpose |
| --- | --- |
| `GET /ui` | **Channel picker.** Filter, click, copy the stream URL |
| `GET /channels` | Lineup as JSON — id, name, number, description, genre |
| `GET /icecast/{guid}` | **The stream.** Send `Icy-MetaData: 1` for inline metadata |
| `GET /nowplaying` | Current track as JSON |
| `GET /cover.jpg` | Current cover art |
| `GET /` | Upstream's Blazor UI — **broken, use `/ui`** |

## Channel IDs are GUIDs, not names

`/icecast/{id}` matches on `Entity.Id` — the channel's GUID
(`MetadataService.SetCurrentChannelAsync`). Slugs like `purejazz` are NOT
accepted and fail with `Channel ... not found`. The only exception is the literal
`current`, meaning "whatever channel is already selected".

This is why `/ui` exists: click a channel, get the URL, never type a GUID.

## Browsing costs SiriusXM nothing

`GetChannelsAsync()` returns the cached `_allChannels` list once populated, so
the lineup is fetched **once per process lifetime** no matter how much anyone
browses. `/channels` and `/ui` therefore generate zero SiriusXM requests.

This is deliberate. Unattended, repetitive traffic is what gets an account
flagged, and the same subscription is the one in the car. For the same reason the
healthcheck hits `/channels` (cached, free) rather than pulling bytes from
`/icecast/` — a streaming probe would detect a dead session, but only by opening
a real stream against their servers every interval, forever. Not worth it. A dead
session instead surfaces as a failed stream in samo, which falls through to
rotation if the source is on a schedule rule.

## Verifying a channel streams

Verified working 2026-08-16. Grab a GUID from `/ui` —
`194adbca-34d6-cb94-b153-3488ee563308` is SiriusXM Hits 1.

```bash
curl -s -H 'Icy-MetaData: 1' --max-time 20 http://localhost:7717/icecast/194adbca-34d6-cb94-b153-3488ee563308 -o /tmp/sxm-probe.aac; file /tmp/sxm-probe.aac
```

Expect curl exit 28 (still streaming when the clock ran out — success for an
endless stream), roughly 630KB per 20s (~252kbps), and `file` reporting
`MPEG ADTS, AAC, v4 LC, 44.1 kHz, stereo`. A small file that `file` calls HTML is
an error page — read the body, don't trust the byte count.

Confirm ffmpeg — what samo actually uses — can consume it, with the same flags
`transcodeArgs` builds for a live network source:

```bash
ffmpeg -hide_banner -loglevel error -nostdin -rw_timeout 15000000 -reconnect 1 -reconnect_streamed 1 -reconnect_delay_max 5 -analyzeduration 3000000 -probesize 1000000 -i "http://localhost:7717/icecast/194adbca-34d6-cb94-b153-3488ee563308" -vn -ac 2 -ar 44100 -b:a 128k -c:a libmp3lame -f mp3 -t 10 /tmp/sxm-samo-test.mp3
```

## Tests

The image build runs the suite and fails if it goes red, so a broken change
cannot deploy. To run it by hand:

```bash
docker run --rm -v "$PWD/sxm-player":/src -w /src mcr.microsoft.com/dotnet/sdk:10.0 dotnet test sxmplayer.sln -c Release --filter "Category!=Local"
```

**`Category!=Local` is not optional.** Upstream's `LocalTests` authenticate
against the live SiriusXM API with real subscriber credentials. Running them
unattended on every build is precisely the traffic pattern this repo otherwise
works to avoid. Everything else — including `extras/SamoStabilityTests.cs`,
which covers the stability fixes — runs against fakes and opens no sockets.

Note the build must be `-c Release`: upstream regenerates the NSwag client in
`Debug`, which needs tooling the SDK image does not carry.

## Wiring into samo-server

Add as an `internet-station` source pointing at:

```
http://<server>:7717/icecast/<channel-guid>
```

samo's ICY probe (`internal/sources/icy.go`) picks up now-playing from the same
endpoint, so the channel gets real track info rather than a bare label. Give the
source `RoleShow` on a schedule rule rather than rotation — if the proxy dies,
the slot falls through instead of wedging the channel on a dead source.

## One channel at a time

**The proxy streams exactly one SiriusXM channel process-wide.** This is a
deliberate limit, not a missing feature. One subscription cannot plausibly be
playing two stations at once, and concurrent pulls on two channels is the
clearest possible signal to SiriusXM that this is not a person listening to the
radio.

Two consequences for samo:

- Point **one** source at the proxy at a time. A second source on a different
  GUID will take the channel away from the first.
- Channel changes are rate-limited to one per **30 seconds**. A change requested
  inside that window is refused with `429` and a `Retry-After` header, and costs
  SiriusXM nothing — the refusal is decided from local state. Legitimate
  schedule changes are far slower than this; the limit exists so a
  misconfiguration (two stations alternating between two GUIDs) cannot drive
  rapid re-tuning.

## Output is paced to real time — do not remove this

**This is the one that made samo work.** Everything else in this section is
supporting cast.

The proxy trickles bytes continuously, like any other Icecast station. It used
to write each segment to the socket the moment the segment arrived — the whole
~356KB, as fast as the socket would take it — and then go quiet for ten seconds.
The average bitrate was correct. Measured on the wire:

```
duration 60.0s  total 1890657 bytes = 252.1 kbps
t=5.6s  gap=5.56s     ← first audio 5.6s after connect
t=16.6s gap=11.08s
t=28.0s gap=11.35s
t=39.5s gap=11.04s
t=50.9s gap=11.44s
```

samo-server does not tolerate that. From its `internal/channels/streamer.go`:

```go
listenerBuffer       = 64                       // chunks
streamChunk          = 16 * 1024                // 16 KB
listenerJitterTarget = 2500 * time.Millisecond
```

`listener.send()` queues audio and then calls `catchUp()`, which **drops the
oldest queued audio** to hold a live listener within the jitter target. That is
correct behaviour for a radio server — a live listener should not drift behind —
and every other source in the setup is unaffected by it, because a normal
Icecast stream trickles and never lets the queue get deep enough to trim.

Ours arrived ten seconds at a time. samo kept 2.5 seconds of each slug and threw
away the other seven and a half. What came out of the speaker was a second or
two of audio, then eight seconds of silence, on repeat.

`StreamPacer` (`extras/StreamPacer.cs`) is a virtual clock, one per connection.
It releases a small burst up front and then holds output to real time. Segment
rate comes from that segment's own byte count divided by its playing time,
measured off the ADTS frame headers, so pacing follows the real encoding rather
than a nominal bitrate — running fast here would put the client back over the
trim threshold and bring the dropouts straight back.

Three things about it are load-bearing:

- **`StreamPacer.Burst` (2s) must stay under samo's 2500ms trim.** It is how far
  ahead of real time a client may run. Raise it past 2.5s and the dropouts
  return.
- **The delay lives inside `IcyStreamWriter`'s existing 16KB chunk loop**, so the
  ICY metadata interval — which is byte-exact and corrupts the stream if
  disturbed — is untouched.
- **Every paced chunk is flushed.** Without the flush the chunks pile up in
  Kestrel's output pipe and leave as one burst anyway, silently defeating the
  whole thing.

Pacing is output-side only. The rate segments are fetched from SiriusXM is
unchanged.

## Why the browser always worked

`/ui`'s preview played the same URL through the same endpoint and never
stuttered once, which made the proxy look innocent for a long time. A browser's
`<audio>` element buffers tens of seconds ahead and is happy to swallow a
ten-second slug whole. It has no jitter target and drops nothing.

So the working case and the broken case differed in the consumer, not the
stream. **A clean `/ui` preview proves the URL serves audio; it proves nothing
about whether samo can play it.**

## The stream carries ~40s of prebuffer

A fresh producer starts 4 segments back from the live edge rather than on the
newest one, and `SegmentFanoutHub` replays the same depth to any client joining
a stream already in flight. That buffer lives *inside* the proxy: it absorbs
SiriusXM's own jitter, while the pacer keeps the wire smooth. Without it the
producer holds one segment at a time and any late fetch upstream starves the
output.

The two have to be understood together. Before pacing existed, a deeper buffer
made things actively **worse** — a bigger buffer meant a bigger slug, and samo
still kept only 2.5 seconds of it. Buffer depth and delivery rate are separate
problems, and only fixing the second one made the first one useful.

## Listener identity and producer lifetime

Everything samo runs — ffmpeg, the ICY probe, the loudness pass — reaches the
proxy from one address, the docker bridge gateway. Listeners are therefore
identified per *connection* (Kestrel's `ConnectionId`) rather than per IP;
before that, retiring any one of those clients unregistered all of them and took
the live playout off the air with it.

The shared HLS producer also lingers 20s after its last subscriber leaves, so a
reconnect re-attaches instead of paying a fresh tune plus playlist fetch against
SiriusXM. Because reconnects are now free, samo's ICY probe and loudness pass
need no special configuration.

Both were real bugs with real measurements behind them, and fixing them changed
the logs a great deal and the audio not at all. Worth remembering the next time
this misbehaves: connection-level churn and byte-level delivery are different
failures, and the logs only show the first one.

See [extras/patches/README.md](extras/patches/README.md) for the full account of
all five.

## Known upstream bugs

All in `yob15662/sxm-player` at `470b35b`:

1. **The M3U exporter can kill the whole process.** It registers an `async`
   lambda with `lifetime.ApplicationStarted.Register()`, a sync-callback API, so
   exceptions inside are unobserved and fatal. Its startup call fetches 1000
   channels in one page, and SiriusXM's gateway intermittently returns
   `500 context deadline exceeded` on that. An optional feature therefore takes
   the proxy down. We keep it disabled via `LibraryM3UExportFolder: ""`.
2. **The web UI is inert.** `_framework/blazor.web.js` is absent from both the
   published output and the static asset manifest, so the Blazor circuit never
   starts and nothing responds to clicks. `/ui` replaces it.
3. **Wrong scoped-CSS reference.** `App.razor` asks for `MyApplication.styles.css`;
   the built file is `SXMPlayer.Proxy.styles.css`. Cosmetic.

The 500s from SiriusXM's own gateway are transient — the same call succeeded on
retry. Expected weather, not a broken build.

## Caveats

- Reverse-engineered against SiriusXM's private API. It breaks when they change
  auth, and it violates SiriusXM's terms of service.
- Requires an active SiriusXM subscription.
- `/channels` filters to `channel-linear` entities that aren't `unentitled`
  (434 of 712 raw items). It cannot filter off-air channels, so a handful of
  seasonal channels will appear and play silence. `offAir` could be recovered
  the same way `genre` was — see `extras/DecorationsGenre.cs` — if it ever
  becomes annoying enough to matter.
- `genre` is supplied by extending the generated `Decorations` partial record
  rather than regenerating the NSwag client; that file must be compiled into
  `SXMPlayer.Client`, not `SXMPlayer.Proxy`, because partial types cannot span
  assemblies.
- Upstream is a single-maintainer project (~6 stars). The pinned commit is what
  was verified; check the diff before moving.
