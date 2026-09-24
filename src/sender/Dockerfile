# Imagine separata, doar pentru sender (P1).
#
# Se construieste din RADACINA repo-ului, pentru ca sender-ul are nevoie si de src/common:
#   docker build -f src/sender/Dockerfile -t pad-sender .
#
# Rulare de una singura (broker-ul pe calculatorul gazda):
#   docker run --rm -it --network host pad-sender
# Rulare impreuna cu restul: vezi docker-compose.yml din radacina.

# ---- Etapa 1: compilam. Imaginea cu SDK are compilatorul .NET. ----
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copiem doar ce ne trebuie: biblioteca comuna si sender-ul.
# Asa imaginea nu depinde de broker sau receiver, care sunt partea colegilor.
COPY src/common/ src/common/
COPY src/sender/ src/sender/

# "publish" = compilare in Release + adunarea fisierelor necesare rularii intr-un folder.
RUN dotnet publish src/sender -c Release -o /app

# ---- Etapa 2: imaginea finala. Doar runtime-ul .NET, fara SDK: iese mult mai mica. ----
FROM mcr.microsoft.com/dotnet/runtime:8.0
WORKDIR /app
COPY --from=build /app .

# ENTRYPOINT = ce porneste mereu. Ce scrii dupa numele imaginii ajunge ca argument:
#   docker run --rm -it pad-sender --name vasile --host 192.168.0.15
ENTRYPOINT ["dotnet", "Pad.Sender.dll"]
