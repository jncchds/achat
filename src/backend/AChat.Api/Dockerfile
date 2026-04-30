# Stage 1: Build frontend
FROM node:22-alpine AS node-build
WORKDIR /frontend
COPY src/frontend/achat-web/package*.json ./
RUN npm ci
COPY src/frontend/achat-web/ .
RUN npm run build

# Stage 2: Build & publish .NET API
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/backend/AChat.Api/AChat.Api.csproj AChat.Api/
COPY src/backend/AChat.Core/AChat.Core.csproj AChat.Core/
COPY src/backend/AChat.Infrastructure/AChat.Infrastructure.csproj AChat.Infrastructure/
COPY src/backend/AChat.Worker/AChat.Worker.csproj AChat.Worker/
RUN dotnet restore AChat.Api/AChat.Api.csproj
COPY src/backend/ .
WORKDIR /src/AChat.Api
RUN dotnet publish AChat.Api.csproj -c Release -o /app/publish /p:UseAppHost=false

# Stage 3: Runtime
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
EXPOSE 8080
COPY --from=build /app/publish .
COPY --from=node-build /frontend/dist ./wwwroot
ENTRYPOINT ["dotnet", "AChat.Api.dll"]
