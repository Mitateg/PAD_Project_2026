# Dovezi: load test prin proxy (Lab 3)

Rulari de incarcare prin proxy, cu comenzile exacte si rezultatele neprelucrate. Scopul este sa
arate ca **cache-ul si load balancing-ul chiar functioneaza**, si cat de mult: trei scenarii cerute
(cache gol, cache cald, un warehouse oprit in timpul rularii) plus doua rulari de control care
explica cifrele.

**Data:** 2026-11-15 · **Unealta:** `tools/loadtest` · **Cod:** `feature/sender` peste `f0fee36`

---

## Cum au fost pornite componentele

Pe masina de test nu era instalat Docker, deci nodurile au rulat ca **procese locale**, nu in
containere:

```bash
# 3 noduri warehouse, stocare in memorie (PAD_CASSANDRA_HOST nesetata)
PAD_PORT=5001 PAD_WAREHOUSE_NAME=warehouse-1 dotnet run --project src/warehouse &
PAD_PORT=5002 PAD_WAREHOUSE_NAME=warehouse-2 dotnet run --project src/warehouse &
PAD_PORT=5003 PAD_WAREHOUSE_NAME=warehouse-3 dotnet run --project src/warehouse &

# proxy peste cele 3 noduri, fara Redis
PAD_WAREHOUSE_NODES="http://localhost:5001,http://localhost:5002,http://localhost:5003" \
  dotnet run --project src/proxy &
```

Pentru ca nodurile in memorie **nu** isi impart datele (in compose asta o face Cassandra), acelasi
angajat a fost scris **direct pe fiecare nod**, o singura data, inainte de masuratori. Scrierea de
seed ocoleste intentionat proxy-ul: altfel ar fi ajuns la un singur nod si `GET`-urile ar fi dat
`404` pe celelalte doua.

```bash
for p in 5001 5002 5003; do
  curl -s -X PUT -H "Content-Type: application/json" \
    -d '{"id":1,"name":"Ion Popescu","position":"Engineer","department":"R&D","updatedAt":"2026-11-15T09:00:00Z"}' \
    "http://localhost:$p/employee/"
done     # 201, 201, 201
```

### Ce nu acopera aceste rulari

| Limita | De ce conteaza |
|---|---|
| Stocare in memorie, nu Cassandra | Nu se testeaza stocarea comuna, doar comportamentul proxy-ului. Cifrele de latenta spre nod sunt mai mici decat cu Cassandra. |
| Redis nu rula (`PAD_REDIS` nesetata) | Evidenta clientilor si `/proxy/clients` nu sunt masurate aici. `/health` a raportat corect `"redis":"unavailable"`, iar proxy-ul a functionat normal fara el. |
| Un singur proces de proxy, pe `localhost` | Fara latenta de retea reala intre proxy si noduri. |

Inainte de prezentare, rularile merita repetate pe stiva din `docker compose`, ca sa intre si
Cassandra si Redis in masuratoare.

---

## Scenariul 1 — cache gol

```bash
dotnet run --project tools/loadtest -- \
  --clients 20 --requests 10 --path "/employee/?id=1" --accept json
```

`/proxy/stats` inainte: `{"totalRequests":2,"cacheHits":0,"cacheMisses":0,"requestsPerNode":{},"sickNodes":[]}`

```
Total      : 200 cereri in 2.5 s  ->  81 cereri/s
Latenta    : p50 = 1 ms   p95 = 2441 ms   max = 2447 ms
Coduri     : 200 200 (100%)
Cache      : HIT 180 (90%)  ·  MISS 20 (10%)
Noduri     : warehouse-1 186 (93%)  ·  warehouse-2 7 (4%)  ·  warehouse-3 7 (4%)
```

`/proxy/stats` dupa: `{"totalRequests":203,"cacheHits":180,"cacheMisses":20,"requestsPerNode":{"warehouse-1":6,"warehouse-2":7,"warehouse-3":7},"sickNodes":[]}`

**Ce se citeste aici:**

