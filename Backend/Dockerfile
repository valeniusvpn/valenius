# syntax=docker/dockerfile:1
# Build context is the repo root (Valenius/) so Shared/ is reachable.
# docker-compose.yml sets:  context: ..   dockerfile: Backend/Dockerfile

# ── Build stage ──────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore layer: copy project files first for cache efficiency
COPY Shared/Valenius.Shared/Valenius.Shared.csproj             Shared/Valenius.Shared/
COPY Backend/src/Valenius.Backend/Valenius.Backend.csproj      Backend/src/Valenius.Backend/
RUN dotnet restore Backend/src/Valenius.Backend/Valenius.Backend.csproj

# Source layer
COPY Shared/Valenius.Shared/        Shared/Valenius.Shared/
COPY Backend/src/Valenius.Backend/  Backend/src/Valenius.Backend/

RUN dotnet publish Backend/src/Valenius.Backend/Valenius.Backend.csproj \
    -c Release -o /app/publish --no-restore

# ── Runtime stage ─────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# libgssapi-krb5-2: Npgsql tries to load libgssapi_krb5.so.2 for GSSAPI/Kerberos
# support. Without it the library logs a non-fatal warning on every startup.
# Installing it silences the warning; password auth (what we use) works either way.
# (libkrb5-3 was previously used but only provides libkrb5.so.3, not libgssapi_krb5.so.2)
RUN apt-get update \
    && apt-get install -y --no-install-recommends libgssapi-krb5-2 \
    && rm -rf /var/lib/apt/lists/*

COPY --from=build /app/publish .

# /app/data  → versions.json + downloads/ (bind-mounted from host)
# /app/keys  → Data Protection key ring (named volume)
VOLUME ["/app/data", "/app/keys"]

EXPOSE 8080
# ASPNETCORE_HTTP_PORTS=8080 is already set by the base image; do not repeat it
# here to avoid the "Overriding HTTP_PORTS … Binding to URLS instead" warning.

ENTRYPOINT ["dotnet", "Valenius.Backend.dll"]
