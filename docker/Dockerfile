FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props ./
COPY src/FlexyParse.Core/FlexyParse.Core.csproj src/FlexyParse.Core/
COPY src/FlexyParse.Host/FlexyParse.Host.csproj src/FlexyParse.Host/
COPY src/FlexyParse.Recognition.RapidOcr/FlexyParse.Recognition.RapidOcr.csproj src/FlexyParse.Recognition.RapidOcr/
COPY src/FlexyParse.Recognition.Nemotron/FlexyParse.Recognition.Nemotron.csproj src/FlexyParse.Recognition.Nemotron/
RUN dotnet restore src/FlexyParse.Host/FlexyParse.Host.csproj
COPY src/ src/
RUN dotnet publish src/FlexyParse.Host/FlexyParse.Host.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .
RUN mkdir -p /app/models && chown ${APP_UID}:${APP_UID} /app/models
ENV ASPNETCORE_ENVIRONMENT=Production
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "FlexyParse.Host.dll"]
