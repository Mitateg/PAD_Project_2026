# Contract mesaj v1 (varianta cu meniu si canale private)

> **Transportul descris aici nu mai este in vigoare.** Documentul a fost scris pentru Partea 1,
> cand mesajele circulau ca JSON pe o linie, peste socketuri TCP. Din Partea 2 sistemul foloseste
> **gRPC cu protobuf** (vezi [ADR-002](../docs/decisions/adr-002-grpc-si-protobuf.md)), iar
> `src/common/Message.cs` si `LineReader.cs` au fost sterse din cod.
>
> Ce s-a schimbat si ce a ramas:
>
> | Aici scrie | In cod acum |
> |---|---|
> | JSON pe o singura linie | protobuf binar, incadrat de HTTP/2 |
> | clasa `Pad.Common.Message` | `Pad.Common.Grpc.BrokerMessage`, generata din [`broker.proto`](../src/common/Protos/broker.proto) |
> | mesajul `HELLO` | RPC-ul `RegisterSender` (sender) si stream-ul `Subscribe` (receiver) |
> | comenzile `subscribe` / `list_receivers` | RPC-ul `GetReceivers` si `SubscriptionChange` din stream |
> | `payload` ca obiect JSON | campul `payload_json`, un string |
>
> **Campurile si regulile de rutare de mai jos raman valabile**: `messageId`, `correlationId`,
> `messageType`, tipurile publice fata de canalele private, ACK/NACK. S-a schimbat doar felul in
> care mesajul este impachetat si trimis pe fir.
>
> Documentul se pastreaza ca inregistrare istorica si nu se rescrie, la fel ca ADR-001.
> Contractul in vigoare pentru forma mesajelor este fisierul `.proto`.

Orice mesaj care circula prin sistem este un obiect JSON **pe o singura linie**, terminat cu `\n`.
Clasa C# corespunzatoare este `Pad.Common.Message` (src/common/Message.cs).

```json
{"messageId":"guid","correlationId":"guid","messageType":"Anunt","schemaVersion":1,"occurredAt":"2026-09-14T10:00:00Z","payload":{"text":"salut"}}
```

| Camp | Tip | Obligatoriu | Descriere |
|---|---|---|---|
| `messageId` | GUID string | da | unic per mesaj (`Guid.NewGuid()`) |
| `correlationId` | GUID string | da | leaga toate mesajele unei solicitari; apare in toate logurile |
| `messageType` | string | da | destinatia: un tip public (`Anunt`, `Alerta`) sau numele unui receiver (canal privat, ex. `ion-1`) |
| `schemaVersion` | int | da | incepem cu `1` |
| `occurredAt` | string ISO-8601 UTC | da | `DateTime.UtcNow.ToString("o")` |
| `payload` | obiect JSON | da | continutul; in aceasta varianta mereu `{"text":"..."}` |

## Destinatii (messageType)

- **Tipuri publice** (`Anunt`, `Alerta`, lista in `Constants.KnownMessageTypes`): mesajul ajunge la toti receiver-ii abonati la acel tip.
- **Canal privat** = numele unic al unui receiver (`ion-1`): singurul abonat este acel receiver.
  Broker-ul il aboneaza automat la pornire si nu permite dezabonarea de la el.
  Un mesaj catre un nume pe care broker-ul nu l-a vazut niciodata primeste NACK `unknown_type`.
  Un mesaj catre un receiver cunoscut dar deconectat asteapta in coada pana revine.

## HELLO (primul mesaj dupa conectare)

```json
{"role":"sender","name":"sender"}
{"role":"receiver","name":"ion","subscribeTo":["Anunt"]}
```

Broker-ul raspunde **si la sender, si la receiver** cu numele unic pe care i l-a dat.
Numele cerut primeste cel mai mic numar liber: `ion` devine `ion-1`, iar daca `ion-1` este deja
conectat, urmatorul devine `ion-2`. Numele se elibereaza la deconectare.

```json
{"status":"ACK","assignedName":"ion-1"}
{"status":"ACK","assignedName":"sender-2"}
```

Diferenta dintre cele doua roluri:

- la **receiver**, numele este si canalul lui privat, deci se foloseste la rutare;
- la **sender**, numele conteaza doar in loguri, ca sa se vada cine a trimis fiecare mesaj.

Daca broker-ul nu trimite acest raspuns intr-o secunda, sender-ul merge mai departe cu numele cerut,
fara sa se blocheze.

## Raspunsuri broker (dupa fiecare mesaj de la sender)

```json
{"status":"ACK","messageId":"<id-ul mesajului confirmat>"}
{"status":"NACK","messageId":"<id sau null>","reason":"invalid_json | missing_field:messageType | unknown_type"}
```

Sender-ul considera mesajul livrat brokerului DOAR dupa ce primeste ACK.

## ACK de la receiver (dupa procesare)

```json
{"status":"ACK","messageId":"<id-ul mesajului procesat>"}
```

## Comenzi (dupa HELLO)

Receiver -> broker, schimbarea abonarilor publice din mers:

```json
{"action":"subscribe","type":"Alerta"}
{"action":"unsubscribe","type":"Alerta"}
```

Sender -> broker, lista receiver-ilor conectati (ca sa aleaga un canal privat):

```json
{"action":"list_receivers"}
{"receivers":["ana-1","ion-1","ion-2"]}
```

## Exemple

Fisierele din `contracts/examples/` sunt folosite de testele de contract (`tests/contract`):

- `valid-*.json` trebuie sa treaca validarea
- `invalid-*.json` trebuie sa pice validarea, cu motivul din numele fisierului
