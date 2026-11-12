# Lab 3 — Partea lui A (Sender, Client HTTP, Load test, Contract)

> **Tu ești A.** Ai făcut sender-ul în Lab 1 și 2. Acum ai trei lucruri: **contractul comun**
> (de care depind ceilalți doi), **clientul HTTP** cu care se face demonstrația, și **integrarea**
> finală. Plus mici schimbări în sender.
>
> Fișierele celorlalți: [`02-broker-warehouse-B.md`](02-broker-warehouse-B.md) ·
> [`03-receiver-proxy-C.md`](03-receiver-proxy-C.md)

---

## 0. Două decizii de luat înainte de orice

### 0.1 Întreabă profesoara: Lab 3 are voie să depindă de Lab 1 și 2?

Enunțul spune că metoda `PUT` e folosită de **nodurile de stocare** ca să trimită date la warehouse.
Nu spune nimic despre un broker. Avem două variante:

| Variantă | Ce înseamnă | Risc |
|---|---|---|
| **A. Lab 3 independent** (sigur) | Sursa face `PUT` direct prin proxy. Brokerul din Lab 1–2 rămâne nefolosit aici. | Zero. Acoperi tot enunțul. |
| **B. Brokerul devine calea de intrare** | Sursa publică prin broker → un receiver „bridge" face `PUT` la warehouse. | Dacă nu e acceptat, ai muncit degeaba. Dar arată integrare între laboratoare. |

**Recomandarea mea:** construiți **întâi varianta A**, care acoperă complet cerința. Varianta B se
adaugă pe deasupra, ca bonus demonstrabil, fără să rescrii nimic. Așa nu rămâneți descoperiți dacă
răspunsul e „nu".

Întreabă explicit și scrie răspunsul aici: `_______________`

### 0.2 Cassandra: obligatoriu sau recomandat?

Enunțul zice „**Recomandări**: Utilizarea BD Cassandra". E recomandare, nu cerință. Dar e cea mai
bună idee din enunț: dacă toate cele 3 warehouse-uri scriu în aceeași Cassandra, **nu mai ai nicio
problemă de sincronizare** între ele. Fără asta, cele 3 noduri ar avea date diferite și round-robin-ul
ar returna răspunsuri diferite la aceeași cerere — exact ce nu vrei să se vadă la prezentare.

Deci: **da, merită**. Dar vezi avertismentul de timp din fișierul lui B.

---

## 1. Contractul comun — îl definești tu, toți depind de el

**Acesta e primul lucru de făcut.** Până nu există, B și C nu pot lucra decât pe presupuneri.
Scrie-l, pune-l în `Pad.Common`, fă commit, anunță echipa. O oră de muncă, dar deblochează doi oameni.

### 1.1 Modelul de date

`src/common/Models/Employee.cs`:

```csharp
namespace Pad.Common.Models;

// Clasa (nu record) cu proprietati publice si constructor fara parametri:
// serializatorul XML din .NET are nevoie de amandoua ca sa functioneze.
public class Employee
{
    public int    Id         { get; set; }
    public string Name       { get; set; } = "";
    public string Position   { get; set; } = "";
    public string Department { get; set; } = "";
    public string UpdatedAt  { get; set; } = "";   // ISO-8601 UTC
}
```

> **Atenție la detaliul ăsta:** dacă faci `record` sau pui proprietăți `init`, serializarea XML
> crapă la rulare, nu la compilare. Și o să pierzi o oră căutând de ce. Clasă simplă, `get; set;`,
> constructor implicit.

### 1.2 API-ul HTTP

Toate răspunsurile respectă antetul `Accept` trimis de client: `application/json` sau
`application/xml`. Orice altceva → `406 Not Acceptable`.

| Metodă | Cale | Ce face | Succes | Erori |
|---|---|---|---|---|
| `GET` | `/employee/?id={id}` | un angajat | `200` | `404` inexistent, `400` id invalid |
| `GET` | `/employees/?offset={o}&limit={l}` | o pagină de angajați | `200` (listă, poate fi goală) | `400` parametri invalizi |
| `PUT` | `/employee/` | creare sau înlocuire (upsert idempotent) | `201` creat / `200` înlocuit | `400` corp invalid, `415` content-type greșit |
| `POST` | `/employee/?id={id}` | modificare parțială | `200` | `404`, `400` |
| `GET` | `/update/employees/?since={ISO-8601}` | ce s-a schimbat de la un moment (pull-request) | `200` | `400` |
| `GET` | `/health` | e viu? (pentru proxy și Docker) | `200` | `503` |

Limite: `limit` implicit 10, maxim 100. `offset` implicit 0.

