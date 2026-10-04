# PAD Laborator

Un agent de mesaje publish/subscribe, construit peste socket-uri TCP create manual, în .NET. Proiectul are trei aplicații de consolă: **sender**, **broker** și **receiver**.

- **Sender** construiește mesaje și le trimite broker-ului. Un mesaj e considerat trimis abia după ce broker-ul răspunde `ACK`, iar acel `ACK` înseamnă că mesajul este **salvat pe disc** (vezi [Persistență](#persistență-write-ahead-log)).
- **Broker** validează fiecare mesaj, îl scrie în jurnalul de pe disc (WAL) și îl pune într-o coadă după `messageType`. Un worker pentru fiecare tip îl împarte receiver-ilor abonați, iar fiecare receiver are propriul fir de livrare, cu confirmare (ACK), retry și dead-letter.
- **Receiver** se abonează la tipuri de mesaje, afișează și salvează mesajele primite, ignoră duplicatele și trimite ACK pentru fiecare.

Garanția de livrare este **at-least-once**: un mesaj acceptat de broker nu se pierde (nici la `kill -9`), dar poate ajunge de mai multe ori la același receiver. Receiver-ul deduplică după `messageId`.

## Cum funcționează

```
 ┌──────────┐   mesaj      ┌────────────────────────────┐   mesaj     ┌────────────┐
 │  Sender  │ ───────────► │           Broker           │ ──────────► │ Receiver 1 │
 │          │ ◄─────────── │                            │ ◄────────── │ (ion-1)    │
 └──────────┘  ACK / NACK  │  WAL pe disc + o coadă     │    ACK      └────────────┘
                           │  pentru fiecare tip,       │   mesaj     ┌────────────┐
                           │  retry, apoi dead-letter   │ ──────────► │ Receiver 2 │
                           └────────────────────────────┘ ◄────────── │ (ana-1)    │
                                                              ACK     └────────────┘
```

### Tipuri de mesaje și canale

| Fel | Exemple | Cine primește mesajul |
|-----|---------|-----------------------|
| **Tip public** | `Anunt`, `Alerta` | Toți receiver-ii abonați la acel tip |
| **Canal privat** | `ion-1`, `ana-1` | Doar receiver-ul cu acel nume |

Fiecare receiver este abonat automat la propriul canal privat și nu se poate dezabona de la el. Broker-ul face numele unice adăugând un număr (`ion` devine `ion-1`, al doilea `ion` devine `ion-2`). Orice alt `messageType` este respins cu `NACK unknown_type`.

## Arhitectura broker-ului

Broker-ul are patru părți care lucrează împreună:

```
 Sender ──► ClientHandler ──► BrokerState.Enqueue ──► WAL (fsync) ──► coada tipului
                                      │                                  │
                              ACK către sender                  Worker per tip (Examine)
                           (abia după scrierea pe disc)                   │
                                                          ┌───────────────┼───────────────┐
                                                          ▼               ▼               ▼
                                                   Outbox ion-1     Outbox ana-1     Outbox ...
                                                          │               │
                                                   fir de livrare   fir de livrare    (câte unul
                                                   trimite, așteaptă ACK, retry       per receiver)
```

### 1. Un worker pentru fiecare tip de mesaj

Workerul parcurge coada tipului și **examinează fiecare mesaj o dată pe tur**. Pentru fiecare mesaj:

| Situație | Ce face workerul |
|----------|------------------|
| Există abonați care nu l-au primit încă | Îl pune în `Outbox`-ul fiecăruia și readaugă mesajul la coada cozii |
| Livrări în curs sau în așteptarea unui retry | Readaugă mesajul la coada cozii |
| Cel puțin un receiver a confirmat (sau am renunțat la unul) și nu mai e nimic în curs | Mesajul este **terminat** și iese din WAL |
| Nu l-a primit nimeni pentru că nu există abonați | Îl păstrează în coadă; după `MessageTtl` merge în dead-letter |

Un mesaj nu rămâne niciodată blocat în capul cozii. Dacă așteaptă un abonat, mesajele din spatele lui trec mai departe.

### 2. Un fir de livrare pentru fiecare receiver

Fiecare receiver conectat are un `Outbox` și un fir care îl golește pe rând: trimite mesajul, așteaptă ACK-ul (maxim `AckTimeout`) și, dacă nu vine, reîncearcă după `RetryDelay`. Un receiver lent sau mort își întârzie doar propriile mesaje, nu pe ale celorlalți.

- Retry-ul se face fără să blocheze firul (`Task.Delay`).
- După `MaxDeliveryAttempts` eșecuri la același receiver, broker-ul renunță la el pentru acel mesaj și îl scrie în `deadletter.log`.
- Dacă receiver-ul **se deconectează**, încercarea nu se numără ca eșec. Mesajul rămâne în coadă și este livrat dacă receiver-ul revine.

### 3. Abonat nou și backlog

Un mesaj pe care nu l-a primit nimeni rămâne în coadă până la TTL, deci un abonat care apare mai târziu primește tot backlog-ul acumulat. Mesajele deja livrate cuiva nu se mai retrimit unui abonat nou.

## Persistență (write-ahead log)

Broker-ul scrie fiecare mesaj acceptat în `data/broker.wal`, un fișier doar de adăugat (append-only), **înainte** de a trimite ACK sender-ului.

```
ENQ|{json-ul mesajului}      mesaj acceptat (cu fsync)
ACK|{messageId}|{receiver}   receiver-ul a confirmat mesajul
DONE|{messageId}             mesaj terminat: livrat sau dead-letter
```

La pornire, broker-ul citește fișierul și pune înapoi în cozi mesajele care au `ENQ` și nu au `DONE`, cu confirmările deja primite. Apoi rescrie fișierul compact, doar cu mesajele rămase. Fișierul se compactează și la fiecare 500 de mesaje terminate.

| Eveniment | Ce se întâmplă |
|-----------|----------------|
| `kill -9` cu mesaje necitite | La repornire mesajele sunt reîncărcate și livrate |
| `kill -9` în timpul scrierii | Ultima linie ruptă este ignorată |
| Cădere după procesare, înainte de ACK | Mesajul se retrimite; receiver-ul îl ignoră după `messageId` |
| Pierderea curentului | `ENQ` are fsync și rezistă; `ACK`/`DONE` pot lipsi, ceea ce duce doar la o retrimitere |

Limite: persistența este locală, pe un singur nod, fără replicare. După o repornire, termenul TTL al mesajelor recuperate pornește de la zero.

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

Poți porni oricâți senderi și receiveri vrei, fiecare în propriul terminal. Broker-ul creează singur folderele `logs/` și `data/` în directorul din care îl pornești.

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
- Folderul `./data` este montat în containerul broker-ului. Acolo trăiește `broker.wal`, deci mesajele necomplet livrate supraviețuiesc repornirii sau ștergerii containerului. Dacă ștergi `./data`, ștergi și mesajele salvate.
- Serviciile sender și receiver folosesc profilul `interactiv`, așa că `docker compose up` nu le pornește. Le pornești cu `run` când ai nevoie.

### Rulare a unei singure imagini

Construiește din rădăcina repository-ului, pentru că fiecare imagine are nevoie și de `src/common`:

```bash
docker build -f src/broker/Dockerfile    -t pad-broker .
docker build -f src/sender/Dockerfile    -t pad-sender .
docker build -f src/receivers/Dockerfile -t pad-receiver .

docker run --rm -it -p 4242:4242 -v "$(pwd)/logs:/app/logs" -v "$(pwd)/data:/app/data" pad-broker
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

## Parametri de livrare

Valorile sunt în `src/common/Constants.cs` și se aplică tuturor componentelor.

| Constantă | Valoare | Rol |
|-----------|---------|-----|
| `MaxDeliveryAttempts` | 3 | Încercări de livrare pentru fiecare pereche mesaj–receiver, înainte de dead-letter. Sender-ul folosește aceeași valoare pentru retry-ul lui. |
| `RetryDelay` | 2 s | Pauza între două încercări |
| `AckTimeout` | 5 s | Cât așteptăm un ACK înainte să socotim încercarea eșuată |
| `MessageTtl` | 5 min | Cât păstrăm un mesaj pe care nu l-a primit nimeni (fără abonați) înainte de dead-letter |

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

Sender-ul afișează `ACK primit` când broker-ul a acceptat mesajul. Asta înseamnă că mesajul este salvat pe disc la broker, nu că un receiver l-a procesat deja.

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

## Scenarii de eșec (demonstrații)

Fiecare scenariu poate fi reprodus în câteva minute. Logurile menționate sunt în `logs/`.

### 1. Broker-ul cade cu mesaje necitite (`kill -9`)

1. Pornește broker-ul și un sender. Nu porni niciun receiver.
2. Trimite 5 mesaje către `Anunt` (opțiunea 1 sau 2 din meniul sender-ului). Fiecare primește `ACK`.
3. Oprește brusc broker-ul:
   - local: `kill -9 <pid>`
   - Docker: `docker kill -s KILL <container-broker>`
4. Repornește broker-ul. În `logs/broker.log` apare `wal_loaded` și `wal_recovered 5 messages re-queued`.
5. Pornește un receiver abonat la `Anunt`. Cele 5 mesaje ajung la el.

### 2. Receiver-ul cade după procesare, înainte de ACK

1. Pornește un receiver cu `--crash-before-ack` și unul normal, ambii abonați la `Anunt`.
2. Trimite un mesaj `Anunt`. Primul receiver îl afișează și se oprește fără ACK; cel normal confirmă.
3. În `broker.log` apare `delivery_interrupted` pentru receiver-ul căzut, iar mesajul se termină (`message_complete`) pentru cel rămas.
4. Repornește un receiver cu același nume: nu primește mesajul din nou dacă broker-ul l-a terminat, iar dacă îl primește, apare `DUPLICAT ignorat` (deduplicare după `messageId`).

### 3. Un abonat dispare în timpul retry-ului

Cazul în care coada unui tip ar fi rămas blocată.

1. Pornește doi receiveri abonați la `Anunt`; al doilea cu `--crash-before-ack`.
2. Trimite mai multe mesaje `Anunt`.
3. Receiver-ul sănătos le primește pe toate, fără să aștepte după cel căzut. În `broker.log` nu rămân mesaje blocate.

### 4. Receiver lent sau care nu confirmă

Un receiver care nu trimite ACK își consumă cele 3 încercări (`delivery_failed attempt=1/3`, `2/3`, `3/3`), apoi mesajul pentru el ajunge în `logs/deadletter.log`. Ceilalți receiveri nu sunt întârziați.

### 5. Abonat care apare târziu

1. Trimite mesaje `Alerta` cât timp nu e niciun abonat. Ele rămân în coadă și în WAL.
2. Pornește un receiver abonat la `Alerta`: primește tot backlog-ul.
3. Dacă nu apare niciun abonat în `MessageTtl`, mesajele merg în `deadletter.log` cu motivul `expired`.

## Loguri și fișiere generate

| Fișier | Conținut |
|--------|----------|
| `logs/broker.log` | Evenimentele broker-ului: `message_received`, `message_delivered`, `delivery_failed`, `message_complete`, `dead_letter`, `wal_loaded`, `wal_recovered` etc. |
| `logs/deadletter.log` | Mesajele care nu au putut fi livrate, cu motivul (`no ack from ... after max attempts` sau `expired: ...`) |
| `logs/<sender>.log` | Evenimentele fiecărui sender (`message_acked`, `ack_timeout`, `message_failed`) |
| `logs/<receiver>.log` | Evenimentele tehnice ale receiver-ului |
| `logs/processed-<receiver>.log` | Mesajele procesate de receiver; din el se reface deduplicarea după repornire |
| `data/broker.wal` | Jurnalul mesajelor necomplet livrate (nu se editează manual) |

Format de log: `timestamp | nivel | componentă | eveniment | corr | msg | type | rezultat | durată`. Merită adăugat `data/` și `logs/` în `.gitignore`.

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

Comportamentul broker-ului (persistență, retry, dead-letter) se verifică manual cu scenariile din secțiunea [Scenarii de eșec](#scenarii-de-eșec-demonstrații).