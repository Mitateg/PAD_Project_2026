# Runbook: sender-ul in Docker

Sender-ul are imaginea lui, construita din `src/sender/Dockerfile`.
Imaginea contine doar `src/common` si `src/sender`, deci se construieste chiar daca
broker-ul si receiver-ul nu sunt inca in repo.

## Construire si rulare cu docker compose

```powershell
docker compose build sender
docker compose run --rm sender
```

`--rm` sterge containerul cand iesi din meniu cu `0`.
Poti porni oricati senderi: fiecare comanda creeaza alt container.

## Construire si rulare fara compose

```powershell
docker build -f src/sender/Dockerfile -t pad-sender .
docker run --rm -it pad-sender --name vasile --host 192.168.0.15
```

Atentie la punctul de la final: contextul de build este radacina repo-ului, nu folderul sender-ului.
Ce scrii dupa numele imaginii ajunge direct ca argument in aplicatie.

## Numele sender-ului

Nu trebuie sa te ocupi de el. La conectare, broker-ul da fiecarui sender un nume unic:
primul devine `sender-1`, al doilea pornit in acelasi timp devine `sender-2`, si asa mai departe.
Numele se elibereaza cand sender-ul se inchide.

Numele primit apare in titlul ferestrei, in meniu si da numele fisierului de log
(`logs/sender-1.log`), deci doi senderi nu se amesteca.

Daca vrei alt nume de baza, il dai cu `--name`:

```powershell
docker compose run --rm sender --name vasile
```

Broker-ul tot adauga numarul, deci vei aparea ca `vasile-1`.

La receiver, numele unic este si canalul lui privat, deci se foloseste la rutare.
La sender, numele conteaza doar in loguri.

## Adresa broker-ului

| Situatie | Ce folosesti |
|---|---|
| totul in docker compose | `PAD_BROKER_HOST: broker` (deja setat in compose) |
| broker pornit pe calculatorul tau, sender in container | `--host host.docker.internal` |
| broker pe alt calculator | `--host 192.168.0.15` |
| fara Docker | implicit `127.0.0.1` |
