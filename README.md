# sxm-proxy

A SiriusXM → Icecast bridge. It authenticates against SiriusXM's API with your
own subscriber credentials and re-exposes a channel as an Icecast stream with
ICY metadata — exactly the shape
[samo-server](https://github.com/bouliehaan/samo-server)'s `internet-station`
source consumes.

Wraps [yob15662/sxm-player](https://github.com/yob15662/sxm-player) (Apache-2.0,
pinned at `470b35b`). Nothing here is part of samo-server and samo-server has no
knowledge of it; the only coupling is a URL.

**Requires an active SiriusXM subscription.** It is reverse-engineered against
their private API, it breaks when they change auth, and it violates their terms
of service.

## Install

Your SiriusXM credentials are the one thing this cannot work out for itself, so
they go on the same line. Nothing to download, nothing to edit:

```bash
read -rs SXM_PASSWORD && export SXM_PASSWORD          # keeps it out of your shell history
SXM_USERNAME=you@example.com docker compose -f oci://ghcr.io/bouliehaan/sxm-proxy:compose up -d
```

Then open `http://<this machine>:7717/ui`, pick a channel, copy its stream URL.

The password is mounted as a secret **file**, not an environment variable, so it
never appears in `docker inspect`, the container's environment, or the process
table. The port is **7717** (not 8080 — too commonly taken); override with
`SXM_PORT` on the same line.

The image is `linux/amd64` only, because upstream's .NET build is. It will not
run on an arm64 host.

Building it yourself instead of pulling: the Dockerfile compiles from the pinned
upstream checkout, so fetch that first or the build fails.

```bash
git clone https://github.com/yob15662/sxm-player.git sxm-player
git -C sxm-player checkout 470b35b44de00514f1fd626b06c56367695c6efc
docker compose -f docker-compose.yml -f docker-compose.build.yml up -d --build
```

## Endpoints

| Route | Purpose |
| --- | --- |
| `GET /ui` | **Channel picker.** Filter, click, copy the stream URL |
| `GET /channels` | Lineup as JSON — id, name, number, description, genre |
| `GET /icecast/{guid}` | **The stream.** Send `Icy-MetaData: 1` for inline metadata |
| `GET /nowplaying` | Current track as JSON |
| `GET /cover.jpg` | Current cover art |
| `GET /` | Upstream's Blazor UI — **broken, use `/ui`** |

**Channel ids are GUIDs, not names.** `/icecast/{id}` matches on the channel's
GUID; slugs like `purejazz` fail with `Channel ... not found`. The only
exception is the literal `current`, meaning whatever channel is already
selected. That is why `/ui` exists — click a channel, get the URL, never type a
GUID.

## Wiring into samo-server

Add an `internet-station` source pointing at:

```
http://<server>:7717/icecast/<channel-guid>
```

samo's ICY probe picks up now-playing from the same endpoint, so the channel
gets real track info rather than a bare label. Give the source `RoleShow` on a
schedule rule rather than rotation — if the proxy dies, the slot falls through
instead of wedging the channel on a dead source.

**The proxy streams exactly one SiriusXM channel process-wide.** A deliberate
limit, not a missing feature: one subscription cannot plausibly be playing two
stations at once, and concurrent pulls on two channels is the clearest possible
signal to SiriusXM that this is not a person listening to the radio. So point
**one** source at it at a time, and expect channel changes to be rate-limited to
one per 30 seconds (`429` plus `Retry-After`, decided from local state, costing
SiriusXM nothing).

## Tests

The image build runs the suite and fails if it goes red, so a broken change
cannot deploy. By hand:

```bash
docker run --rm -v "$PWD/sxm-player":/src -w /src mcr.microsoft.com/dotnet/sdk:10.0 \
  dotnet test sxmplayer.sln -c Release --filter "Category!=Local"
```

**`Category!=Local` is not optional.** Upstream's `LocalTests` authenticate
against the live SiriusXM API with real credentials, and running them unattended
on every build is precisely the traffic pattern this repo works to avoid.

## Caveats

- `/channels` filters to `channel-linear` entities that aren't `unentitled`
  (434 of 712 raw items). It cannot filter off-air channels, so a handful of
  seasonal ones appear and play silence.
- `genre` is supplied by extending the generated `Decorations` partial record
  rather than regenerating the NSwag client; that file must be compiled into
  `SXMPlayer.Client`, not `SXMPlayer.Proxy`, because partial types cannot span
  assemblies.
- Upstream is a single-maintainer project (~6 stars). The pinned commit is what
  was verified; check the diff before moving.

## Design notes

[docs/DESIGN.md](docs/DESIGN.md) — why the image compiles upstream's source
rather than repackaging its prebuilt artifact, why browsing costs SiriusXM
nothing, how to verify a channel actually streams, why the output is paced to
real time and must stay that way, and the known upstream bugs.

## License

Apache-2.0, the same as [yob15662/sxm-player](https://github.com/yob15662/sxm-player),
which this builds on. See [LICENSE](LICENSE), and [NOTICE](NOTICE) for the
statement of changes that Apache-2.0 §4(b) asks for. Upstream is not vendored —
it is cloned at build time and the checkout is gitignored.
