FROM node:24-alpine AS client
WORKDIR /src
COPY src/IncidentBot.Client/package.json src/IncidentBot.Client/package-lock.json src/IncidentBot.Client/
RUN npm ci --prefix src/IncidentBot.Client
COPY src/IncidentBot.Client src/IncidentBot.Client
RUN npm run build --prefix src/IncidentBot.Client

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json IncidentBot.sln ./
COPY src/IncidentBot.Api/IncidentBot.Api.csproj src/IncidentBot.Api/
COPY src/IncidentBot.Contracts/IncidentBot.Contracts.csproj src/IncidentBot.Contracts/
RUN dotnet restore src/IncidentBot.Api/IncidentBot.Api.csproj
COPY src/IncidentBot.Api src/IncidentBot.Api
COPY src/IncidentBot.Contracts src/IncidentBot.Contracts
COPY config config
COPY --from=client /src/src/IncidentBot.Api/wwwroot src/IncidentBot.Api/wwwroot
RUN dotnet publish src/IncidentBot.Api/IncidentBot.Api.csproj -c Release -o /app/publish \
    -p:BuildClient=false \
    -p:GenerateTypeScriptContracts=false \
    -p:OpenApiGenerateDocuments=false \
    --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl libgssapi-krb5-2 \
    && rm -rf /var/lib/apt/lists/*
COPY --from=build /app/publish .
EXPOSE 8080
HEALTHCHECK --interval=10s --timeout=3s --retries=6 \
    CMD curl --fail --silent --show-error http://127.0.0.1:8080/health/ready >/dev/null || exit 1
USER $APP_UID
ENTRYPOINT ["dotnet", "IncidentBot.Api.dll"]
