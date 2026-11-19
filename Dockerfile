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
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "FunkyMakerTransferServer.dll"]
