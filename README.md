# PAD Laborator

Un agent de mesaje publish/subscribe, construit peste socket-uri TCP create manual, în .NET. Proiectul are trei aplicații de consolă: **sender**, **broker** și **receiver**.

- **Sender** construiește mesaje și le trimite broker-ului. Un mesaj e considerat trimis abia după ce broker-ul răspunde `ACK`.
- **Broker** validează fiecare mesaj și îl pune într-o coadă după `messageType`. Un worker (dispatcher) pentru fiecare tip îl livrează tuturor receiver-ilor abonați și așteaptă confirmările lor.
- **Receiver** se abonează la tipuri de mesaje, afișează și salvează mesajele primite și trimite ACK pentru fiecare.

### Tipuri de mesaje și canale

| Fel | Exemple | Cine primește mesajul |
|-----|---------|-----------------------|
| **Tip public** | `Anunt`, `Alerta` | Toți receiver-ii abonați la acel tip |
| **Canal privat** | `ion-1`, `ana-1` | Doar receiver-ul cu acel nume |

Fiecare receiver este abonat automat la propriul canal privat și nu se poate dezabona de la el. Broker-ul face numele unice adăugând un număr (`ion` devine `ion-1`, al doilea `ion` devine `ion-2`). Orice alt `messageType` este respins cu `NACK unknown_type`.


## Pornire rapidă

### Cerințe

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- Opțional: Docker cu Docker Compose

### Rulare locală (trei terminale)

```bash
# Terminal 1: broker-ul (pornește-l primul sau lasă clienții să aștepte)
dotnet run --project src/broker

# Terminal 2: un receiver
dotnet run --project src/receivers -- --name ion --subscribe Anunt,Alerta

# Terminal 3: un sender
dotnet run --project src/sender
```

Poți porni oricâți senderi și receiveri vrei, fiecare în propriul terminal.

### Rulare cu Docker Compose

```bash
docker compose up -d --build broker        # pornește broker-ul în fundal
docker compose run --rm sender             # sender interactiv
docker compose run --rm receiver --name ion --subscribe Anunt,Alerta
docker compose logs -f broker              # urmărește logurile broker-ului
docker compose down                        # oprește tot
```

Note:

- Compose setează `PAD_BROKER_HOST=broker` pentru sender și receiver, deci ele găsesc broker-ul după numele serviciului.
- Folderul `./logs` de pe calculatorul tău este montat în fiecare container, deci logurile și dead letters rămân după oprirea containerelor.
- Serviciile sender și receiver folosesc profilul `interactiv`, așa că `docker compose up` nu le pornește. Le pornești cu `run` când ai nevoie.

### Rulare a unei singure imagini

Construiește din rădăcina repository-ului, pentru că fiecare imagine are nevoie și de `src/common`:

```bash
docker build -f src/broker/Dockerfile    -t pad-broker .
docker build -f src/sender/Dockerfile    -t pad-sender .
docker build -f src/receivers/Dockerfile -t pad-receiver .

docker run --rm -it -p 4242:4242 -v "$(pwd)/logs:/app/logs" pad-broker
docker run --rm -it --network host pad-sender
docker run --rm -it --network host pad-receiver --name ion --subscribe Anunt,Alerta
```

## Opțiuni din linia de comandă

### Broker

Broker-ul nu primește argumente. Ascultă pe portul **4242**, sau pe **4243** dacă 4242 este deja ocupat.

### Sender

| Opțiune | Implicit | Descriere |
|---------|----------|-----------|
| `--host` | `PAD_BROKER_HOST`, altfel `127.0.0.1` | Adresa broker-ului (IP sau nume de host) |
| `--port` | `4242` | Portul broker-ului |
| `--name` | `sender` | Numele cerut; broker-ul îl face unic (`sender-1`, `sender-2`, ...) |

### Receiver

| Opțiune | Implicit | Descriere |
|---------|----------|-----------|
| `--name` | întrebat interactiv | Numele cerut; broker-ul îl face unic (`ion` devine `ion-1`) |
| `--host` | `PAD_BROKER_HOST`, altfel `localhost` | Adresa broker-ului (IP sau nume de host) |
| `--port` | `4242` | Portul broker-ului |
| `--subscribe` | niciunul | Tipuri publice separate prin virgulă, ex. `Anunt,Alerta` |
| `--no-dedup` | oprit | Dezactivează deduplicarea (pentru demonstrații) |
| `--crash-before-ack` | oprit | Procesează primul mesaj, apoi se oprește fără să trimită ACK (pentru demonstrații) |

## Folosirea aplicațiilor

### Meniul sender-ului

```
=== SENDER "sender-1" ===
  1. Trimite mesaje automate (câte unul pentru fiecare destinație)
  2. Scriu eu mesajul
  0. Ieșire
```

- **Opțiunea 1** trimite câte un mesaj de probă către fiecare tip public și către fiecare receiver conectat. Toate au același `correlationId`.
- **Opțiunea 2** cere un text, apoi arată o listă numerotată de destinații (`Anunt`, `Alerta` și numele privat al fiecărui receiver conectat).

Sender-ul afișează `ACK primit` când broker-ul a acceptat mesajul. Asta înseamnă că broker-ul îl are, nu că un receiver l-a procesat deja.

### Meniul receiver-ului

```
=== RECEIVER "ion-1" === mesaje primite: 3
abonat la: [privat: ion-1, Anunt]
  1. Abonează-mă la un tip
  2. Dezabonează-mă de la un tip
  0. Ieșire
```

- **1** listează tipurile publice la care nu ești încă abonat.
- **2** listează abonările tale publice. Canalul privat nu apare niciodată aici.
- **0** închide conexiunea corect.

Mesajele noi se afișează deasupra meniului, iar meniul este reafișat după fiecare.

Dacă broker-ul cade, receiver-ul afișează o notificare și se reconectează la fiecare 2 secunde. La reconectare trimite din nou abonările curente într-un `HELLO` nou.

## Teste

Testele unitare acoperă `Pad.Common` (serializare JSON, validarea mesajelor și framing-ul pe linii):

```bash
dotnet test
```

| Clasă de test | Ce verifică |
|---------------|-------------|
| `MessageSerializationTests` | câmpuri camelCase, o singură linie, dus-întors complet, JSON-ul exact al unui ACK |
| `MessageValidatorTests` | mesaj valid, JSON greșit, câmpuri lipsă, tip necunoscut |
| `LineReaderTests` | linii reconstruite din bucăți TCP, pe un socket real de loopback |