FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
WORKDIR /app
EXPOSE 8080

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY ["AChat.Api/AChat.Api.csproj", "AChat.Api/"]
COPY ["AChat.Core/AChat.Core.csproj", "AChat.Core/"]
COPY ["AChat.Infrastructure/AChat.Infrastructure.csproj", "AChat.Infrastructure/"]
RUN dotnet restore "AChat.Api/AChat.Api.csproj"
COPY . .
WORKDIR "/src/AChat.Api"
RUN dotnet build "AChat.Api.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "AChat.Api.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "AChat.Api.dll"]
