# Build
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/FunkyMakerTransferServer/FunkyMakerTransferServer.csproj src/FunkyMakerTransferServer/
RUN dotnet restore src/FunkyMakerTransferServer/FunkyMakerTransferServer.csproj
COPY src/ src/
RUN dotnet publish src/FunkyMakerTransferServer/FunkyMakerTransferServer.csproj -c Release -o /app --no-restore

# Run
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_gcServer=0
# Render sets $PORT; Program.cs listens on it (falls back to 8080 locally).
ENV ASPNETCORE_HTTP_PORTS=8080
# The app runs as a non-root user and /app isn't writable: keep the leaderboard JSON in /data.
# (Ephemeral on Render's free plan; set MONGODB_URI for a persistent leaderboard.)
ENV FUNKYMAKER_DATA_DIR=/data
RUN mkdir -p /data && chown $APP_UID /data
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "FunkyMakerTransferServer.dll"]
