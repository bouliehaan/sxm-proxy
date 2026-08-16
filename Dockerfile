# Multi-stage build from source.
#
# Upstream's own Dockerfile only COPYs a CI-built artifact (.docker/app/), so
# using it means trusting a prebuilt binary from ghcr.io. This builds the same
# code from the checked-out source instead, so what runs is what we read.
#
# Build context is the sxm-proxy directory (the parent), not the clone, so the
# sxm-player/ checkout stays pristine and `git pull` never conflicts.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore first against just the project files so dependency layers cache
# across source edits.
COPY sxm-player/sxmplayer.sln ./
COPY sxm-player/Directory.Build.props ./
COPY sxm-player/SXMPlayer.Client/SXMPlayer.csproj SXMPlayer.Client/
COPY sxm-player/SXMPlayer.Proxy/SXMPlayer.Proxy.csproj SXMPlayer.Proxy/
COPY sxm-player/SXMPlayer.Tests/SXMPlayer.Tests.csproj SXMPlayer.Tests/
RUN dotnet restore sxmplayer.sln

COPY sxm-player/ ./

# Our additions (see extras/SamoExtras.cs): a /channels JSON API and a working
# /ui, injected as one call rather than a diff against upstream's source. A
# single anchored line survives upstream drift far better than a patch file.
#
# The grep guard is the point: if upstream ever renames or moves this line, the
# build FAILS here rather than silently producing an image without /channels
# and /ui, which would look fine until someone opened the UI.
COPY extras/SamoExtras.cs SXMPlayer.Proxy/
# Goes into SXMPlayer.Client, not the Proxy: it completes the generated
# `partial record Decorations`, and partial types cannot span assemblies.
COPY extras/DecorationsGenre.cs SXMPlayer.Client/
# Output pacing (see extras/patches/README.md, cause 4). A new file, so it needs
# no patch — the diff only wires it into the existing write path.
COPY extras/StreamPacer.cs SXMPlayer.Client/
RUN grep -q '^app\.MapStaticAssets();' SXMPlayer.Proxy/Program.cs \
      || { echo "PATCH ANCHOR MISSING: 'app.MapStaticAssets();' not found in Program.cs — upstream changed, update Dockerfile"; exit 1; } \
 && sed -i 's|^app\.MapStaticAssets();|app.MapSamoExtras(sxm);\napp.MapStaticAssets();|' SXMPlayer.Proxy/Program.cs \
 && grep -q 'app\.MapSamoExtras(sxm);' SXMPlayer.Proxy/Program.cs \
      || { echo "PATCH INJECTION FAILED"; exit 1; }

# The streaming-stability fixes (see extras/patches/README.md). Unlike the
# additions above these are edits to upstream logic — per-connection listener
# identity, the shared producer's linger, and the channel-dwell guard — so they
# ship as a diff rather than an injected call.
#
# --check first for the same reason the grep guard exists above: if upstream
# drifts, the build must FAIL here rather than quietly produce an image that
# streams but still cuts out.
COPY extras/patches/ patches/
RUN git apply --check -p1 patches/samo-stability.patch \
      || { echo "PATCH DOES NOT APPLY: extras/patches/samo-stability.patch no longer matches upstream — regenerate it against the pinned commit"; exit 1; } \
 && git apply -p1 patches/samo-stability.patch \
 && rm -rf patches

# Tests for those fixes. Lives here rather than in the checkout because it is a
# new file, so it needs no patch — same arrangement as SamoExtras.cs.
COPY extras/SamoStabilityTests.cs SXMPlayer.Tests/

# Build gate. Category!=Local is NOT optional: upstream's LocalTests talk to the
# live SiriusXM API with real subscriber credentials, and running them on every
# image build is exactly the unattended, repetitive traffic that gets an account
# flagged. Everything that does run here is backed by fakes and opens no
# sockets.
RUN dotnet test sxmplayer.sln -c Release --no-restore --filter "Category!=Local" --nologo

RUN dotnet publish -c Release --no-restore -o /app SXMPlayer.Proxy/SXMPlayer.Proxy.csproj

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app

# curl purely so the container can health-check itself; the runtime image ships
# without it.
RUN apt-get update \
 && apt-get install -y --no-install-recommends curl \
 && rm -rf /var/lib/apt/lists/*

COPY --from=build /app/ ./

# Not 8080: too commonly taken on a general-purpose server. Overridable, but the
# default is deliberately in a quiet range.
EXPOSE 7717
ENV ASPNETCORE_URLS=http://0.0.0.0:7717
ENTRYPOINT ["dotnet", "SXMPlayer.Proxy.dll"]
