FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Project files first so restore is cached independently of source changes.
COPY src/Smurfm3u.Core/Smurfm3u.Core.csproj src/Smurfm3u.Core/
COPY src/Smurfm3u.Data/Smurfm3u.Data.csproj src/Smurfm3u.Data/
COPY src/Smurfm3u.App/Smurfm3u.App.csproj src/Smurfm3u.App/
RUN dotnet restore src/Smurfm3u.App/Smurfm3u.App.csproj

COPY src/ src/

# Deliberately NOT --no-restore. With it, publish omits the shared framework's static web
# assets, so wwwroot/_framework/blazor.web.js never lands in the image, no circuit is
# established and every interactive control silently does nothing. The restore above still
# warms the NuGet cache, so this second restore is cheap.
RUN dotnet publish src/Smurfm3u.App/Smurfm3u.App.csproj \
    -c Release \
    -o /app/publish

# Fail the build rather than ship a UI whose buttons do nothing.
RUN test -f /app/publish/wwwroot/_framework/blazor.web.js \
    || (echo "blazor.web.js missing from publish output" && exit 1)

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_gcServer=1

# curl is only here so the healthcheck below has something to probe with.
RUN apt-get update \
    && apt-get install --no-install-recommends -y curl \
    && rm -rf /var/lib/apt/lists/*

COPY --from=build /app/publish .

# Defaults matching the settings seeded on first run; mount volumes over them.
RUN mkdir -p /downloads/incomplete /downloads/complete /playlists /config

EXPOSE 8080

HEALTHCHECK --interval=30s --timeout=5s --start-period=40s --retries=3 \
    CMD curl --fail --silent http://localhost:8080/health || exit 1

ENTRYPOINT ["dotnet", "Smurfm3u.App.dll"]
