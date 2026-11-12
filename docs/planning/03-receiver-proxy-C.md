# Lab 3 — Partea lui C (Proxy + Redis + Bridge receiver)

> **Tu ai făcut receiver-ul** în Lab 1 și 2. Acum construiești **proxy-ul** — componenta centrală a
> etapei 2 din enunț, prin care trece tot traficul clienților.
>
> **Partea ta e cea mai mare.** Proxy-ul are patru lucruri de făcut deodată: redirecționare,
> echilibrare, cache și evidența clienților. Dacă rămâi în urmă, cere ajutor lui A — el termină
> primul și poate prelua cache-ul sau testele.
>
> Contractul comun e în [`01-sender-client-A.md`](01-sender-client-A.md), secțiunea 1.

---

## 0. Ce cere enunțul de la tine, textual

> *„Modificați nodul informativ din etapa 1 astfel încît comunicarea acestuia cu orice client să fie
> prin aplicația proxy. Funcțiile nodului proxy:*
> - ***Smart-proxy**, pentru păstrarea conexiunilor spre clienți;*
> - ***Caching**, pentru a micșora timpul general de răspuns al sistemului;*
> - ***Load-balancing**, […] nodul proxy va selecta unul din serverele disponibile conform unui
>   algoritm selectat."*

Trei funcții, toate obligatorii. Plus recomandarea de a ține conexiunile în **Redis**.

---

## 1. Cum începi fără să aștepți pe nimeni

B îți dă un warehouse în memorie în prima zi, iar A îți dă un warehouse fals de 20 de linii.
Pornește două copii ale aceluiași lucru pe porturi diferite (`5001`, `5002`) și ai deja pe ce testa
round-robin-ul. Nu aștepta Cassandra — n-ai nevoie de ea ca să-ți construiești proxy-ul.

---

## 2. Proiectul `Pad.Proxy`

`src/proxy/Pad.Proxy.csproj`, cu `Sdk="Microsoft.NET.Sdk.Web"`, `net8.0`, referință la `Pad.Common`,
pachetul `StackExchange.Redis`. Ascultă pe `Constants.ProxyPort` (8080).

### 2.1 De ce îl scriem de mână și nu cu YARP

YARP (biblioteca Microsoft de reverse proxy) ți-ar da redirecționarea și echilibrarea gata făcute.
Dar atunci cache-ul și evidența clienților ar fi niște middleware-uri lipite pe lângă, iar
**exact alea sunt cerința**. Un proxy scris de mână e mai ușor de explicat la prezentare și arată
clar ce ai făcut tu. Scrie motivul ăsta în ADR, e o decizie reală.

### 2.2 Miezul: redirecționarea

Un singur endpoint care prinde tot:

```csharp
app.Map("/{**path}", async (HttpContext ctx, IHttpClientFactory factory) =>
{
    // 1. alege nodul (round-robin, doar dintre cele sanatoase)
    // 2. daca e GET si avem raspuns in cache -> intoarce-l, X-Cache: HIT
    // 3. construieste cererea catre nod: metoda, cale, query, corp
    //    + antetele Accept si Content-Type (pe astea DOUA trebuie sa le duci mai departe)
    // 4. trimite, cu timeout = Constants.ProxyRequestTimeout
    // 5. copiaza inapoi: status, content-type, corp
    // 6. daca a fost GET 200 -> pune in cache
    // 7. daca a fost PUT/POST reusit -> invalideaza cache-ul
    // 8. scrie clientul in Redis
});
```

Folosește `IHttpClientFactory`, nu `new HttpClient()` la fiecare cerere — altfel epuizezi socketurile
sub încărcare și load test-ul lui A o să ți-o arate urât.

---

## 3. Load balancing (Round-Robin)

### 3.1 Alegerea nodului

Lista vine din `PAD_WAREHOUSE_NODES`, separată prin virgulă.

```csharp
private int _counter;

private string NextNode()
{
    var healthy = _nodes.Where(n => !IsInCooldown(n)).ToList();
    if (healthy.Count == 0)
        healthy = _nodes;                        // toate par picate: incercam oricum

    int i = Interlocked.Increment(ref _counter);
    return healthy[(i & 0x7FFFFFFF) % healthy.Count];
}
```

`Interlocked.Increment` pentru că mai multe cereri vin în paralel. Un `_counter++` obișnuit ar
pierde incrementări și distribuția ar ieși strâmbă — exact ce se vede în demonstrația cu 9 cereri.

Masca `& 0x7FFFFFFF` e pentru cazul în care contorul trece prin valori negative după multe cereri.

### 3.2 Când un nod cade

Asta e partea care face demonstrația „opresc un warehouse și nu se întâmplă nimic":

- La eroare de conexiune, timeout, sau răspuns `5xx` → marchezi nodul ca bolnav pentru
  `Constants.NodeFailureCooldown` (10 s).
- **Și reîncerci aceeași cerere pe următorul nod.** Fără asta, clientul primește eroare și
  demonstrația pică.
