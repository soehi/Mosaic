# Root-level build wrapper for Coolify (build context = repository root).
# For standalone builds use server/Dockerfile with server/ as the context.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY server/Directory.Build.props server/Directory.Packages.props server/NuGet.config ./
COPY server/src/Mosaic.Server/Mosaic.Server.csproj src/Mosaic.Server/
RUN dotnet restore src/Mosaic.Server/Mosaic.Server.csproj

COPY server/src/ src/
RUN dotnet publish src/Mosaic.Server/Mosaic.Server.csproj -c Release -o /app --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

ENV ASPNETCORE_URLS=http://0.0.0.0:8080 \
    MOSAIC_DATA_DIR=/data \
    DOTNET_RUNNING_IN_CONTAINER=true

RUN useradd --uid 1654 --gid 0 --home-dir /app --no-create-home app && mkdir -p /data && chown -R 1654:0 /data

COPY --from=build /app .
USER 1654:0

EXPOSE 8080
VOLUME ["/data"]

HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=3 \
    CMD ["dotnet", "Mosaic.Server.dll", "--healthcheck"]

ENTRYPOINT ["dotnet", "Mosaic.Server.dll"]
