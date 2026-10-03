# Imagine separata, doar pentru receiver (P3).
#
# Se construieste din RADACINA repo-ului, pentru ca receiver-ul are nevoie si de src/common:
#   docker build -f src/receivers/Dockerfile -t pad-receiver .
#
# Rulare de una singura (broker-ul pe calculatorul gazda):
#   docker run --rm -it --network host pad-receiver
# Rulare impreuna cu restul: vezi docker-compose.yml din radacina.

# ---- Etapa 1: compilam. Imaginea cu SDK are compilatorul .NET. ----
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copiem doar ce ne trebuie: biblioteca comuna si receiver-ul.
# Asa imaginea nu depinde de broker sau sender, care sunt partea colegilor.
COPY src/common/ src/common/
COPY src/receivers/ src/receivers/

# "publish" = compilare in Release + adunarea fisierelor necesare rularii intr-un folder.
RUN dotnet publish src/receivers -c Release -o /app

# ---- Etapa 2: imaginea finala. Doar runtime-ul .NET, fara SDK: iese mult mai mica. ----
FROM mcr.microsoft.com/dotnet/runtime:8.0
WORKDIR /app
COPY --from=build /app .

# ENTRYPOINT = ce porneste mereu. Ce scrii dupa numele imaginii ajunge ca argument:
#   docker run --rm -it pad-receiver --name ion --subscribe Anunt,Alerta
ENTRYPOINT ["dotnet", "Pad.Receiver.dll"]