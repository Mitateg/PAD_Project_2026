# ADR-001: Transport TCP peste Socket brut, mesaje JSON delimitate prin newline

**Status:** acceptat
**Data:** 2026-09-17

## Context

Cerinta laboratorului: cream singuri socketul si listener-ul, fara wrappere (`TcpListener`/`TcpClient`)
si fara biblioteci de messaging. Avem 3 componente (sender, broker, receiver) scrise de 3 persoane
in paralel, deci protocolul trebuie sa fie simplu si usor de testat manual.

## Decizie

- Clasa `System.Net.Sockets.Socket` cu `Bind` / `Listen` / `Accept` / `Connect` manual.
- Transport **TCP**: mesajele nu au voie sa se piarda sau sa se altereze.
- Format **JSON** pe o singura linie, UTF-8, serializat fara indentare.
- Framing propriu: **un mesaj = o linie**, delimitata de `\n` (clasa `LineReader` din `src/common`).
- **ACK/NACK explicit** de la broker dupa fiecare mesaj; sender-ul considera mesajul livrat doar dupa ACK.
- Un mesaj **HELLO** obligatoriu dupa conectare, prin care clientul spune ce rol are si (receiver) la ce tipuri se aboneaza.

## Alternative respinse

| Alternativa | De ce nu |
|---|---|
| UDP | pierde pachete, nu garanteaza ordinea; inacceptabil pentru un broker de mesaje |
| length-prefix framing (4 octeti lungime + continut) | mai greu de testat manual cu telnet/netcat |
| XML | mai verbos, parsare mai grea; JSON are suport nativ in .NET (`System.Text.Json`) |
| `TcpListener`/`TcpClient` | interzise de cerinta labului (sunt wrappere peste `Socket`) |

## Consecinte

- Simplu de testat: te poti conecta cu telnet si scrie JSON de mana.
- Payload-ul nu poate contine newline neescapat (JSON oricum il escapeaza ca `\n`).
- Fiecare conexiune are propriul task in broker; concurenta pe cozi se rezolva cu `ConcurrentQueue`.

## Limite cunoscute (v1.0)

- Fara persistenta pe disc: daca broker-ul cade, mesajele din memorie se pierd.
- Setul de `messageId` procesate din receiver este in memorie: la restart complet, receiver-ul uita ce a procesat.
