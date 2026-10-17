# Imagine separata, doar pentru broker (P2).
#
# Se construieste din RADACINA repo-ului, pentru ca broker-ul are nevoie si de src/common:
#   docker build -f src/broker/Dockerfile -t pad-broker .
#
# Rulare de unul singur (portul 4242 publicat pe calculatorul gazda, logurile pastrate pe disc):
#   docker run --rm -it -p 4242:4242 -v "$(pwd)/logs:/app/logs" pad-broker
# Rulare impreuna cu restul: vezi docker-compose.yml din radacina.

# ---- Etapa 1: compilam ----
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY src/common/ src/common/
COPY src/broker/ src/broker/

RUN dotnet publish src/broker -c Release -o /app

# ---- Etapa 2: imaginea finala (ASP.NET Core runtime, nu doar .NET runtime) ----
FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app .

# gRPC pe portul 4242 (HTTP/2)
EXPOSE 4242

ENTRYPOINT ["dotnet", "Pad.Broker.dll"]