# Build context es la raiz del repo para poder restaurar por csproj primero.
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY src/SolidarityGrid.Node/SolidarityGrid.Node.csproj src/SolidarityGrid.Node/
RUN dotnet restore src/SolidarityGrid.Node/SolidarityGrid.Node.csproj

COPY src/SolidarityGrid.Node/ src/SolidarityGrid.Node/
RUN dotnet publish src/SolidarityGrid.Node/SolidarityGrid.Node.csproj \
    -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

# curl para el healthcheck de compose; la imagen aspnet no lo trae.
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*

COPY --from=build /app .
EXPOSE 8080 8081
ENTRYPOINT ["dotnet", "SolidarityGrid.Node.dll"]
