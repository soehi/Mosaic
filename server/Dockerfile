# Build from the server directory:  docker build -t mosaic-server .
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
    MOSAIC_PUBLIC_URL=http://localhost:8080 \
    DOTNET_EnableDiagnostics=0

RUN mkdir -p /data && chown -R $APP_UID:$APP_UID /data

COPY --from=build /app .
COPY deploy/mosaic-mobile-0.3.5-release.apk /opt/mosaic-mobile/mosaic-mobile-0.3.5-release.apk
COPY deploy/release.json /opt/mosaic-mobile/release.json
USER $APP_UID

EXPOSE 8080
VOLUME ["/data"]

HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=3 \
    CMD ["dotnet", "Mosaic.Server.dll", "--healthcheck"]

ENTRYPOINT ["dotnet", "Mosaic.Server.dll"]
