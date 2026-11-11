# Contract warehouse v1 (Lab 3)

Warehouse-ul (DW) este un server HTTP care tine angajatii. Toate componentele (proxy, client, bridge)
se scriu dupa acest contract. Modelul C# este `Pad.Common.Models.Employee` (src/common/Models/Employee.cs).

## Modelul `Employee`

```json
{ "id": 1, "name": "Ion Popescu", "position": "Engineer", "department": "R&D", "updatedAt": "2026-11-09T10:00:00Z" }
```

| Camp | Tip | Descriere |
|---|---|---|
| `id` | int > 0 | cheia angajatului |
| `name` | string | obligatoriu la `PUT` (nevid) |
| `position`, `department` | string | optionale |
| `updatedAt` | ISO-8601 UTC | momentul ultimei schimbari; la `PUT` il poate trimite clientul (vezi mai jos) |

In XML, elementele au numele proprietatilor (`<Employee><Id>1</Id><Name>...</Name>...</Employee>`).

## Endpoint-uri

| Metoda | Cale | Scop | Succes | Erori |
|---|---|---|---|---|
| `GET` | `/employee/?id={id}` | un angajat | `200` | `404` negasit, `400` id invalid |
| `GET` | `/employees/?offset={o}&limit={l}` | o pagina | `200` (lista, eventual goala) | `400` parametri invalizi |
| `PUT` | `/employee/` | creare sau inlocuire (upsert idempotent) | `201` creat / `200` exista deja | `400` corp invalid, `415` content type nesuportat |
| `POST` | `/employee/?id={id}` | modificare partiala (doar campurile trimise) | `200` | `404`, `400` |
| `GET` | `/update/employees/` + antetul `If-Modified-Since` | schimbarile de dupa un moment (pull-request) | `200` | `400` |
| `GET` | `/health` | `200` doar daca stocarea raspunde | `200` | `503` |

- **Format:** clientul alege cu `Accept: application/json` sau `Accept: application/xml`. Alt `Accept` primeste `406`.
  Corpul cererilor are `Content-Type: application/json` (sau `application/xml`).
- **Paginare:** `limit` implicit `10`, maxim `100`; un `limit` mai mare este redus la `100` (nu da eroare).
  `offset < 0` sau `limit < 1` dau `400`.
- **`PUT` idempotent:** acelasi `PUT` de doua ori da `201` si apoi `200`. Proxy-ul poate reincerca pe alt nod fara efecte.
- **`POST`** cu `{"name":"X"}` schimba doar numele; `position` si `department` raman.
- **Pull-request:** momentul vine in antetul `If-Modified-Since`, ca in enuntul laboratorului:
  `If-Modified-Since: Sat, 29 Oct 2014 19:43:31 GMT` (data HTTP; se accepta si ISO-8601). Data HTTP are rezolutie de o secunda,
  iar ziua saptamanii se ignora (exemplul din enunt are ziua gresita). Varianta alternativa, fara antet:
  `?since={ISO-8601}`; daca vin amandoua, castiga antetul. Un antet care nu e data da `400`, la fel lipsa ambelor.
  Un `since` cu offset `+` se trimite URL-encoded (ex. `%2B02%3A00`); varianta `...Z` nu are nevoie.
- **`From: nodename@hostdomain`** (optional, la orice cerere) identifica nodul care cere; warehouse-ul il scrie in log.
- **Proxy:** trebuie sa trimita mai departe antetele `If-Modified-Since` si `From` (nu doar `Accept` si `Content-Type`).
  Raspunsul la `/update/employees/` depinde de antet, deci nu se pune in cache sau cheia de cache include antetul.

## Ordinea schimbarilor (last-write-wins)

Brokerul nu garanteaza ordinea. Daca la `PUT` corpul contine `updatedAt`, acela este momentul scrierii:
o scriere cu `updatedAt` mai vechi decat ce exista deja **nu** suprascrie datele mai noi (raspunsul este `200` cu
varianta stocata). Daca `updatedAt` lipseste, se foloseste momentul primirii cererii.

Bridge receiver-ul pune `OccurredAt` din mesajul `EmployeeChange` in `employee.updatedAt` inainte de `PUT`.

## Mesajul `EmployeeChange` (prin broker)

`messageType` = `EmployeeChange` (`Constants.EmployeeChangeType`, in `Constants.KnownMessageTypes`). `payloadJson`:

```json
{ "op": "upsert", "employee": { "id": 1, "name": "...", "position": "...", "department": "..." } }
```
