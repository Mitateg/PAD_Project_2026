# ADR-004: Proxy scris de mana, cu cache pe raspunsurile GET

**Status:** acceptat
**Data:** 2026-11-15

## Context

Cerinta etapei 2 din Lab 3: *"comunicarea acestuia cu orice client sa fie prin aplicatia proxy"*.
Proxy-ul este deci **singura poarta de intrare** spre cele 3 noduri warehouse, si trebuie sa faca
trei lucruri in acelasi timp:

1. sa imparta cererile intre noduri (load balancing);
2. sa tina sistemul in picioare cand un nod cade (failover);
3. sa fie un *smart proxy*, nu doar o teava: sa pastreze raspunsurile citite des (caching).

Cele 3 noduri warehouse scriu in **aceeasi** Cassandra, deci oricare nod poate raspunde la orice
cerere. Asta e ce face load balancing-ul posibil: fara stocare comuna, doua noduri ar da raspunsuri
diferite la aceeasi cerere, si round-robin-ul s-ar vedea ca un bug la prezentare.

## Decizie

### 1. Proxy scris de mana, nu YARP si nu nginx

Proxy-ul este o aplicatie ASP.NET minimal API de un singur fisier (`src/proxy/Program.cs`), cu o
ruta de tip catch-all `/{**path}` care redirectioneaza tot ce nu este endpoint propriu.

### 2. `Accept` face parte din cheia de cache

Cheia este `GET|{cale}|{query}|{Accept}`. Acelasi angajat cerut ca JSON si ca XML sunt **doua
intrari diferite** in cache.

Warehouse-ul alege formatul raspunsului dupa antetul `Accept` (JSON sau XML, altceva da `406`).
Daca `Accept` nu ar fi in cheie, un client care cere XML ar primi JSON-ul pus in cache de altul —
un bug de tip *cache poisoning* pe formate, greu de observat si aproape imposibil de depanat la
prezentare.

Se cacheaza **numai** raspunsurile `GET` cu status `200`. Erorile (`404`, `400`, `406`) nu se
pastreaza: sunt ieftine de recalculat si s-ar lipi de sistem dupa ce cauza a disparut.

Calea `/update/employees/` este **exclusa explicit** de la caching. Ea este "pull-request"-ul din
enunt: intrebarea *"ce s-a schimbat de cand am intrebat ultima data"*. Un raspuns la intrebarea
asta este corect doar in momentul in care a fost dat, deci pastrarea lui ar fi exact greseala.
In plus, momentul vine din antetul `If-Modified-Since`, care **nu** este in cheia de cache: daca
ruta ar fi cacheabila, doua cereri cu momente diferite ar impartasi aceeasi intrare.

### 3. Invalidare la scriere, nu TTL scurt

Dupa un `PUT` sau `POST` reusit, proxy-ul **sterge** intrarile de cache ale angajatilor
(cheile care incep cu `GET|/employee`), in loc sa se bazeze pe expirarea lor.

TTL-ul exista totusi, `Constants.CacheTtl = 30s`, dar ca **plasa de siguranta**, nu ca mecanism
principal. Il justificam asa: singurele scrieri care pot scapa invalidarii sunt cele care **nu trec
prin acest proxy** — adica un `PUT` facut direct pe un nod warehouse, sau facut printr-o alta
instanta de proxy. Prima varianta nu mai exista in `docker-compose.yml` (porturile warehouse nu
sunt publicate, vezi *Consecinte*), iar a doua nu apare in configuratia laboratorului. Rezulta ca
30 s este limita superioara a cat poate rata un client o schimbare facuta pe o cale neobisnuita —
suficient de scurt pentru demonstratie, suficient de lung ca cache-ul sa aiba efect masurabil.

### 4. Cassandra ca stocare comuna

Decizia de stocare apartine warehouse-ului, dar proxy-ul **depinde** de ea: round-robin-ul este
corect doar daca nodurile vad aceleasi date. Fara Cassandra, cele 3 noduri ar avea fiecare datele
lui in memorie, si acelasi `GET` ar da `200` sau `404` in functie de nodul nimerit.

### 5. Redis doar pentru evidenta clientilor, si optional

Redis tine, pentru fiecare client, un hash `client:{id}` cu `firstSeen`, `lastSeen`, `requests` si
`lastPath`, cu expirare dupa `Constants.ClientRecordTtl = 10 min`. Clientul se identifica prin
antetul `X-Client-Id`, iar daca nu-l trimite, prin adresa IP.

Redis este **optional prin constructie**: daca `PAD_REDIS` nu este setata, sau daca Redis nu
raspunde, proxy-ul porneste si functioneaza normal, iar `/proxy/clients` raspunde
`{"error":"Redis unavailable"}`. Conexiunea foloseste `AbortOnConnectFail = false` si timeout-uri
scurte (2 s la conectare, 500 ms pe operatie), ca o problema la Redis sa nu se transforme in
indisponibilitatea proxy-ului. Datele de trafic sunt utile, dar nu sunt pe calea critica a cererii.

## Alternative respinse

