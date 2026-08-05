FROM node:24-alpine AS client
WORKDIR /src
COPY src/Panko.Client/package.json src/Panko.Client/package-lock.json src/Panko.Client/
RUN npm ci --prefix src/Panko.Client
COPY src/Panko.Client src/Panko.Client
RUN npm run build --prefix src/Panko.Client

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json Panko.sln ./
COPY src/Panko.Api/Panko.Api.csproj src/Panko.Api/
COPY src/Panko.Contracts/Panko.Contracts.csproj src/Panko.Contracts/
COPY src/Panko.Kafka/Panko.Kafka.csproj src/Panko.Kafka/
COPY src/Panko.Observability/Panko.Observability.csproj src/Panko.Observability/
RUN dotnet restore src/Panko.Api/Panko.Api.csproj
COPY src/Panko.Api src/Panko.Api
COPY src/Panko.Contracts src/Panko.Contracts
COPY src/Panko.Kafka src/Panko.Kafka
COPY src/Panko.Observability src/Panko.Observability
COPY config config
COPY --from=client /src/src/Panko.Api/wwwroot src/Panko.Api/wwwroot
RUN dotnet publish src/Panko.Api/Panko.Api.csproj -c Release -o /app/publish \
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
ENTRYPOINT ["dotnet", "Panko.Api.dll"]
