# Lab 3 — Partea lui B (Broker + Warehouse + Cassandra)

> **Tu ai făcut brokerul** în Lab 1 și 2. Acum construiești **nodul warehouse (DW)** — serverul HTTP
> care ține datele. Plus câteva reparații mici în broker.
>
> **Tu ești pe drumul critic.** Și A (clientul) și C (proxy-ul) au nevoie de warehouse ca să-și poată
> testa partea lor. Citește secțiunea 2 înainte de orice: cum să-i deblochezi în prima zi.
>
> Contractul comun e în [`01-sender-client-A.md`](01-sender-client-A.md), secțiunea 1. Nu-l
> reinventa — dacă ceva lipsește, cere-i lui A să-l completeze acolo.

---

## 1. Reparații în broker (scurt, fă-le primul)

Am verificat codul pe commit-ul curent. Din lista care circula, **două erau deja reparate**.
Au rămas trei lucruri reale:

### 1.1 `MessageSave.Append` scrie o linie goală în plus — REAL

`src/broker/MessageSave.cs:122`:

```csharp
_writer!.WriteLine(line + "\n");    // WriteLine adauga DEJA un newline
```

`WriteLine` pune deja terminatorul de linie, iar `+ "\n"` mai adaugă unul. Rezultatul: WAL-ul are o
linie goală după fiecare intrare. Nu strică nimic logic (sunt numărate ca „sărite" la încărcare),
dar dublează fișierul și arată neglijent dacă îl deschide cineva la prezentare.

```csharp
_writer!.WriteLine(line);           // atat
```

### 1.2 README descrie alt format de WAL decât scrie codul — REAL

README-ul spune `ENQ|{json}`, codul scrie `ENQUEUE: {json}` (`MessageSave.cs:80, 153, 198`).
Alege una și fă-le la fel. Mai simplu e să corectezi README-ul.

### 1.3 `EmployeeChange` lipsește din tipurile cunoscute — REAL (doar varianta B)

Dacă echipa merge pe varianta cu broker (vezi 0.1 în fișierul lui A), adaugă în
`src/common/Constants.cs`:

```csharp
public static readonly string[] KnownMessageTypes = { "Anunt", "Alerta", "EmployeeChange" };
```

Fără asta, brokerul răspunde `NACK unknown_type` la orice mesaj de angajat.
**`Pad.Common` e al lui A** — cere-i lui să facă schimbarea, nu o face direct.

### Ce NU mai trebuie reparat

- `RecordAck` **e deja corect**: `RecordAck(messageId, receiverName)`, iar `Dispatcher.cs:176` îl
  cheamă cu argumentele potrivite. Informația că ar fi inversate e veche.
- `using` duplicat în `ReceiverConnection.cs` — **nu mai există**.

**Gata când:** WAL-ul n-are linii goale, README-ul se potrivește cu codul, cele 25 de teste trec.

---

## 2. Deblochează echipa în prima zi

**Cel mai important lucru pe care îl faci.** A și C nu pot avansa fără un warehouse.

Nu începe cu Cassandra. Începe cu un warehouse care ține datele **în memorie** și respectă
contractul. Când merge, publică-l pe `dev` și anunță echipa — din acel moment lucrează toți trei
în paralel.

Cassandra vine abia după. Schimbi o singură clasă (repository-ul), restul rămâne neatins. De asta
merită structura pe straturi de la punctul 3.

> **De ce insist:** Cassandra pornește în 30–60 de secunde și are nevoie de healthcheck, de retry la
> conectare și de un keyspace creat la pornire. E ușor să pierzi o zi acolo. Dacă în ziua aceea A și
> C stau blocați pentru că n-au pe ce testa, pierdeți trei zile-om, nu una.

---

## 3. Structura proiectului `Pad.Warehouse`

Proiect nou: `src/warehouse/Pad.Warehouse.csproj`, cu `Sdk="Microsoft.NET.Sdk.Web"`, `net8.0`,
referință la `Pad.Common`.

Enunțul cere explicit componente **Controller** („«Maparea» cererilor HTTP pe operațiile serviciilor
se va face în componente numite generic Controller"). Respectă trei straturi:

```
Controllers/EmployeeController.cs   HTTP in -> apel la serviciu -> HTTP out.  FARA logica.
Services/EmployeeService.cs         validari, reguli de paginare, urmarirea schimbarilor
Data/IEmployeeRepository.cs         interfata
Data/InMemoryEmployeeRepository.cs  prima varianta (ConcurrentDictionary)
Data/CassandraEmployeeRepository.cs a doua varianta, acelasi contract
```

Avantajul e concret, nu teoretic: schimbi memoria pe Cassandra înlocuind o singură linie la
înregistrarea serviciilor, iar controllerul și serviciul nu se ating deloc.

---

## 4. Endpoint-urile

Respectă tabelul din contract (fișierul lui A, 1.2). Câteva lucruri care se greșesc des:

**Paginarea.** `limit` implicit 10, maxim 100. Dacă vine `limit=5000`, nu returna 5000 —
fie îl tai la 100, fie dai `400`. Alege una și scrie-o în README.

**`PUT` trebuie să fie idempotent.** Același `PUT` trimis de două ori = același rezultat, fără
eroare. E obligatoriu, pentru că proxy-ul lui C reîncearcă pe alt nod când unul cade. Primul `PUT`
dă `201`, următoarele `200`.

**`POST` modifică doar câmpurile trimise.** Dacă vine doar `{"name": "X"}`, nu șterge poziția și
departamentul.

**`/update/employees/?since=T`** returnează ce s-a schimbat după momentul `T`. Ai nevoie de o
evidență a schimbărilor — vezi tabelul `employee_changes` de la 6.2.

**`/health`** returnează `200` doar dacă chiar poți ajunge la Cassandra. Dacă răspunzi `200` fără să
verifici, proxy-ul lui C va trimite trafic spre un nod mort și se va vedea urât la demonstrație.

---

## 5. Cele două cerințe explicite din enunț

### 5.1 Formatul ales de client (JSON / XML)

```csharp
builder.Services
    .AddControllers(o => o.ReturnHttpNotAcceptable = true)   // Accept nesuportat -> 406
    .AddXmlSerializerFormatters();
```

`ReturnHttpNotAcceptable = true` e cheia: fără el, un `Accept: text/plain` primește JSON în loc de
`406`, și cerința nu e îndeplinită.

Verifică cu mâna ta:

```bash
curl -H "Accept: application/json" "http://localhost:5000/employee/?id=1"
curl -H "Accept: application/xml"  "http://localhost:5000/employee/?id=1"
curl -H "Accept: text/plain"       "http://localhost:5000/employee/?id=1"   # trebuie 406
```

Dacă XML-ul iese gol sau crapă: modelul `Employee` trebuie să fie **clasă** cu `get; set;` și
constructor fără parametri. Vezi avertismentul din contractul lui A.

### 5.2 Procesare concurentă, thread-per-request

Kestrel îți dă asta din oficiu — fiecare cerere e servită pe un fir din pool. Dar trebuie să
**demonstrezi** că e așa și să nu strici tu modelul:

- Controllerele rămân **fără stare**. Nicio variabilă de instanță care se schimbă între cereri.
- Starea partajată e thread-safe: `ConcurrentDictionary` în varianta din memorie, Cassandra în cealaltă.
- **Scrie id-ul firului în log la fiecare cerere** — fără asta n-ai ce arăta:

```csharp
_log.Info("http_request",
          result: $"{Request.Method} {Request.Path}{Request.QueryString} thread={Environment.CurrentManagedThreadId}");
```

La demonstrație, rulezi load test-ul lui A și arăți în log zeci de id-uri diferite. Aceea e dovada.

---

## 6. Cassandra

### 6.1 De ce o folosim

Ca să **nu trebuiască să sincronizezi** cele 3 noduri warehouse între ele. Toate scriu și citesc din
aceeași Cassandra, deci văd aceleași date. Fără asta, round-robin-ul ar returna răspunsuri diferite
la aceeași cerere, în funcție de nodul nimerit — și s-ar vedea imediat la prezentare.

### 6.2 Schema

Rulată de warehouse la pornire, cu reîncercare până când Cassandra răspunde:

```sql
CREATE KEYSPACE IF NOT EXISTS pad
  WITH replication = {'class': 'SimpleStrategy', 'replication_factor': 1};

CREATE TABLE IF NOT EXISTS pad.employees (
  id int PRIMARY KEY,
  name text,
  position text,
  department text,
  updated_at timestamp
);

-- evidenta schimbarilor, pentru /update/employees/?since=
CREATE TABLE IF NOT EXISTS pad.employee_changes (
  bucket text,              -- 'all' e suficient la scara laboratorului
  changed_at timeuuid,
  employee_id int,
  PRIMARY KEY (bucket, changed_at)
) WITH CLUSTERING ORDER BY (changed_at DESC);
```

### 6.3 Repository-ul

Un singur `Cluster` și o singură `ISession` pe tot procesul (înregistrate ca singleton). Instrucțiuni
pregătite (`PreparedStatement`), nu concatenare de text.

- `GET` după id: `SELECT ... WHERE id = ?`
- `PUT`: `INSERT` — în Cassandra e deja upsert, deci idempotența vine gratis
- `POST`: `UPDATE ... SET` doar câmpurile primite
- Fiecare scriere adaugă și un rând în `employee_changes`

**Paginarea e problema.** Cassandra nu are `OFFSET`. La scara laboratorului poți citi primele
`offset + limit` rânduri și sări peste primele `offset`. **Scrie limitarea asta în README** — e exact
genul de lucru pe care e bine să-l recunoști tu înainte să-l întrebe cineva.

### 6.4 Mesaje care ajung în altă ordine (doar varianta B)

Brokerul **nu garantează ordinea**. O schimbare mai veche poate ajunge după una mai nouă și ar
suprascrie date bune cu date vechi.

Rezolvarea e elegantă — lași Cassandra să decidă, folosind momentul schimbării ca marcaj de timp:

```sql
INSERT INTO pad.employees (...) VALUES (...) USING TIMESTAMP ?;   -- microsecundele din OccurredAt
```

Cassandra păstrează scrierea cu timestamp-ul cel mai mare, indiferent de ordinea sosirii. Problema
dispare complet, fără cod de comparat versiuni.

---

## 7. Docker

`src/warehouse/Dockerfile` — copiază tiparul din `src/broker/Dockerfile` (build cu `sdk:8.0`, rulare
pe `aspnet:8.0`), `EXPOSE 5000`.

În `docker-compose.yml`:

```yaml
cassandra:
  image: cassandra:4.1
  healthcheck:
    test: ["CMD-SHELL", "cqlsh -e 'describe keyspaces' || exit 1"]
    interval: 10s
    timeout: 5s
    retries: 10
    start_period: 60s        # Cassandra chiar are nevoie de atat

warehouse-1: &warehouse
  build: { context: ., dockerfile: src/warehouse/Dockerfile }
  environment:
    PAD_CASSANDRA_HOST: cassandra
  depends_on:
    cassandra: { condition: service_healthy }
# warehouse-2 si warehouse-3 la fel
```

**Nu publica porturile warehouse-urilor către exterior.** Doar proxy-ul lui C are voie pe 8080.
Așa demonstrezi că accesul chiar trece prin proxy.

---

## 8. Teste

- **Unitare** pe serviciu: id invalid, `limit` peste maxim, `offset` negativ, paginare la margini.
- **De integrare**: fiecare endpoint, în ambele formate, cu toate codurile de eroare. Poți refolosi
  tiparul din `tests/integration` — acolo există deja un `TestBroker` care pornește un server pe un
  port liber; fă la fel un `TestWarehouse`.
- **Concurență**: 50 de `PUT`-uri în paralel cu id-uri diferite, apoi verifici că toate sunt salvate.
- **Idempotență**: același `PUT` de două ori → fără eroare, fără duplicat.

---

## 9. Ordinea în care lucrezi

| # | Ce | Cât durează, aproximativ |
|---|---|---|
| 1 | Reparațiile din broker (secțiunea 1) | 30 min |
| 2 | Proiectul + straturile + repository în memorie | 2 ore |
| 3 | Endpoint-urile + JSON/XML + `/health` | 2 ore |
| 4 | **Publică pe `dev` și anunță echipa** | — |
| 5 | Cassandra: schema, repository, retry la pornire | 3 ore |
| 6 | Dockerfile + servicii în compose | 1 oră |
| 7 | Teste | 1,5 ore |

Pasul 4 e cel mai important din tabel. Nu-l amâna până „e totul gata".

---

## 10. Gata când (bifează)

- [ ] Reparațiile din broker sunt făcute, cele 25 de teste trec
- [ ] Toate endpoint-urile din contract funcționează
- [ ] JSON și XML, iar `Accept` nesuportat dă `406`
- [ ] Logul arată id-uri de fir diferite la cereri simultane
- [ ] Datele supraviețuiesc unei reporniri a warehouse-ului (sunt în Cassandra)
- [ ] Două replici de warehouse văd aceleași date
- [ ] Același `PUT` repetat nu dă eroare și nu duplică
- [ ] `/health` dă `200` doar când Cassandra chiar răspunde
- [ ] Limitarea de paginare e scrisă în README
