# Stage 1: Build frontend
FROM node:22-alpine AS frontend-build
WORKDIR /app/frontend
COPY frontend/package*.json ./
RUN npm ci
COPY frontend/ ./
RUN npm run build

# Stage 2: Build .NET app
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /app
COPY AChat.slnx ./
COPY src/ ./src/
COPY --from=frontend-build /app/src/AChat.Api/wwwroot ./src/AChat.Api/wwwroot/
RUN dotnet publish src/AChat.Api/AChat.Api.csproj -c Release -o /publish

# Stage 3: Runtime image
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /publish .
EXPOSE 8080
ENTRYPOINT ["dotnet", "AChat.Api.dll"]
