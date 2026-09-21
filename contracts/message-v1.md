# Contract mesaj v1 (varianta cu meniu si canale private)

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
{"role":"sender","name":"sender-1"}
{"role":"receiver","name":"ion","subscribeTo":["Anunt"]}
```

Pentru receiver, broker-ul raspunde cu numele unic pe care i l-a dat (cel mai mic numar liber):

```json
{"status":"ACK","assignedName":"ion-1"}
```

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