- Poți reîncerca liniștit pentru că toate operațiile din contract sunt idempotente — inclusiv `PUT`,
  care e un upsert. Dacă `PUT` n-ar fi idempotent, o reîncercare ar putea crea date duplicate.
  (Aceeași idee ca `messageId`-ul din Lab 2: reîncercarea e sigură doar dacă operația e idempotentă.)
- Opțional: verificare activă pe `/health` la câteva secunde.

---

## 4. Caching

### 4.1 Regulile

- Se pun în cache **doar răspunsurile `GET` cu status `200`**. Niciodată `PUT`, `POST` sau erori.
- Durata: `Constants.CacheTtl` (30 s).

### 4.2 Cheia — aici e capcana

```csharp
string key = $"GET|{path}|{queryString}|{acceptHeader}";
```

**`Accept` TREBUIE să facă parte din cheie.** Dacă îl lași afară: un client cere JSON, răspunsul
intră în cache; următorul client cere XML, primește JSON-ul din cache. Cerința de negociere a
formatului se rupe exact în momentul în care funcționează cache-ul — și e greu de depistat, pentru
că apare doar la a doua cerere.

### 4.3 Antete de răspuns (nu sunt opționale)

```
X-Cache: HIT          sau MISS
X-Served-By: warehouse-2
```

Fără ele, demonstrația de caching și de echilibrare **nu se vede**. Clientul lui A le afișează, dar
tu trebuie să le pui. Sunt două linii de cod și jumătate din impactul prezentării.

### 4.4 Invalidarea

La un `PUT` sau `POST` reușit, șterge din cache intrările pentru `/employee*` și `/employees*`.

Invalidare grosieră, dar **corectă** — și asta contează mai mult. O invalidare fină (doar id-ul
schimbat) e mai elegantă dar ușor de greșit: uiți de `/employees/` care conține și el angajatul
modificat, și servești date vechi. Rămâi la varianta simplă și scrie în README de ce.

### 4.5 Unde ții cache-ul

`IMemoryCache` e suficient pentru un singur proxy. Redis dacă vrei ca el să supraviețuiască
repornirii proxy-ului — dar nu e cerut, iar Redis e oricum folosit pentru clienți.

---

## 5. Smart-proxy: evidența clienților în Redis

Asta e funcția pe care enunțul o numește „păstrarea conexiunilor spre clienți".

### 5.1 Ce scrii

La fiecare cerere, într-un hash Redis `client:{id}`:

| Câmp | Ce ține |
|---|---|
| `firstSeen` | prima dată când l-ai văzut |
| `lastSeen` | ultima cerere |
| `requests` | câte cereri a făcut (incrementezi) |
| `lastPath` | ce a cerut ultima dată |

Id-ul clientului: antetul `X-Client-Id` dacă vine (clientul lui A îl trimite), altfel IP-ul.
Pune un TTL pe cheie (10 minute), ca să dispară clienții inactivi.

### 5.2 Două endpoint-uri pentru demonstrație

```
GET /proxy/clients    cine e activ acum, cu numarul de cereri
GET /proxy/stats      hit/miss, cereri per nod, noduri bolnave
```

Nu sunt în contract, sunt numai ale proxy-ului. Dar la prezentare valorează mult: deschizi două
clienți, chemi `/proxy/clients`, și se vede că proxy-ul chiar ține evidența.

### 5.3 Redis picat nu trebuie să oprească proxy-ul

**Important.** Dacă Redis cade, proxy-ul continuă să servească cereri și doar scrie un avertisment
în log. Evidența clienților e o funcție auxiliară — n-are voie să devină un punct de cădere pentru
tot sistemul.

Învelește fiecare apel Redis în `try/catch`. E aceeași idee ca `ListReceivers()` din sender-ul lui A
din Lab 2: la eroare returnează listă goală în loc să arunce, și aplicația merge mai departe cu o
funcție mai puțin. Se cheamă degradare elegantă și merită spusă pe nume la prezentare.

---

## 6. Bridge receiver (doar dacă varianta B e aprobată — vezi 0.1 la A)

Receiver-ul tău din Lab 2 dă ACK imediat ce a afișat mesajul. Bridge-ul trebuie să dea ACK
**numai după ce warehouse-ul a acceptat datele**. Asta leagă garanția de livrare din Lab 2 de
depozitul din Lab 3.

### 6.1 Pașii

1. Opțiuni noi în `ReceiverOptions`: `--bridge` și `--proxy-url`.
2. O interfață comună pentru ce se face cu un mesaj:
   ```csharp
   public interface IMessageHandler
   {
       Task<bool> HandleAsync(BrokerMessage m, bool isPrivate);   // true = ACK
   }
   ```
   - `ConsoleMessageHandler` — ce face `MessageProcessor` azi, întoarce mereu `true`
   - `WarehouseBridgeHandler` — nou
3. În `WarehouseBridgeHandler`: citești `PayloadJson` → `{ op, employee }`, apoi trimiți
   `PUT {proxy}/employee/`.

