From mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source

COPY global.json Directory.Build.props ./
COPY src/WindowWise.Signaling/WindowWise.Signaling.csproj src/WindowWise.Signaling/

RUN dotnet restore src/WindowWise.Signaling/WindowWise.Signaling.csproj

COPY src/WindowWise.Signaling/ src/WindowWise.Signaling/

RUN dotnet publish src/WindowWise.Signaling/WindowWise.Signaling.csproj \
    --configuration Release \
    --no-restore \
    --output /app/publish \
    /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app


COPY --from=build /app/publish .

ENV ASPNETCORE_URLS=http://0.0.0.0:5047

EXPOSE 5047
USER $APP_UID

ENTRYPOINT ["dotnet", "WindowWise.Signaling.dll"]
