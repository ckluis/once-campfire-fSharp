# syntax = docker/dockerfile:1
#
# Production image for the F# port. A drop-in for the reference image (reference/Dockerfile), exactly as
# rust/Dockerfile is: same user (uid 1000), working directory, storage layout
# (/rails/storage/{db,files,backups}), env vars, ports and ONCE hooks. The binary does Thruster's job itself
# (src/Campfire.Kit/Front): HTTP on 80, and with TLS_DOMAIN, HTTPS on 443 with Let's Encrypt certificates
# cached in /rails/storage/thruster, where the reference's Thruster keeps them.
#
#   docker build -t campfire-fsharp:app --build-arg APP_VERSION=... --build-arg GIT_REVISION=... .
#
# Media: variants and video posters must be byte-identical to the reference's, so libvips and ffmpeg are built
# from the same Debian trixie source packages the reference image ships (libvips 8.16.1-1+deb13u1, ffmpeg
# 7:7.1.5-0+deb13u1), with the same compiler flags, against the same Debian libraries, and the same
# reductions (see rust/Dockerfile for what is left out: only what Campfire never reaches). The `media-base`,
# `vips`, `ffmpeg` and `dotnet` stages below are copies of the stages of the same names in Dockerfile.toolchain,
# instruction for instruction, so the two builds share their layers (and the storage vectors run against the
# very libraries that ship).
#
# The .NET app is published self-contained, not framework-dependent:
#   * the runtime that ships is the one the SDK in global.json built and tested the app with, not whichever
#     one a moving `aspnet` image tag happens to hold, and its version is in the image's own layers;
#   * the runtime stage stays `debian:trixie-slim` as in the reference and Rust images, so the libvips and
#     ffmpeg libraries are installed on the same base, and what .NET adds to it is small (libstdc++, zlib and
#     libssl, which ca-certificates brings; globalization is invariant, so no ICU);
#   * it costs about 70 MB of image over framework-dependent, which the media libraries dwarf anyway.
# ReadyToRun-compiles the app's assemblies (FSharp.Core included), which takes
# 5 MiB off the idle process and tens of milliseconds off the time to the first request (the JIT otherwise
# compiles the route table, the templates and SQLite's bindings on it; bench/results/campfire-app-boot.md has
# the numbers), and costs nothing once tiering has recompiled what is hot. Not trimmed: ASP.NET Core and
# Falco use reflection.

ARG DOTNET_VERSION=10.0.401
ARG DEBIAN_RELEASE=trixie
ARG LIBVIPS_VERSION=8.16.1-1+deb13u1
ARG LIBVIPS_DSC_SHA256=60205e00d061b9d8072938e04899f2ca2fdac0513068e561d33f7c87fae1ae2e
ARG FFMPEG_VERSION=7:7.1.5-0+deb13u1
ARG FFMPEG_DSC_SHA256=9ed2ed34cbe7f056eeebbe9045c5e2d15e41b5b053fe7c8ba6979a0b6fb081ce


# Toolchain and Debian -dev packages for libvips and ffmpeg, with deb-src enabled so
# `apt-get source` can fetch the exact Debian sources (the .dsc checksums pin them; dpkg-source
# verifies the tarballs against the .dsc).
FROM docker.io/library/buildpack-deps:${DEBIAN_RELEASE} AS media-base
RUN sed -i 's/^Types: deb$/Types: deb deb-src/' /etc/apt/sources.list.d/debian.sources && \
    apt-get update -qq && \
    apt-get install --no-install-recommends -y \
      dpkg-dev meson ninja-build nasm \
      libglib2.0-dev libexpat1-dev zlib1g-dev libjpeg62-turbo-dev libspng-dev libpng-dev \
      libwebp-dev libtiff-dev libheif-dev libexif-dev liblcms2-dev libcgif-dev libimagequant-dev \
      libhwy-dev libdav1d-dev libbz2-dev liblzma-dev
WORKDIR /usr/src