> **De ce `PUT` trebuie să fie idempotent** (același `PUT` de două ori = același rezultat):
> pentru că proxy-ul lui C va **reîncerca pe alt nod** când unul cade. Dacă `PUT` n-ar fi idempotent,
> o reîncercare ar putea crea un al doilea angajat. Idempotența e ce face failover-ul sigur —
> exact aceeași idee ca `messageId` din Lab 2.

### 1.3 Constante noi în `Pad.Common/Constants.cs`

```csharp
/// <summary>Portul proxy-ului — singura poarta de intrare pentru clienti.</summary>
public const int ProxyPort = 8080;

/// <summary>Portul unui nod warehouse (in container).</summary>
public const int WarehousePort = 5000;

/// <summary>Cat traieste un raspuns in cache-ul proxy-ului.</summary>
public static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(30);

/// <summary>Cat asteapta proxy-ul un warehouse inainte sa-l considere picat.</summary>
public static readonly TimeSpan ProxyRequestTimeout = TimeSpan.FromSeconds(5);

/// <summary>Cat timp ocolim un nod care a esuat.</summary>
public static readonly TimeSpan NodeFailureCooldown = TimeSpan.FromSeconds(10);

/// <summary>Tipul de mesaj pentru schimbari de angajat (doar daca mergem pe varianta B).</summary>
public const string EmployeeChangeType = "EmployeeChange";
```

Și, dacă mergeți pe varianta B, adaugi `"EmployeeChange"` în `KnownMessageTypes` — altfel brokerul
dă `NACK unknown_type`.

### 1.4 Variabile de mediu

| Variabilă | Cine o citește | Ce înseamnă |
|---|---|---|
| `PAD_PROXY_URL` | client, load test, bridge | adresa proxy-ului |
| `PAD_WAREHOUSE_NODES` | proxy | lista de noduri DW, separate prin virgulă |
| `PAD_CASSANDRA_HOST` | warehouse | unde e Cassandra |
| `PAD_REDIS` | proxy | unde e Redis |
| `PAD_BROKER_HOST` | sender, receiver | există deja din Lab 2 |

### 1.5 Ce dai echipei ca să nu stea blocată

Pe lângă contract, dă-le **un warehouse fals** de 20 de linii, ca C să poată lucra la proxy înainte
ca B să termine:

```csharp
// scripts/fake-warehouse/Program.cs — doar pentru deblocare, se sterge la final
var app = WebApplication.CreateBuilder(args).Build();
app.MapGet("/employee/", (int id) => Results.Ok(new { id, name = "Fals", position = "Test", department = "QA" }));
app.MapGet("/health", () => Results.Ok("ok"));
app.Run("http://localhost:5001");
```

**Gata când:** contractul e comis pe `dev`, constantele sunt în `Pad.Common`, echipa a confirmat.

---

## 2. Sender: mesaje de tip EmployeeChange

> Sari peste tot capitolul ăsta dacă răspunsul la 0.1 e „Lab 3 trebuie să fie independent".

În `src/sender/MessageFactory.cs`:

```csharp
public static BrokerMessage EmployeeChange(Employee e, string? correlationId = null)
{
    var payload = new { op = "upsert", employee = e };

    return new BrokerMessage
    {
        MessageId     = Guid.NewGuid().ToString(),
        CorrelationId = correlationId ?? Guid.NewGuid().ToString(),
        MessageType   = Constants.EmployeeChangeType,
        SchemaVersion = 1,
        OccurredAt    = DateTime.UtcNow.ToString("o"),
        PayloadJson   = JsonSerializer.Serialize(payload, Json.Options),
    };
}
```

În meniul din `src/sender/Program.cs`, trei opțiuni noi:

- `3.` trimite un angajat generat automat (id și nume aleatoare)
- `4.` scriu eu datele angajatului (id, nume, poziție, departament)
- `5.` trimite N angajați deodată — **asta îți trebuie pentru demonstrația de încărcare**

**Nu schimba nimic la `SendWithRetry` sau la ACK.** Semantica rămâne cea din Lab 2: ACK-ul înseamnă
că brokerul a salvat mesajul în WAL, nu că a ajuns în warehouse. Dacă vrei confirmare până la capăt,
aceea vine din bridge-ul lui C care nu dă ACK decât după ce warehouse-ul a acceptat.

`OccurredAt` devine important aici: B îl folosește ca să rezolve mesajele care ajung în altă ordine.
Nu-l scoate și nu-l lăsa gol.

---

## 3. `Pad.Client` — clientul HTTP (piesa de demonstrație)

Proiect nou, consolă: `src/client/Pad.Client.csproj`, referință la `Pad.Common`.