### 6.2 Ce întorci pentru fiecare rezultat

| Ce s-a întâmplat | Întorci | De ce |
|---|---|---|
| `2xx` | `true` → ACK | a reușit |
| `4xx` (date invalide) | `true` → ACK | **eșec permanent**: o reîncercare ar da exact același `4xx`. Loghează și mergi mai departe |
| `5xx`, timeout, conexiune refuzată | `false` → **fără ACK** | eșec temporar: brokerul reîncearcă singur |
| payload stricat | `true` → ACK | n-are cum să se repare prin reîncercare |

**E exact distincția din Lab 2**, dusă un nivel mai departe: reîncerci la probleme de transport,
niciodată la refuzuri de fond. Dacă ai da `false` la un `4xx`, mesajul ar circula în buclă până ar
ajunge în dead-letter, fără nicio șansă să reușească vreodată.

### 6.3 Restul

- În `BrokerConnection`, când handler-ul întoarce `false`, trimiți `Status = Constants.StatusNack`.
  Brokerul numără o încercare eșuată, reîncearcă după `RetryDelay`, iar după `MaxDeliveryAttempts`
  scrie în `deadletter.log`. Nu schimba nimic la logica asta, e deja corectă.
- Păstrează deduplicarea după `messageId`. Chiar dacă `PUT` e idempotent și duplicatele n-ar strica,
  e muncă în plus degeaba.
- **Pornește un singur bridge.** Brokerul face fan-out către toți abonații, deci două bridge-uri ar
  trimite fiecare aceeași schimbare. Ar fi corect (upsert idempotent), dar inutil.
- Pornire: `--name bridge --subscribe EmployeeChange --bridge`

---

## 7. Docker

`src/proxy/Dockerfile`, `EXPOSE 8080`, tiparul din `src/broker/Dockerfile`.

```yaml
redis:
  image: redis:7

proxy:
  build: { context: ., dockerfile: src/proxy/Dockerfile }
  ports: ["8080:8080"]        # SINGURUL port publicat in tot sistemul
  environment:
    PAD_WAREHOUSE_NODES: http://warehouse-1:5000,http://warehouse-2:5000,http://warehouse-3:5000
    PAD_REDIS: redis:6379
  depends_on: [redis, warehouse-1, warehouse-2, warehouse-3]
```

---

## 8. Teste

| Ce testezi | Cum |
|---|---|
| Round-robin | 9 cereri → `X-Served-By` dă 3/3/3 |
| Failover | oprești un warehouse → cererile tot reușesc, pe celelalte |
| Cache | aceeași cerere de două ori → `MISS` apoi `HIT` |
| Invalidare | `GET` (HIT), apoi `PUT`, apoi `GET` → `MISS` din nou |
| Izolarea formatelor | JSON și XML pentru aceeași cale → răspunsuri diferite, nu amestecate |
| Redis picat | oprești Redis → proxy-ul tot servește, doar avertizează |
| Bridge | warehouse picat → brokerul reîncearcă, datele ajung după revenire |
| Bridge, payload stricat | nu intră în buclă infinită |

---

## 9. Ordinea în care lucrezi

| # | Ce | De ce aici |
|---|---|---|
| 1 | Proiectul + redirecționarea simplă către **un singur** nod | până merge asta, nimic altceva n-are sens |
| 2 | Round-robin peste 2 noduri false | se testează ușor, fără Cassandra |
| 3 | Cooldown + reîncercare pe alt nod | asta e demonstrația de failover |
| 4 | Cache + antetele `X-Cache` / `X-Served-By` | de aici se vede ceva la prezentare |
| 5 | Invalidarea la scriere | altfel servești date vechi |
| 6 | Redis + `/proxy/clients` + `/proxy/stats` | a treia funcție cerută |
| 7 | Dockerfile + compose | |
| 8 | Bridge-ul | doar dacă varianta B e aprobată; e independent de proxy |

Dacă timpul se strânge, **bridge-ul e primul la care renunți** — e bonus, nu cerință. Proxy-ul nu.

---

## 10. Gata când (bifează)

- [ ] Tot traficul clienților trece prin proxy; warehouse-urile nu au porturi publicate
- [ ] Round-robin: 9 cereri se împart egal pe 3 noduri
- [ ] Un warehouse oprit **nu** produce erori la client
- [ ] `X-Cache: HIT/MISS` și `X-Served-By` apar în fiecare răspuns
- [ ] Cache-ul se invalidează la `PUT` și `POST`
- [ ] JSON și XML nu se amestecă în cache
- [ ] `/proxy/clients` arată clienții din Redis, cu numărul de cereri
- [ ] Redis oprit → proxy-ul servește în continuare
- [ ] (varianta B) Un mesaj publicat la broker ajunge în Cassandra și se poate citi prin proxy
- [ ] (varianta B) Warehouse picat → fără ACK, brokerul reîncearcă, datele ajung după revenire
