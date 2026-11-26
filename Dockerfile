# Stage 1: Build & Publish
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY src/GuildHub.Core/GuildHub.Core.csproj src/GuildHub.Core/
COPY src/GuildHub.Bot/GuildHub.Bot.csproj src/GuildHub.Bot/
RUN dotnet restore src/GuildHub.Bot/GuildHub.Bot.csproj

COPY src/GuildHub.Core/ src/GuildHub.Core/
COPY src/GuildHub.Bot/ src/GuildHub.Bot/
COPY Icon/ Icon/

RUN dotnet publish src/GuildHub.Bot/GuildHub.Bot.csproj -c Release -o /app/publish --no-restore

# Stage 2: Runtime
FROM mcr.microsoft.com/dotnet/runtime:8.0 AS final
WORKDIR /app

ENV TZ=Asia/Ho_Chi_Minh \
    DOTNET_ENVIRONMENT=Production

COPY --from=build /app/publish .

ENTRYPOINT ["dotnet", "GuildHub.Bot.dll"]