**Regula de aur: clientul vorbește DOAR cu proxy-ul.** Niciodată direct cu un warehouse. Asta e
chiar cerința etapei 2 („comunicarea acestuia cu orice client să fie prin aplicația proxy").

### Meniu

```
=== CLIENT === (format: JSON | proxy: http://localhost:8080)
  1. Caută un angajat după id
  2. Listează angajați (offset / limit)
  3. Schimbă formatul (JSON / XML)
  4. Ce s-a schimbat de la un moment dat
  5. Trimite un angajat (PUT)        <- util ca sa arati invalidarea cache-ului
  0. Ieșire
```

### Ce afișează după fiecare cerere

Asta e partea care face demonstrația convingătoare — fără ea, nu se vede nimic din ce face proxy-ul:

```
200 OK  ·  142 ms  ·  X-Cache: MISS  ·  X-Served-By: warehouse-2
{
  "id": 1,
  "name": "Ion Popescu",
  ...
}
```

Deci: codul de stare, durata, `X-Cache`, `X-Served-By`, apoi corpul formatat frumos.
A doua oară același lucru trebuie să arate `X-Cache: HIT` și un timp mult mai mic.

### Detalii care contează

- Trimite un antet `X-Client-Id` stabil (de exemplu `client-ana`), ca C să te poată număra în Redis.
- Tratează fără să crape: `404` (nu există), `406` (format nesuportat), conexiune refuzată
  (proxy-ul e oprit).
- Pentru XML, afișează XML-ul indentat, nu pe un rând.

**Gata când:** merge în ambele formate, prin proxy, și se văd antetele de cache și de nod.

---

## 4. `Pad.LoadTest` — unealta de încărcare

Proiect nou, consolă: `tools/loadtest/Pad.LoadTest.csproj`.

```
dotnet run --project tools/loadtest -- --clients 50 --requests 20 --path "/employee/?id=1" --accept json
```

Pornește `--clients` task-uri în paralel, fiecare trimite `--requests` cereri prin proxy, și măsoară.

La final scoate un tabel:

```
Total:        1000 cereri în 4,2 s  →  238 cereri/s
Latență:      p50 = 12 ms   p95 = 48 ms   max = 210 ms
Cache:        HIT 962 (96%)  ·  MISS 38 (4%)
Distribuție:  warehouse-1: 13  ·  warehouse-2: 12  ·  warehouse-3: 13
```

Ultimele două rânduri sunt cele care dovedesc caching-ul și round-robin-ul. Rulează-l de **două ori**:
prima dată cu cache-ul gol, a doua oară cu el cald — diferența de timp e demonstrația.

**Gata când:** scoate tabelul de mai sus și se vede diferența între rulări.

---

## 5. Integrarea finală (tu o faci)

1. **Adună `docker-compose.yml`** din bucățile lui B (cassandra + 3 warehouse) și C (redis + proxy).
   Verifică pe un folder curat: `git clone`, apoi `docker compose up -d --build` trebuie să pornească tot.
2. **Publică doar portul proxy-ului** (8080) către exterior. Warehouse-urile rămân în rețeaua internă —
   așa se vede că clientul chiar nu are cum să le acceseze direct.
3. **README**: diagrama arhitecturii, cum se pornește, variabilele de mediu, scenariile de
   demonstrație.
4. **Un ADR nou**, `adr-004-proxy-si-caching.md`, în stilul celorlalte: de ce proxy scris de mână
   și nu YARP, de ce `Accept` face parte din cheia de cache, de ce invalidare la scriere în loc de
   TTL scurt.
5. **Repetiția demonstrației** — cu toți trei, cronometrat.

---

## 6. Ordinea în care lucrezi

| # | Ce | De ce în ordinea asta |
|---|---|---|
| 1 | Contractul + constantele + modelul | B și C nu pot începe serios fără |
| 2 | Warehouse-ul fals | C poate lucra la proxy fără să aștepte |
| 3 | `Pad.Client` pe warehouse-ul fals | îți testezi clientul devreme |
| 4 | `Pad.LoadTest` | simplu odată ce clientul merge |
| 5 | Sender-ul cu `EmployeeChange` | doar dacă varianta B e aprobată |
| 6 | Integrare, compose, README, ADR | la final, când există toate piesele |

---

## 7. Gata când (bifează)

- [ ] Contractul e comis și echipa l-a confirmat
- [ ] `Employee` și constantele noi sunt în `Pad.Common`
- [ ] `Pad.Client` merge prin proxy, JSON și XML, afișează `X-Cache` și `X-Served-By`
- [ ] `Pad.LoadTest` scoate tabelul cu latențe, cache și distribuție
- [ ] (varianta B) Sender-ul trimite `EmployeeChange`, simplu și în masă
- [ ] `docker compose up -d --build` pornește tot dintr-un clone curat
- [ ] README actualizat, ADR-004 scris
- [ ] Demonstrația repetată cel puțin o dată, cu toți trei
