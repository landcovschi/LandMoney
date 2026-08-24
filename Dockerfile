# One image: the .NET API and the built React client, served from one origin by
# one process. That is #20's decision arriving at its destination -- there is no
# nginx container here and no CORS, because from a browser's point of view there
# is only ever one server.
#
# Three stages, and the rule that shapes all of them: manifests first, restore
# second, source third. A layer is keyed by the previous layer plus the bytes
# the instruction copies, so a COPY of the source above the restore makes every
# edit re-download every dependency. It is the same separation ci.yml already
# makes between `dotnet restore` and `dotnet build --no-restore`; here it is
# structural rather than a matter of step order.


# ----------------------------------------------------------------------------
# Stage 1: the client. Discarded -- only wwwroot leaves here.
# ----------------------------------------------------------------------------

# slim and not alpine. Both work: package-lock.json carries the musl bindings as
# well as the gnu ones for all three native dependencies this client has
# (@rolldown/binding-linux-x64, @oxlint/binding-linux-x64, lightningcss), which
# was checked rather than hoped. slim wins on matching the SDK stage's libc, and
# the ~80 MB it costs never reaches the final image because this stage is thrown
# away.
#
# The `24` is written twice in this repository -- here and in
# src/landmoney.client/.nvmrc, which is what ci.yml reads. That is exactly the
# drift CLAUDE.md warns about, and it has no clean fix: FROM cannot read a file,
# and an ARG would declare the duplication without removing it. The .npmrc sets
# engine-strict against `"node": ">=24.0.0 <25"`, so a wrong major here fails at
# `npm ci` with a message naming the version -- which is the guard rail, and the
# reason this is tolerable rather than merely unfixed.
FROM node:24-slim AS client

# Not a free choice. vite.config.ts writes to '../LandMoney.Web/wwwroot' -- a
# path relative to the client folder -- so the client's config knows the
# repository's layout. That is the price #20 accepted in writing, and this is
# the first place it is charged. Reproducing the layout keeps the build output
# where the config expects it; a bare /client would put wwwroot at the image
# root, which works by accident and reads as a mistake.
WORKDIR /src/src/landmoney.client

# The two manifests alone, so that everything below `npm ci` is reused until a
# dependency actually changes.
COPY src/landmoney.client/package.json src/landmoney.client/package-lock.json ./

# `ci` and not `install`, for the reason ci.yml already gives: it installs
# exactly the lock file and fails when package.json disagrees, where `install`
# rewrites the lock and makes the build depend on when it last ran.
RUN npm ci

COPY src/landmoney.client/ ./

# `tsc -b && vite build`. Type errors therefore fail the image build, which is
# wanted. Lint does not run here: `npm run lint` is CI's job, and an image build
# that fails on a style rule fails for a reason that has nothing to do with
# whether the thing runs.
RUN npm run build


# ----------------------------------------------------------------------------
# Stage 2: publish. Also discarded -- only /app/publish leaves here.
# ----------------------------------------------------------------------------

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Both of these are load-bearing and both fail quietly if forgotten.
#
# global.json pins the SDK to 10.0.400 with rollForward: latestFeature. The
# sdk:10.0 tag resolves to 10.0.400 today -- checked against the registry's tag
# list, not assumed -- so there is nothing to roll and the pin costs nothing.
# Without this file the image would silently build on whatever band the tag
# happens to carry, which is a different compiler from this machine's with
# nothing reporting the difference. The failure mode in the other direction is
# worth recognising in advance: if the tag ever sat below 10.0.400 the error is
# "A compatible .NET SDK was not found", which reads like a broken Dockerfile
# rather than a version rule doing its job.
#
# NuGet.config carries <clear/>. Without it restore falls back to the image's
# default nuget.org -- which works, and that is the problem: the machine, CI and
# the image would be resolving from different places while all three stay green,
# and the day that matters is the day a feed is added.
COPY global.json NuGet.config ./

# The project file alone. Restore is the expensive layer and it depends on this
# and nothing else.
#
# The web project only, and deliberately not LandMoney.slnx -- which is the
# opposite of the call ci.yml makes one folder away, for the opposite reason.
# There, `dotnet test --no-build` needs the test project built, so the solution
# is the right argument. Here the test project would be restored, compiled and
# thrown away: the image is not where tests run.
COPY src/LandMoney.Web/LandMoney.Web.csproj src/LandMoney.Web/
RUN dotnet restore src/LandMoney.Web/LandMoney.Web.csproj

COPY src/LandMoney.Web/ src/LandMoney.Web/

# The order of these two lines is the whole point of the file, and getting it
# wrong is silent. wwwroot is build output: it is git-ignored, absent from a
# clone, and excluded from the build context by .dockerignore. So a publish that
# ran before this COPY would succeed, produce an image that starts, and answer
# every API request correctly -- with `/` a 404 and nothing in any log saying
# why. docs/roadmap.md flags this for #23; it is the same failure that made #20
# pick UseStaticFiles over MapStaticAssets, entered through a different door.
#
# The insurance is not this comment. It is requesting `/` after the build and
# asserting a 200, rather than trusting that the stages ran in the order they
# are written in.
COPY --from=client /src/src/LandMoney.Web/wwwroot src/LandMoney.Web/wwwroot

# -c Release is the default for `dotnet publish` since .NET 8 and is written out
# anyway: ci.yml says it on both build and test because those two have to agree,
# and a reader comparing the two files should not have to know which commands
# default to what.
RUN dotnet publish src/LandMoney.Web/LandMoney.Web.csproj \
    --no-restore \
    -c Release \
    -o /app/publish


# ----------------------------------------------------------------------------
# Stage 3: what actually ships. No SDK, no node_modules, no source.
# ----------------------------------------------------------------------------

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app

COPY --from=build /app/publish ./

# Documentation, not a rule -- EXPOSE publishes nothing and binds nothing. 8080
# because that is what the aspnet image sets ASPNETCORE_HTTP_PORTS to, and it
# sets it because of the line below: ports under 1024 need CAP_NET_BIND_SERVICE,
# so a non-root process cannot have port 80. .NET 8 moved the default rather
# than asking every image to grant the capability back.
EXPOSE 8080

# APP_UID is defined by the base image (1654, user `app`). Everything above this
# line runs as root and everything after it does not, which is why it sits below
# the COPY: the published files are owned by root and read by app, and nothing
# in this application writes to its own directory.
#
# No development certificate is installed and none is wanted. The container
# speaks plain HTTP; Container Apps terminates TLS in slice 3.
USER $APP_UID

# The .dll and not the apphost. Both are in the publish output; naming the dll
# is the form that does not care whether UseAppHost is on.
ENTRYPOINT ["dotnet", "LandMoney.Web.dll"]
