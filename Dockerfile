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
RUN mkdir -p /app/runtime-data && chown -R $APP_UID:$APP_UID /app/runtime-data
USER $APP_UID
ENTRYPOINT ["dotnet", "QuantDesk.Api.dll"]
