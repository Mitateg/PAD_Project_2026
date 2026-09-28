# Imagine separata, doar pentru broker (P2).
#
# Se construieste din RADACINA repo-ului, pentru ca broker-ul are nevoie si de src/common:
#   docker build -f src/broker/Dockerfile -t pad-broker .
#
# Rulare de unul singur (portul 4242 publicat pe calculatorul gazda, logurile pastrate pe disc):
#   docker run --rm -it -p 4242:4242 -v "$(pwd)/logs:/app/logs" pad-broker
# Rulare impreuna cu restul: vezi docker-compose.yml din radacina.

# ---- Etapa 1: compilam. Imaginea cu SDK are compilatorul .NET. ----
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copiem doar ce ne trebuie: biblioteca comuna si broker-ul.
# Asa imaginea nu depinde de sender sau receiver, care sunt partea colegilor.
COPY src/common/ src/common/
COPY src/broker/ src/broker/

# "publish" = compilare in Release + adunarea fisierelor necesare rularii intr-un folder.
RUN dotnet publish src/broker -c Release -o /app

# ---- Etapa 2: imaginea finala. Doar runtime-ul .NET, fara SDK: iese mult mai mica. ----
FROM mcr.microsoft.com/dotnet/runtime:8.0
WORKDIR /app
COPY --from=build /app .

# Broker-ul asculta pe 4242 (4243 daca primul e ocupat). Informativ: portul se publica cu -p la "docker run".
EXPOSE 4242
EXPOSE 4243

# Logurile si deadletter.log se scriu in /app/logs; monteaza un volum acolo ca sa nu se piarda odata cu containerul.
ENTRYPOINT ["dotnet", "Pad.Broker.dll"]