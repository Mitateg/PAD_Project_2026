# Imagine separata, doar pentru warehouse (DW).
#
# Se construieste din RADACINA repo-ului, pentru ca warehouse-ul are nevoie si de src/common:
#   docker build -f src/warehouse/Dockerfile -t pad-warehouse .
#
# Rulare de unul singur, cu date in memorie (fara Cassandra), pe portul 5000:
#   docker run --rm -p 5000:5000 pad-warehouse
# Rulare impreuna cu Cassandra si restul: vezi docker-compose.yml din radacina.

# ---- Etapa 1: compilam ----
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY src/common/ src/common/
COPY src/warehouse/ src/warehouse/

RUN dotnet publish src/warehouse -c Release -o /app

# ---- Etapa 2: imaginea finala (ASP.NET Core runtime) ----
FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app .

# HTTP pe portul 5000 (se poate schimba cu PAD_PORT)
EXPOSE 5000

ENTRYPOINT ["dotnet", "Pad.Warehouse.dll"]
