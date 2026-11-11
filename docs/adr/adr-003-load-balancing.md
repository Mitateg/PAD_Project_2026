# ADR-003: Failover pe partea de client, cu lista ordonata de brokere

**Status:** acceptat
**Data:** 2026-10-19

## Context

Cerinta: *"Porneste cel putin 2 brokere. Da-le senderului si receiverului o lista de adrese:
senderul trebuie sa incerce urmatorul broker cand primeste UNAVAILABLE, iar receiverul sa se
reconecteze la altul."*

Un broker unic este un **punct unic de esec**: daca pica, tot sistemul se opreste.

## Decizie

- Clientii (sender si receiver) primesc o **lista ordonata** de adrese, prin `--brokers` sau prin
  variabila de mediu `PAD_BROKER_ADDRESSES`. Fara ea, se foloseste `--host:--port`, ca inainte.
- Clientul lucreaza cu **un singur broker la un moment dat**, mereu primul care raspunde din lista.
- La `UNAVAILABLE`, clientul trece la **urmatoarea adresa** si se reinregistreaza acolo.
  Receiver-ul isi reface si abonarile.
- Clientul parcurge lista **in cerc**. Pauza (`Constants.RetryDelay`) vine abia dupa ce a incercat
  toate adresele fara succes, nu intre doua adrese.
- Un **`NACK` nu declanseaza comutarea**: mesajul a fost refuzat pe fond, deci ar fi refuzat
  identic si de celalalt broker.
- Broker-ul primeste `--port`, `--name` si `--wal`, ca sa putem porni mai multe instante pe aceeasi
  masina fara sa se bata pe acelasi port sau pe acelasi fisier.

Pentru ca lista este **ordonata**, al doilea broker se comporta in practica drept **rezerva**:
toti clientii pornesc de la prima adresa si ajung la a doua abia cand prima nu mai raspunde.

## Alternative respinse

| Alternativa | De ce nu |
|---|---|
| Active-passive cu adresa comuna (IP virtual, reverse proxy, cluster de failover) | Este solutia "corecta" in productie si pastreaza aceeasi adresa pentru clienti, dar se **configureaza pe infrastructura**, nu se scrie in aplicatie. Nu ar fi demonstrat nimic din cod, iar cerinta spune explicit "lista de adrese" si "sa se reconecteze la **altul**" |
| Alegere aleatoare / round-robin la fiecare mesaj | Ar imprastia mesajele intre brokere care nu-si impart starea, deci un receiver conectat la unul singur ar pierde mesaje |
| Brokere care isi replica starea intre ele | Ar fi raspuns la problema reala (vezi limitele de mai jos), dar inseamna consens intre noduri - mult peste ce cere laboratorul |

## Consecinte

- Sistemul supravietuieste caderii unui broker: am verificat ca sender-ul si receiver-ul migreaza
  amandoi pe a doua adresa si ca mesajele continua sa ajunga.
- Sender-ul se reinregistreaza pe noul broker, deci **poate primi alt nume** (de exemplu `sender-2`).
  Fisierul de log ramane cel dintai, ca sa nu se imprastie istoricul.
- Nu exista o componenta in plus de pornit si de intretinut (proxy, load balancer).

## Limite cunoscute

- **Brokerele nu isi impart starea.** Fiecare are cozile si WAL-ul lui. Un mesaj trimis catre
  `broker-1` nu ajunge la un receiver conectat doar la `broker-2`. Este **failover, nu replicare**:
  sistemul ramane disponibil, dar mesajele aflate in broker-ul cazut asteapta repornirea lui.
- Pentru demonstratie, toti clientii trebuie sa primeasca **aceeasi lista**, altfel se imprastie
  pe brokere diferite si par ca nu comunica intre ei.
- Clientii nu se intorc singuri pe primul broker dupa ce acesta revine. Raman pe cel curent pana
  cand si acela cade.
- Un receiver care se reconecteaza **foarte repede** poate primi un nume nou (`ana-2`), pentru ca
  broker-ul elibereaza numele abia cand observa deconectarea. In cazul acela, mesajul neconfirmat
  al lui `ana-1` nu ii mai revine si ajunge la retry, apoi in dead-letter.