# libvips with only the loaders and savers above, built into the library (no modules), with
# Debian's build flags (debian/rules: meson, buildtype plain, hardening=+all).
FROM media-base AS vips
ARG LIBVIPS_VERSION
ARG LIBVIPS_DSC_SHA256
RUN apt-get source -qq vips=${LIBVIPS_VERSION} && \
    echo "${LIBVIPS_DSC_SHA256}  vips_${LIBVIPS_VERSION}.dsc" | sha256sum -c - && \
    cd vips-${LIBVIPS_VERSION%-*} && \
    eval "$(DEB_BUILD_MAINT_OPTIONS=hardening=+all dpkg-buildflags --export=sh)" && \
    meson setup build --buildtype=plain --wrap-mode=nodownload --prefix=/opt/vips --libdir=lib \
      --auto-features=disabled -Dmodules=disabled -Dintrospection=disabled -Dcplusplus=false \
      -Ddeprecated=false -Dexamples=false \
      -Djpeg=enabled -Dspng=enabled -Dpng=enabled -Dwebp=enabled -Dtiff=enabled -Dheif=enabled \
      -Dexif=enabled -Dlcms=enabled -Dcgif=enabled -Dimagequant=enabled -Dhighway=enabled \
      -Dzlib=enabled && \
    meson install -C build --strip


