# ── Stage 1: Build React UI ───────────────────────────────────────────────────
FROM node:20-alpine AS ui-build
WORKDIR /ui
COPY src/achat-ui/package*.json ./
RUN npm ci
COPY src/achat-ui/ ./
COPY VERSION /VERSION
# Build to ./dist (the dev outDir points at the API wwwroot; override here)
RUN npx vite build --outDir ./dist --emptyOutDir

# ── Stage 2: Build .NET API ────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS api-build
WORKDIR /src

# Copy solution and project files first (layer caching)
COPY AChat.slnx ./
COPY src/AChat.Core/AChat.Core.csproj src/AChat.Core/
COPY src/AChat.Infrastructure/AChat.Infrastructure.csproj src/AChat.Infrastructure/
COPY src/AChat.Api/AChat.Api.csproj src/AChat.Api/
COPY src/AChat.Tests/AChat.Tests.csproj src/AChat.Tests/
RUN dotnet restore src/AChat.Api/AChat.Api.csproj

# Copy source and build
COPY src/ src/
RUN dotnet publish src/AChat.Api/AChat.Api.csproj -c Release -o /app/publish --no-restore

# ── Stage 3: Runtime ───────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=api-build /app/publish ./

# Copy React build output into wwwroot
COPY --from=ui-build /ui/dist ./wwwroot

EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
ENTRYPOINT ["dotnet", "AChat.Api.dll"]