- Exact **20 de MISS** pentru 20 de clienti: fiecare client a ratat prima cerere si a lovit cache-ul
  la celelalte noua. Cei 20 au pornit deodata, cand cache-ul era gol, deci au mers **toti** la
  noduri pentru *acelasi* angajat. Proxy-ul nu are coalescing (vezi
  [ADR-004, Limite cunoscute](../decisions/adr-004-proxy-si-caching.md#limite-cunoscute)).
- Cele 20 de MISS se regasesc 1:1 in `requestsPerNode`: 6 + 7 + 7 = 20 cereri spre noduri,
  impartite egal. **Asta** e load balancing-ul.
- Distributia pe noduri raportata de client (93% / 4% / 4%) **nu** este distributia reala. Un
  raspuns servit din cache pastreaza `X-Served-By` al nodului care l-a produs initial, deci cele
  180 de HIT-uri arata toate acelasi nod. Distributia reala se citeste din `/proxy/stats`, nu din
  antetele vazute de client.

---

## Scenariul 2 — cache cald

Incalzire cu **acelasi** `Accept` pe care il trimite load test-ul, apoi rulare identica:

```bash
curl -s -D - -H "Accept: application/json" "http://localhost:8080/employee/?id=1" -o /dev/null | grep X-Cache
# X-Cache: MISS      <- prima cerere umple cache-ul
curl -s -D - -H "Accept: application/json" "http://localhost:8080/employee/?id=1" -o /dev/null | grep X-Cache
# X-Cache: HIT       <- cache-ul e cald

dotnet run --project tools/loadtest -- \
  --clients 20 --requests 10 --path "/employee/?id=1" --accept json
```

`/proxy/stats` inainte: `{"totalRequests":409,"cacheHits":362,"cacheMisses":41,"requestsPerNode":{"warehouse-1":13,"warehouse-2":14,"warehouse-3":14},"sickNodes":[]}`

```
Total      : 200 cereri in 2.1 s  ->  93 cereri/s
Latenta    : p50 = 1 ms   p95 = 2122 ms   max = 2124 ms
Coduri     : 200 200 (100%)
Cache      : HIT 200 (100%)
Noduri     : warehouse-3 200 (100%)
```

`/proxy/stats` dupa: `{"totalRequests":610,"cacheHits":562,"cacheMisses":41,"requestsPerNode":{"warehouse-1":13,"warehouse-2":14,"warehouse-3":14},"sickNodes":[]}`

**Ce se citeste aici:** `requestsPerNode` este **neschimbat** inainte si dupa (13/14/14), iar
`cacheMisses` a ramas 41. Cele 200 de cereri au fost servite **integral din cache**, fara nicio
cerere spre warehouse. Asta este dovada cache-ului, mai tare decat o diferenta de timp:
**200 de cereri, 0 cereri spre noduri.**

### De ce nu se vede in latenta: prima incercare, gresita

Prima rulare a scenariului 2 a dat tot 90% HIT, ca si cache-ul gol. Cauza nu era cache-ul, ci
incalzirea: `curl` fara `-H "Accept: ..."` trimite `Accept: */*`, iar proxy-ul tine `Accept`
**in cheia de cache**. Deci incalzirea a umplut intrarea `*/*`, iar load test-ul a cerut
`application/json` — o alta intrare, inca goala.

```
Cache      : HIT 181 (90%)  ·  MISS 19 (10%)      <- incalzire cu Accept: */*
Cache      : HIT 200 (100%)                        <- incalzire cu Accept: application/json
```

Greseala merita pastrata, pentru ca este chiar demonstratia deciziei din ADR-004: **formate
diferite sunt intrari diferite in cache.**

---

## Control — de unde vine p95 de 2.1 s

In toate rularile, `p50` este 0-1 ms dar `max` este ~2.1 s. Aceeasi rulare cu mai putini clienti,
acelasi numar total de cereri, arata de ce:

```bash
dotnet run --project tools/loadtest -- \
  --clients 2 --requests 100 --path "/employee/?id=1" --accept json
```

```
Total      : 200 cereri in 2.1 s  ->  93 cereri/s
Latenta    : p50 = 0 ms   p95 = 1 ms   max = 2088 ms
Cache      : HIT 200 (100%)
```

**O singura** cerere lenta (2088 ms), restul sub 1 ms. Costul este al **primei cereri**: pornirea
procesului, JIT-ul si deschiderea primei conexiuni TCP. Cu 20 de clienti porniti deodata sunt 20 de
astfel de prime cereri, adica 10% din 200 — exact cat trebuie ca `p95` sa cada peste ele.

**Concluzie pentru citirea rezultatelor:** la aceste dimensiuni, `p95`, `max` si `cereri/s` sunt
dominate de pornire, nu de cache. Efectul cache-ului se citeste pe **raportul HIT/MISS** si pe
**numarul de cereri ajunse la noduri**. Pentru cifre de latenta comparabile ar trebui o faza de
incalzire separata, care nu intra in masuratoare.

---

## Scenariul 3 — un warehouse oprit in timpul rularii

Calea `/update/employees/` **nu se cacheaza** (exclusa explicit in proxy), deci fiecare cerere
ajunge la un nod. Asta e calea potrivita pentru a vedea load balancing si failover: pe
`/employee/?id=1` cache-ul ar fi servit tot si nu ar fi plecat nicio cerere spre noduri.

Verificare prealabila — niciun HIT, distributie egala:

```bash
dotnet run --project tools/loadtest -- \
  --clients 5 --requests 20 --path "/update/employees/?since=2026-01-01T00:00:00Z" --accept json
```

```
Total      : 100 cereri in 2.3 s  ->  44 cereri/s
Latenta    : p50 = 6 ms   p95 = 44 ms   max = 2160 ms
Coduri     : 200 100 (100%)
Cache      : MISS 100 (100%)
Noduri     : warehouse-3 34 (34%)  ·  warehouse-1 33 (33%)  ·  warehouse-2 33 (33%)
```

Rularea cu oprire la mijloc:

```bash
# 8000 de cereri, ~9,5 s
dotnet run --project tools/loadtest -- \
  --clients 8 --requests 1000 --path "/update/employees/?since=2026-01-01T00:00:00Z" --accept json &

sleep 5          # la t=5 s, cu rularea in curs, se opreste warehouse-2
# Windows: procesul care asculta pe 5002
powershell -Command "Get-NetTCPConnection -LocalPort 5002 -State Listen |
  Select-Object -First 1 -ExpandProperty OwningProcess | ForEach-Object { Stop-Process -Id $_ -Force }"
# echivalent in compose: docker compose stop warehouse-2
```

| Moment | `/proxy/stats` |
|---|---|
| inainte | `{"totalRequests":914,...,"requestsPerNode":{"warehouse-1":47,"warehouse-2":48,"warehouse-3":48},"sickNodes":[]}` |
| imediat dupa oprire (t=5 s) | `{"totalRequests":7498,...,"requestsPerNode":{"warehouse-1":2363,"warehouse-2":2004,"warehouse-3":2364},"sickNodes":["warehouse-2"]}` |
| la final | `{"totalRequests":8916,"cacheHits":763,"cacheMisses":41,"requestsPerNode":{"warehouse-1":3072,"warehouse-2":2004,"warehouse-3":3072},"sickNodes":["warehouse-2"]}` |

```
Total      : 8000 cereri in 9.5 s  ->  844 cereri/s
Latenta    : p50 = 7 ms   p95 = 14 ms   max = 2136 ms
Coduri     : 200 8000 (100%)
Cache      : MISS 8000 (100%)
Noduri     : warehouse-1 3025 (38%)  ·  warehouse-3 3024 (38%)  ·  warehouse-2 1951 (24%)
```

**Ce se citeste aici:**

- **Nicio cerere nu a esuat.** Toate cele 8000 au primit `200`, desi un nod din trei a disparut la
  mijlocul rularii. Clientul nu a vazut niciun `502` si niciun timeout: proxy-ul a reluat cererea
  pe alt nod **in cadrul aceleiasi cereri HTTP**.
- **Nodul oprit a fost scos din rotatie.** `requestsPerNode` pentru `warehouse-2` a inghetat la
  2004 si nu a mai crescut pana la final, iar `sickNodes` l-a raportat corect. Celelalte doua au
  preluat tot: 38% + 38% fata de 24% ramase la cel oprit.
- **Costul failover-ului, in cifre:** proxy-ul a numarat 2004 *incercari* catre `warehouse-2`, dar
  clientii au vazut doar 1951 de raspunsuri servite de el. Diferenta de **53** sunt incercarile
  care au esuat si au fost reluate pe alt nod. Alea 53 sunt, practic, pretul caderii — platit o
  singura data, nu pe fiecare cerere, pentru ca nodul a intrat in cooldown
  (`NodeFailureCooldown = 10s`).
- `p95 = 14 ms` cu un nod mai putin: cele doua noduri ramase au dus 844 cereri/s fara degradare
  vizibila.
- `cacheHits` si `cacheMisses` **nu s-au miscat** (763 / 41) pe tot parcursul rularii, ceea ce
  confirma ca `/update/employees/` nu intra niciodata in cache.

---

## Pe scurt

| Ce trebuia dovedit | Unde se vede | Rezultat |
|---|---|---|
| Cache-ul chiar serveste | Scenariul 2 | 200 de cereri, **0** spre noduri |
| `Accept` e in cheia de cache | Scenariul 2, prima incercare | `*/*` si `application/json` = intrari diferite |
| Round-robin peste noduri | Scenariul 1 (6/7/7), calibrare (34/33/33) | distributie egala |
| Un nod oprit nu doboara sistemul | Scenariul 3 | 8000 de cereri, **100% `200`**, 0 erori |
| Nodul mort iese din rotatie | Scenariul 3, `sickNodes` | `warehouse-2`, inghetat la 2004 incercari |
| Pull-request-ul nu se cacheaza | Scenariul 3 | `MISS 8000 (100%)` |
