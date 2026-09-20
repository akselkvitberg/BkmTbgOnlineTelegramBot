FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY EventPhotoBot.slnx ./
COPY src/EventPhotoBot/EventPhotoBot.csproj src/EventPhotoBot/
COPY tests/EventPhotoBot.Tests/EventPhotoBot.Tests.csproj tests/EventPhotoBot.Tests/
RUN dotnet restore

COPY . .
RUN dotnet publish src/EventPhotoBot/EventPhotoBot.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app .

# Cloud Run supplies PORT; Program.cs reads it and binds accordingly, defaulting
# to 8080 only when PORT is unset (local runs). This EXPOSE is documentation of
# that default — Cloud Run's own routing does not consult it — and must not be
# the thing that decides what the app binds.
EXPOSE 8080

# Run as a non-root user. The app writes nothing to the filesystem — all state
# and all bytes live in the bucket — so there is nothing to grant write access to.
USER $APP_UID

ENTRYPOINT ["dotnet", "EventPhotoBot.dll"]
