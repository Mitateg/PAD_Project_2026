# Decizii de arhitectura (ADR)

Fiecare fisier de aici inregistreaza **o decizie**: ce am ales, ce am respins si de ce.

Un ADR nu se rescrie cand codul se schimba. Daca o decizie nu mai este valabila, i se trece
statusul pe *inlocuit* si se adauga unul nou. Asa ramane vizibil **de ce** am ajuns aici,
nu doar unde am ajuns.

| ADR | Decizie | Status |
|-----|---------|--------|
| [001](adr-001-tcp-si-framing.md) | Socket TCP brut, mesaje JSON delimitate prin newline | inlocuit de 002 |
| [002](adr-002-grpc-si-protobuf.md) | gRPC peste HTTP/2, contract protobuf | acceptat |
| [003](adr-003-load-balancing.md) | Failover pe client, cu lista ordonata de brokere | acceptat |

ADR-001 descrie Partea 1 a laboratorului, 002 si 003 descriu Partea 2.