| Alternativa | De ce nu |
|---|---|
| **YARP** (reverse proxy Microsoft pentru .NET) | Ar da gratis load balancing, health checks active si retry-uri, configurate din `appsettings.json`. Dar exact ele sunt **ce trebuie demonstrat**: cu YARP, round-robin-ul si cooldown-ul ar fi doua linii de configurare, nu cod al nostru. Laboratorul cere sa aratam mecanismul, nu sa-l consumam. |
| **nginx** | Acelasi argument, plus ca este un serviciu separat configurat in afara codului. Un cache in nginx nu ar sti sa se invalideze la un `PUT`: ar trebui TTL scurt, adica exact decizia 3 pe dos. |
| **Cache cu TTL scurt (1-2 s), fara invalidare** | Mai simplu de scris, dar face cache-ul aproape inutil (la 1 s, aproape fiecare cerere e MISS) si tot lasa o fereastra de date vechi. Invalidarea la scriere da raspuns corect *imediat* dupa scriere si, in acelasi timp, un TTL lung. |
| **Cache per nod, in fiecare warehouse** | Fiecare nod ar avea cache-ul lui, deci acelasi `GET` ar fi MISS pe celelalte doua. Cache-ul in proxy e lovit de toti clientii, pentru ca proxy-ul e singura cale de intrare. |
| **Redis ca loc al cache-ului de raspunsuri** | Ar permite mai multe instante de proxy cu cache comun, dar in laborator exista o singura instanta, iar un salt in retea pe fiecare citire ar face cache-ul mai lent decat `ConcurrentDictionary`-ul din memorie. |
| **`X-Served-By` scos din raspuns** | Ar fi mai curat ca "productie" (dezvaluie topologia interna), dar fara el nu se **vede** load balancing-ul la prezentare. La fel `X-Cache`. Sunt antete de demonstratie, asumate ca atare. |

## Consecinte

- **Clientul nu poate ocoli proxy-ul.** In `docker-compose.yml`, nodurile warehouse folosesc
  `expose` si nu `ports`, Cassandra si Redis la fel; singurul port publicat spre gazda este `8080`
  al proxy-ului. Asta nu e doar curatenie: un `PUT` facut direct pe un nod ar ocoli invalidarea
  cache-ului, deci proxy-ul ar servi date vechi pana la expirarea TTL-ului.
- **Failover-ul este transparent pentru client.** Un nod care raspunde `5xx` sau nu raspunde deloc
  este marcat bolnav pentru `Constants.NodeFailureCooldown = 10s`, iar cererea se reia pe alt nod
  **in aceeasi cerere HTTP**. Clientul nu vede eroarea. `502` apare numai cand toate nodurile au
  fost incercate si au esuat.
- **Masurat** (vezi [`docs/evidence/lab3-loadtest.md`](../evidence/lab3-loadtest.md)): cu un
  warehouse oprit in timpul unei rulari de 8000 de cereri, **toate** au primit `200` si nicio
  cerere nu a esuat; 53 de incercari au fost redirectionate pe alt nod.
- **Idempotenta lui `PUT` devine obligatorie,** nu optionala: proxy-ul reia cererea pe alt nod,
  deci acelasi `PUT` poate ajunge de doua ori in warehouse. De aceea contractul cere `PUT` upsert
  (vezi [`contracts/warehouse-v1.md`](../../contracts/warehouse-v1.md)).
- Proxy-ul nu are stare pe disc. Reporneste cu cache gol si cu toate nodurile considerate sanatoase.

## Limite cunoscute

Scrise aici ca sa fie vizibile, nu ascunse. Primele patru sunt in lucru pe partea proxy.

- **Nu exista contor de generatie.** O citire pornita *inainte* de o scriere se poate memora in
  cache *dupa* ea: raspunsul vechi ajunge in cache dupa invalidare si traieste pana la TTL.
  Fereastra este de ordinul duratei unei cereri, dar exista.
- **Numarul de intrari nu este limitat.** Cache-ul este un `ConcurrentDictionary` fara plafon si
  fara curatare de fundal: o intrare expirata pentru o cale care nu se mai cere niciodata ramane in
  memorie. La un numar mare de chei distincte (`?id=` diferit la fiecare cerere), memoria creste
  nelimitat.
- **O scriere cu rezultat necunoscut nu invalideaza cache-ul.** Daca un `PUT` da timeout,
  invalidarea nu se executa (codul cere `resp.IsSuccessStatusCode`), desi scrierea s-ar putea sa
  fi reusit la nod. Corect ar fi sa invalidam si pe necunoscut.
- **Nu exista verificare activa a nodurilor.** Nodurile au `/health`, dar proxy-ul nu il apeleaza
  periodic: un nod aflat deja jos este descoperit abia la **prima cerere esuata**, care plateste
  pana la `ProxyRequestTimeout = 3s`.
- **Nu exista coalescing.** La cache gol, N cereri identice simultane dau N MISS si N cereri spre
  noduri: masurat, 20 de clienti porniti deodata au produs 20 de MISS si 20 de cereri spre
  warehouse pentru *acelasi* angajat.
- **Redis este pe calea cererii.** Fiecare cerere face 5 operatii Redis secventiale inainte de a fi
  redirectionata. Timeout-urile sunt mici (500 ms) si erorile sunt inghitite, deci nu poate opri
  cererea, dar adauga latenta. Corect ar fi un pipeline sau o scriere asincrona, in afara cai.
- **`requestsPerNode` din `/proxy/stats` numara incercari, nu reusite.** Un nod care a esuat apare
  cu cererea la activ, desi raspunsul a venit de la altul. Diferenta se vede comparand cu
  `X-Served-By` numarat de client (2004 incercari vs 1951 raspunsuri in rularea cu nod oprit).
- **Cache-ul nu respecta `If-Modified-Since` pe `/employee`.** Nu e o problema azi, pentru ca
  antetul se foloseste doar pe `/update/employees/`, care nu se cacheaza.
