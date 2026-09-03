# Build only the C# API and its production project references.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY global.json Directory.Build.props Directory.Packages.props QuantDesk.slnx ./
COPY src ./src

RUN dotnet restore src/QuantDesk.Api/QuantDesk.Api.csproj
RUN dotnet publish src/QuantDesk.Api/QuantDesk.Api.csproj \
    --configuration Release \
    --no-restore \
    --output /app/publish \
    --no-self-contained

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080

COPY --from=build /app/publish .
# Both directories are mounted as named volumes. Docker seeds a fresh volume from the image
# path including its ownership, so they must exist and belong to the app user *here*: created
# by the daemon at mount time instead, they arrive owned by root and the non-root process
# cannot write them. Replay recording degrades to disabled rather than crashing, so getting
# this wrong costs the section 22 gate silently.
RUN mkdir -p /app/runtime-data /app/replay-logs && chown -R $APP_UID:$APP_UID /app/runtime-data /app/replay-logs
USER $APP_UID
ENTRYPOINT ["dotnet", "QuantDesk.Api.dll"]