# ffmpeg and ffprobe (shared libavcodec/libavformat/...) from Debian's source with Debian's
# toolchain and version string (debian/rules), so the vectors' version check still applies.
FROM media-base AS ffmpeg
ARG FFMPEG_VERSION
ARG FFMPEG_DSC_SHA256
RUN apt-get source -qq ffmpeg=${FFMPEG_VERSION} && \
    upstream=${FFMPEG_VERSION#*:} && upstream=${upstream%-*} && revision=${FFMPEG_VERSION##*-} && \
    echo "${FFMPEG_DSC_SHA256}  ffmpeg_${FFMPEG_VERSION#*:}.dsc" | sha256sum -c - && \
    cd ffmpeg-${upstream} && \
    ./configure --prefix=/opt/ffmpeg --extra-version="${revision}" --toolchain=hardened \
      --enable-shared --disable-static --disable-doc --disable-ffplay --disable-avdevice \
      --disable-autodetect --disable-network --disable-hwaccels --disable-devices \
      --enable-libdav1d --enable-zlib --enable-bzlib --enable-lzma \
      --disable-encoders --enable-encoder=mjpeg \
      --disable-muxers --enable-muxer=image2 \
      --disable-protocols --enable-protocol=file,pipe \
      --disable-filters \
      --enable-filter=buffer,buffersink,abuffer,abuffersink,format,aformat,null,anull,scale,aresample \
      --enable-filter=select,loop,trim,transpose,hflip,vflip,rotate,crop && \
    make -j"$(nproc)" && \
    make install && \
    strip --strip-unneeded /opt/ffmpeg/lib/*.so.* /opt/ffmpeg/bin/*


# The .NET SDK global.json pins (10.0.401, rolling forward to later feature bands), installed
# the way Microsoft documents for Linux images that don't ship it.
FROM media-base AS dotnet
ARG DOTNET_VERSION
RUN curl -fsSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh && \
    bash /tmp/dotnet-install.sh --version ${DOTNET_VERSION} --install-dir /opt/dotnet && \
    rm /tmp/dotnet-install.sh


# Publish the app. NetVips loads the system libvips by its soname at run time, so nothing here links
# against the media libraries; src/Campfire.Assets digests and embeds the reference's assets and public/
# at build time (src/Campfire.Assets.Build), which is why reference/ is in the context. That tool runs from
# bin/Release/net10.0, where a build without a runtime identifier puts it; publishing with `-r` would
# build it under a runtime-specific directory, so it is built once without one first.
FROM media-base AS build
COPY --from=dotnet /opt/dotnet /opt/dotnet
ENV DOTNET_ROOT=/opt/dotnet \
    PATH=/opt/dotnet:$PATH \
    DOTNET_NOLOGO=1 \
    DOTNET_CLI_TELEMETRY_OPTOUT=1
ARG TARGETARCH
# --build-arg READY_TO_RUN=false publishes the IL alone (to measure what ReadyToRun buys).
ARG READY_TO_RUN=true

WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src src
COPY reference reference

RUN --mount=type=cache,id=campfire-fsharp-nuget,target=/root/.nuget/packages \
    case "$TARGETARCH" in \
      amd64) rid=linux-x64 ;; \
      arm64) rid=linux-arm64 ;; \
      *) echo "unsupported architecture: $TARGETARCH" >&2; exit 1 ;; \
    esac && \
    dotnet build src/Campfire.Assets.Build/Campfire.Assets.Build.fsproj -c Release && \
    dotnet publish src/Campfire.App/Campfire.App.fsproj -c Release -r "$rid" --self-contained true \
      -p:PublishReadyToRun=$READY_TO_RUN -p:DebugType=None -p:DebugSymbols=false -o /out/campfire


# The shared libraries and executables that go into the runtime, in one directory tree.
FROM scratch AS media
COPY --from=vips /opt/vips/lib/libvips.so.42 /usr/local/lib/
COPY --from=ffmpeg /opt/ffmpeg/lib/libavcodec.so.61 /opt/ffmpeg/lib/libavfilter.so.10 \
     /opt/ffmpeg/lib/libavformat.so.61 /opt/ffmpeg/lib/libavutil.so.59 \
     /opt/ffmpeg/lib/libswresample.so.5 /opt/ffmpeg/lib/libswscale.so.8 /usr/local/lib/
COPY --from=ffmpeg /opt/ffmpeg/bin/ffmpeg /opt/ffmpeg/bin/ffprobe /usr/local/bin/


FROM docker.io/library/debian:${DEBIAN_RELEASE}-slim

# ca-certificates: the system CA store, for webhooks, unfurling, Web Push and the ACME directory (and
# libssl, which it brings, for Kestrel's TLS). The rest are the Debian libraries libvips and ffmpeg were built
# against: glib and expat, the image codecs (with libde265 and dav1d as libheif's HEIC and AVIF decoders),
# lcms2, libexif, cgif and libimagequant for GIF saving, and highway for libvips' SIMD paths. libstdc++ and
# zlib are what the self-contained .NET runtime links (libheif and libtiff bring libstdc++ and zlib anyway,
# so they are listed to keep the runtime's needs from depending on them).
RUN apt-get update -qq && \
    apt-get install --no-install-recommends -y \
      ca-certificates libstdc++6 zlib1g libglib2.0-0t64 libexpat1 libjpeg62-turbo libspng0 libpng16-16t64 \
      libwebp7 libwebpmux3 libwebpdemux2 libtiff6 libheif1 libheif-plugin-libde265 \
      libheif-plugin-dav1d libexif12 liblcms2-2 libcgif0 libimagequant0 libhwy1t64 libdav1d7 && \
    rm -rf /var/lib/apt/lists /var/cache/apt/archives

COPY --from=media / /
RUN ldconfig

# Image metadata
ARG OCI_DESCRIPTION
LABEL org.opencontainers.image.description="${OCI_DESCRIPTION}"
ARG OCI_SOURCE
LABEL org.opencontainers.image.source="${OCI_SOURCE}"
LABEL org.opencontainers.image.licenses="MIT"

# Run and own only the runtime files as a non-root user, as the reference does.
RUN groupadd --system --gid 1000 rails && \
    useradd rails --uid 1000 --gid 1000 --create-home --shell /bin/bash

WORKDIR /rails

# The self-contained publish directory: the `campfire` executable, the runtime and the app's assemblies.
COPY --from=build /out/campfire /opt/campfire
RUN ln -s /opt/campfire/campfire /usr/local/bin/campfire

# bin/boot: what the reference's `thrust bin/start-app` did, in one process: HTTP_PORT (80) and,
# with TLS_DOMAIN, HTTPS_PORT (443), with the app itself also on TARGET_PORT (3000, loopback only unless TARGET_BIND says otherwise). Thruster's
# environment (HTTP_*_TIMEOUT, TLS_DOMAIN, ACME_DIRECTORY, CACHE_SIZE, ... and their THRUSTER_
# forms) means the same.
COPY --chmod=755 <<'EOF' /rails/bin/boot
#!/bin/sh
exec /usr/local/bin/campfire server
EOF

# The storage root is Rails.root.join("storage"): storage/db/<env>.sqlite3, storage/files,
# storage/backups.
RUN mkdir -p /rails/storage/db /rails/storage/files /rails/storage/backups && \
    chown -R 1000:1000 /rails

# ONCE backup/restore hooks. pre-backup is script/admin/prepare-backup (`campfire backup`);
# post-restore is the reference's own script.
COPY --chmod=755 <<'EOF' /hooks/pre-backup
#!/bin/bash
cd /rails
exec /usr/local/bin/campfire backup
EOF
COPY --chmod=755 reference/hooks/post-restore /hooks/post-restore

USER 1000:1000

# Configure environment defaults
ENV RAILS_ENV="production"
ENV HTTP_IDLE_TIMEOUT=60
ENV HTTP_READ_TIMEOUT=300
ENV HTTP_WRITE_TIMEOUT=300

# Set version and revision
ARG APP_VERSION
ENV APP_VERSION=$APP_VERSION
ARG GIT_REVISION
ENV GIT_REVISION=$GIT_REVISION

# Expose ports for HTTP and HTTPS
EXPOSE 80 443

# Start the server by default, this can be overwritten at runtime
CMD ["bin/boot"]
