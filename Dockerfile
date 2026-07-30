FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY . .
RUN dotnet restore src/Snet.Edge.WebApi/Snet.Edge.WebApi.csproj
RUN dotnet publish src/Snet.Edge.WebApi/Snet.Edge.WebApi.csproj \
    --configuration Release \
    --no-restore \
    --output /app/publish \
    /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
RUN apt-get update \
    && apt-get install --yes --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/* \
    && mkdir -p /var/lib/snet-edge \
    && chown -R app:app /var/lib/snet-edge

WORKDIR /app
COPY --from=build --chown=app:app /app/publish .

USER app
ENV ASPNETCORE_URLS=http://+:8080 \
    Gateway__DataDirectory=/var/lib/snet-edge \
    DOTNET_EnableDiagnostics=0
EXPOSE 8080
VOLUME ["/var/lib/snet-edge"]
HEALTHCHECK --interval=15s --timeout=3s --start-period=30s --retries=4 \
    CMD curl --fail --silent --show-error http://127.0.0.1:8080/health/ready || exit 1

ENTRYPOINT ["dotnet", "Snet.Edge.WebApi.dll"]
