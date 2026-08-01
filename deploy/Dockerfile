# Dumb Luck Rides — the server image (§9).
#
# Two stages. The build stage needs the SDK, a git history for SourceLink and every project in the
# solution; the runtime stage needs none of those, and shipping them would put the source, the
# NuGet cache and the SDK's own toolchain into an image that faces the internet.
#
# Built from the repository root:  docker build -f deploy/Dockerfile .

# ---------------------------------------------------------------------------
# Build
# ---------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0-noble AS build

WORKDIR /src

# Restore before the source is copied, so a change to a .cs file does not invalidate the layer
# holding several hundred megabytes of packages. The two Directory.Build files come first because
# MSBuild walks up the tree to find them and a restore without them resolves a different graph.
COPY Directory.Build.props Directory.Build.targets global.json ./
COPY Web/DLR.sln Web/
COPY Web/src/DLR.Core/DLR.Core.csproj Web/src/DLR.Core/
COPY Web/src/DLR.Server/DLR.Server.csproj Web/src/DLR.Server/
COPY Web/src/DLR.Server.Migrations/DLR.Server.Migrations.csproj Web/src/DLR.Server.Migrations/
COPY Web/tests/DLR.Core.Tests/DLR.Core.Tests.csproj Web/tests/DLR.Core.Tests/
COPY Web/tests/DLR.Server.Tests/DLR.Server.Tests.csproj Web/tests/DLR.Server.Tests/
COPY Web/tests/DLR.TestSupport/DLR.TestSupport.csproj Web/tests/DLR.TestSupport/
COPY Web/tests/DLR.Architecture.Tests/DLR.Architecture.Tests.csproj Web/tests/DLR.Architecture.Tests/

RUN dotnet restore Web/src/DLR.Server/DLR.Server.csproj

COPY . .

# The commit is embedded by SourceLink from the .git directory, and /api/v1/about reads it back off
# AssemblyInformationalVersion (§14.6.2). A build with no git history produces a server that cannot
# state which commit it is, which is an AGPL §13 obligation rather than a nicety — so the .git
# directory is deliberately *not* in .dockerignore.
RUN dotnet publish Web/src/DLR.Server/DLR.Server.csproj \
	--configuration Release \
	--no-restore \
	--output /app

# ---------------------------------------------------------------------------
# Runtime
# ---------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble AS runtime

# Two packages, both load-bearing. SkiaSharp's native library needs fontconfig even for an
# application that draws no text (§16.4) — discovering that in production costs one broken photo
# upload and a confusing stack trace. curl is what HEALTHCHECK below runs; the aspnet image ships
# neither, and a HEALTHCHECK whose command does not exist reports unhealthy forever.
RUN apt-get update \
	&& apt-get install --yes --no-install-recommends libfontconfig1 curl \
	&& rm --recursive --force /var/lib/apt/lists/*

WORKDIR /app

COPY --from=build /app .

# Non-root, and it owns nothing it does not need to write. Not --system: that flag picks a uid
# below SYS_UID_MAX and warns when one is given explicitly, and an explicit uid is what makes the
# blob volume's ownership predictable across a rebuild.
RUN useradd --uid 5000 --create-home --shell /usr/sbin/nologin dlr

# The blob volume's mount point has to exist in the image, owned by the user that will write to it.
# Docker seeds a *fresh* named volume from the image path — ownership included — and creates it
# root-owned when the path is absent. That is a container which starts, answers /healthz, and then
# fails every upload with a permission error nobody connects to this line.
RUN mkdir -p /blobs && chown dlr:dlr /blobs

USER dlr

ENV ASPNETCORE_HTTP_PORTS=8080 \
	DOTNET_gcServer=0

EXPOSE 8080

# Caddy terminates TLS and forwards over the compose network, so the container speaks plain HTTP.
# The health endpoint judges the schema and the disk as well as the process (§9, §9.1), which is
# what makes it worth wiring into the container rather than only into an external pinger.
HEALTHCHECK --interval=60s --timeout=5s --start-period=30s --retries=3 \
	CMD ["/usr/bin/curl", "--fail", "--silent", "http://127.0.0.1:8080/healthz"]

ENTRYPOINT ["dotnet", "DLR.Server.dll"]
