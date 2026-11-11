# ADR-002: Transport gRPC cu contract protobuf

**Status:** acceptat
**Data:** 2026-10-17
**Inlocuieste:** [ADR-001](adr-001-tcp-si-framing.md)

## Context

Partea a 2-a a laboratorului cere inlocuirea socketurilor cu un **RPC** si o reprezentare de date
proprie acestuia (proto / thrift / avro / graphql). Arhitectura ramane aceeasi: sender, broker,
receiver, cu aceleasi garantii de livrare. Se schimba doar nivelul la care comunica.

Transportul scris de mana din ADR-001 ne costa cod pe care nu-l mai voiam: framing pe linii,
serializare JSON facuta de noi, reconectare, citirea raspunsului corect pentru cererea corecta.

## Decizie

- **gRPC** peste HTTP/2 **necriptat** (h2c), pe acelasi port `4242`.
- **protobuf** ca reprezentare a datelor. Contractul unic:
  [`src/common/Protos/broker.proto`](../../src/common/Protos/broker.proto).
- Codul client **si** server se genereaza din acelasi `.proto`, in `Pad.Common`
  (`<Protobuf ... GrpcServices="Both" />`). Nimeni nu scrie de mana structuri de mesaje.
- Broker-ul devine o aplicatie **ASP.NET Core / Kestrel**, configurata **doar HTTP/2**
  (gRPC nu merge pe HTTP/1.1).
- Patru metode in `service BrokerService`:

  | Metoda | Fel | Cine o cheama | Inlocuieste din ADR-001 |
  |---|---|---|---|
  | `RegisterSender` | unar | sender | mesajul `HELLO` al sender-ului |
  | `SendMessage` | unar | sender | scrierea mesajului + citirea liniei de ACK |
  | `GetReceivers` | unar | sender | `{"action":"list_receivers"}` |
  | `Subscribe` | flux bidirectional | receiver | `HELLO` + bucla de citire + `subscribe`/`unsubscribe` + ACK-uri |

- **ACK/NACK raman in corpul raspunsului** (`SendReply.status`), **nu** ca stare de eroare gRPC.
  Motivul: cele doua inseamna lucruri diferite pentru client. `NACK` este un refuz **pe fond**
  (camp lipsa, tip necunoscut) si nu are rost reincercat, pe cand o eroare de transport
  (`UNAVAILABLE`, `DEADLINE_EXCEEDED`) **merita** reincercata. Daca am fi folosit coduri gRPC
  pentru ambele, clientul nu le-ar mai fi putut deosebi.

## Alternative respinse

| Alternativa | De ce nu |
|---|---|
| Apache Thrift | gRPC are suport nativ in .NET si generare de cod direct din `dotnet build`; Thrift ar fi cerut unelte externe |
| GraphQL | gandit pentru interogari peste un graf de date, nu pentru livrare de evenimente catre abonati |
| REST + JSON | nu este RPC si nu are flux bidirectional; receiver-ul ar fi trebuit sa faca polling |
| TLS (`https://`) | ar fi cerut certificate pe fiecare masina si in fiecare container, fara castig intr-un laborator care ruleaza pe `localhost` si in Docker |
| `payload` tipizat in proto | continutul mesajului difera de la caz la caz; ramane JSON intr-un `string` (`payload_json`), ca broker-ul sa nu fie nevoit sa-l inteleaga ca sa-l ruteze |

## Consecinte

**Ce am castigat**

- Au disparut din `src/common`: `LineReader`, `Message`, `HelloMessage`, `BrokerReply`,
  `ControlMessage` - aproximativ 150 de linii de framing si serializare pe care nu le mai intretinem.
- Contractul e verificat de **compilator**: daca cineva schimba `.proto` si altcineva nu si-a
  regenerat codul, proiectul nu mai compileaza. Inainte, dezacordul aparea abia la rulare.
- Fiecare apel RPC isi are **propriul raspuns**. In ADR-001 exista o problema reala:
  un ACK intarziat putea fi citit drept raspuns la cererea urmatoare. Acum nu mai e posibil.
- Canalul gRPC se **reconecteaza singur**; nu mai scriem noi logica de refacere a socketului.
- Mesajele circula **binar**, nu ca text.

**Ce am pierdut sau ce ne costa**

- Nu mai poti testa cu `telnet` sau `netcat`, asa cum lauda ADR-001. Pentru inspectie manuala
  e nevoie de `grpcurl` sau de testele de integrare.
- Broker-ul depinde acum de **ASP.NET Core runtime**, nu doar de .NET de baza.
- Traficul este **necriptat**. Acceptabil pe `localhost` si intr-o retea Docker interna,
  dar nu ar fi acceptabil in productie.

## Limite cunoscute

- `payload_json` este un `string`: protobuf nu valideaza continutul mesajului, doar plicul.
- Fara TLS si fara autentificare - orice proces care ajunge la port poate trimite mesaje.
- `schema_version` exista in mesaj, dar nimeni nu refuza inca o versiune necunoscuta.
