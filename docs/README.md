# Documentatia proiectului

Fiecare folder de aici tine **un fel de document**, nu un laborator si nu o versiune.
Asa stii unde sa cauti fara sa ghicesti, si unde sa pui ceva nou.

| Folder | Ce contine | Se schimba? |
|---|---|---|
| [`decisions/`](decisions/) | De ce am ales ce am ales (ADR) | Nu se rescriu. O decizie depasita se marcheaza *inlocuita* si se scrie una noua. |
| [`guides/`](guides/) | Cum se ruleaza si cum se opereaza | Se actualizeaza cand se schimba modul de rulare. |
| [`planning/`](planning/) | Cine ce are de facut, pe laboratoare | Temporar. Dupa predare raman doar ca istoric. |

Contractele **nu** sunt aici: stau in [`contracts/`](../contracts/), pentru ca de ele depinde codul
tuturor componentelor, nu doar intelegerea lui.

## Decizii de arhitectura

| ADR | Decizie | Status |
|-----|---------|--------|
| [001](decisions/adr-001-tcp-si-framing.md) | Socket TCP brut, mesaje JSON pe linii | inlocuit de 002 |
| [002](decisions/adr-002-grpc-si-protobuf.md) | gRPC peste HTTP/2, contract protobuf | acceptat |
| [003](decisions/adr-003-load-balancing.md) | Failover pe client, cu lista ordonata de brokere | acceptat |

Un ADR inlocuit **se pastreaza asa cum a fost scris**. El explica de ce am ales atunci ce am ales,
iar asta ramane adevarat chiar daca azi facem altfel. Daca l-am rescrie, am pierde exact informatia
pentru care exista.

## Ghiduri

| Ghid | Despre |
|---|---|
| [docker-sender.md](guides/docker-sender.md) | Rularea sender-ului in Docker |

## Planificare

Impartirea muncii pe Lab 3, cate un fisier pentru fiecare membru:

| Fisier | Cine | Partea lui |
|---|---|---|
| [01-sender-client-A.md](planning/01-sender-client-A.md) | A | contract, client HTTP, load test, integrare |
| [02-broker-warehouse-B.md](planning/02-broker-warehouse-B.md) | B | warehouse, Cassandra, reparatii broker |
| [03-receiver-proxy-C.md](planning/03-receiver-proxy-C.md) | C | proxy, Redis, bridge receiver |

## Unde mai e documentatie

| Loc | Ce |
|---|---|
| [`../README.md`](../README.md) | Prezentarea generala: arhitectura, pornire, scenarii de esec |
| [`../contracts/`](../contracts/) | Contractele dintre componente (mesaje, warehouse HTTP) |
| [`../src/common/Protos/broker.proto`](../src/common/Protos/broker.proto) | Contractul gRPC in vigoare, din care se genereaza codul |

## Cum se citeste proiectul pe etape

Proiectul a trecut prin trei etape. Codul de azi este cel din etapa 3; primele doua se vad in
deciziile pastrate si in istoricul git (etichetele `v1.0`, `v2.0`).

| Etapa | Transport | Documentul care o explica |
|---|---|---|
| 1 | socketuri TCP, JSON pe linii | [ADR-001](decisions/adr-001-tcp-si-framing.md), [contracts/message-v1.md](../contracts/message-v1.md) |
| 2 | gRPC + protobuf | [ADR-002](decisions/adr-002-grpc-si-protobuf.md), [broker.proto](../src/common/Protos/broker.proto) |
| 3 | HTTP + proxy peste warehouse | [contracts/warehouse-v1.md](../contracts/warehouse-v1.md) |
