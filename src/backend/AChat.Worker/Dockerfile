FROM mcr.microsoft.com/dotnet/runtime:10.0 AS base
WORKDIR /app

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY ["AChat.Worker/AChat.Worker.csproj", "AChat.Worker/"]
COPY ["AChat.Core/AChat.Core.csproj", "AChat.Core/"]
COPY ["AChat.Infrastructure/AChat.Infrastructure.csproj", "AChat.Infrastructure/"]
RUN dotnet restore "AChat.Worker/AChat.Worker.csproj"
COPY . .
WORKDIR "/src/AChat.Worker"
RUN dotnet build "AChat.Worker.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "AChat.Worker.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "AChat.Worker.dll"]
