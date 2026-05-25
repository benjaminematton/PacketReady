# PacketReady — Code Conventions

> Rules the codebase already follows, written down so they survive the next contributor.

| | |
|---|---|
| **Status** | Active — v1, 2026-05-22 |
| **Owner** | Ben |
| **Data** | synthetic only · mocked PSV · no PHI |
| **Companion** | [design.md](./design.md), [style.md](./style.md) |
| **Scope** | Backend C# code under `apps/api/`. Frontend conventions deferred until a UI exists. |

---

## 0 · Premise

`style.md` codifies how the docs read. This file codifies how the code looks where the language doesn't decide for you. Anything the compiler or the formatter pins (naming case, brace placement, namespace layout) is out of scope.

The first rule here is **enum-to-column casing**, because the schema already mixes two styles and a cold reader will (correctly) read that as inconsistency unless the underlying rule is visible. The rule isn't arbitrary; it tracks a real distinction the spec already makes. This doc just names it.

---

## 1 · Enum storage — the rule

Two paths. The decision turns on one question: **does the on-disk string have an authority outside this codebase?**

▎ **Domain-state enums** — values that name a C# domain state with no external pin. Stored as `PascalCase` via EF's default `HasConversion<string>()`. The C# member name *is* the canonical string.

▎ **External-identifier enums** — values that match a spec, API identifier, or wire-format token defined outside this codebase. Stored as `lower_snake_case` via an explicit `ValueConverter<T, string>`. The on-disk string answers to its external source, not to C# naming.

The decision is binary. There is no third path.

| Question | Answer | Path |
|---|---|---|
| Is the string defined by an external spec, vendor API, or wire format? | Yes | **External-identifier** (`lower_snake_case`) |
| Does the string exist only because C# named it that way? | Yes | **Domain-state** (`PascalCase`) |

If both feel true: the external authority wins. If neither: it's domain-state by default — the C# name is the only authority.

---

## 2 · Wiring — domain-state enums

```csharp
// apps/api/Infrastructure/Persistence/Configurations/DocumentExtractionConfiguration.cs
b.Property(x => x.Status)
    .HasColumnName("status")
    .HasConversion<string>()      // EF writes ExtractionStatus.Succeeded → 'Succeeded'
    .HasMaxLength(16)
    .IsRequired();

t.HasCheckConstraint(
    "ck_document_extractions_status_values",
    "status IN ('Succeeded', 'Failed')");
```

Three requirements:

1. `HasConversion<string>()` — the parameterless form. EF maps each member to its `nameof` string verbatim. Do not override.
2. The CHECK constraint lists the exact `PascalCase` members. Adding a new enum value means adding it to the CHECK and shipping a migration.
3. `MaxLength` leaves headroom for at least one rename or addition. Postgres `varchar(N)` and `text` have identical storage cost; the cap exists to catch corrupt seed data, not to save bytes. Never pin to the exact length of the longest current value.

Current domain-state columns: [§6](#6--current-inventory).

---

## 3 · Wiring — external-identifier enums

```csharp
// apps/api/Infrastructure/Persistence/Configurations/DocumentExtractionConfiguration.cs
private static readonly ValueConverter<ExtractionSource, string> SourceConverter = new(
    v => ToColumn(v),
    s => FromColumn(s));

private static string ToColumn(ExtractionSource v) => v switch
{
    ExtractionSource.Llm => "llm",
    ExtractionSource.ProviderEdit => "provider_edit",
    ExtractionSource.AdminEdit => "admin_edit",
    _ => throw new InvalidOperationException($"Unmapped ExtractionSource value: {v}"),
};

private static ExtractionSource FromColumn(string s) => s switch
{
    "llm" => ExtractionSource.Llm,
    "provider_edit" => ExtractionSource.ProviderEdit,
    "admin_edit" => ExtractionSource.AdminEdit,
    _ => throw new InvalidOperationException($"Unmapped source value: '{s}'"),
};
```

Five requirements:

1. Static `ToColumn` / `FromColumn` helpers, called by the converter's `Expression<Func<>>` arguments. EF's expression trees do not permit `switch` or `throw` expressions inline.
2. Both helpers throw `InvalidOperationException` on the `_` arm. A fourth enum value added without updating the converter fails fast at the write boundary — never silently routes to the last branch of a ternary chain.
3. The CHECK constraint lists the exact `lower_snake_case` literals. Same migration rule as §2.
4. `MaxLength` leaves headroom for at least one rename or addition. `'provider_edit'` is 13 chars → 16 is enough; do not pin to the exact length.
5. The C# enum keeps `PascalCase` member names (`ExtractionSource.Llm`, not `ExtractionSource.llm`). The converter is the only place casing is translated. Domain code, tests, and JSON-wire serialization use the C# names.

Current external-identifier columns: [§6](#6--current-inventory).

---

## 4 · Cross-field invariants

Domain factories enforce cross-field invariants in C# (e.g., `Source = Llm` ⇔ `Model IS NOT NULL`). The DB CHECK constraint is the floor that catches raw SQL backfills and any future caller that bypasses the aggregate root.

▎ If a cross-field rule is load-bearing for a downstream reader, add the CHECK. If it's a defensive nicety, leave it in the domain layer.

Name format: `ck_<table>_<short-description>`. The description is verbs and nouns, not booleans — `status_error_pairing`, not `status_xor_error`. See `ck_document_extractions_status_error_pairing` and `ck_document_extractions_llm_provenance_pairing` for the pattern.

---

## 5 · Check constraint naming

```
ck_<table>_<column>_values          -- value-set CHECKs (one column, IN clause)
ck_<table>_<column>_range           -- numeric range CHECKs
ck_<table>_<short-description>      -- cross-field CHECKs (§4)
```

Three rules:

1. Always prefix `ck_`. EF auto-names start with `CK_` (PascalCase); ours start `ck_` (snake_case) so the convention is grep-visible.
2. Table name is singular if the entity is, plural if the table is (`ck_documents_…`, `ck_document_extractions_…`).
3. For multi-column constraints, name by intent, not by enumeration. `…_status_error_pairing` reads; `…_status_and_error` doesn't.

---

## 6 · Current inventory

Every enum-stored column in the schema, as of 2026-05-22:

| Table | Column | Path | On-disk values |
|---|---|---|---|
| `documents` | `doc_type` | Domain-state | `'License' \| 'Dea' \| 'BoardCert' \| 'Malpractice' \| 'Cv' \| 'Other'` |
| `documents` | `uploaded_by` | External-identifier | `'provider' \| 'admin'` |
| `document_extractions` | `status` | Domain-state | `'Succeeded' \| 'Failed'` |
| `document_extractions` | `source` | External-identifier | `'llm' \| 'provider_edit' \| 'admin_edit'` |
| `readiness_scores` | `tier` | Domain-state | `'Red' \| 'Yellow' \| 'Green'` |
| `primary_source_results` | `source` | External-identifier | `'nppes' \| 'oig' \| 'sam' \| 'state_board' \| 'caqh'` |
| `primary_source_results` | `status` | External-identifier | `'ok' \| 'not_found' \| 'error'` |

Why `primary_source_results.source` is external-identifier: `'nppes'`, `'oig'`, `'sam'` are the canonical identifiers for those primary-source APIs. PascalCasing to `'Nppes'` would be wrong in the same way uppercasing `application/pdf` to `Application/PDF` would be wrong.

Why `documents.doc_type` is domain-state: there is no external authority for "License" vs "Dea" — the classifier emits whatever the prompt asks for, and the prompt asks for the C# enum names.

`Issue.Severity` (`'Critical' \| 'Major' \| 'Minor'`) lives inside the `readiness_scores.issues` JSONB blob, not as its own column. Same rule applies: domain-state, serialized as the C# member name.

---

## 7 · JSON wire format

When a domain enum is serialized to JSON — API responses, agent tool inputs, eval fixtures — the casing on the wire matches the casing on disk. Same rule, same reason.

▎ Domain-state on-disk = `PascalCase` on wire. ▎ External-identifier on-disk = `lower_snake_case` on wire.

The eval-harness fixtures already follow this: see `"status": "Active"` under [phase-2-eval-harness.md §"Golden JSON shape"](./impl/phase-2-eval-harness.md#golden-json-shape) (license-status JSON, `LicenseStatus.Active` on the C# side, `PascalCase` on the wire).

Field *names* (the JSON keys) are always `camelCase`. That's a JSON convention, not an enum-storage rule, and it applies independent of the value casing.

---

## 8 · Rejected directions

✕ **Lowercase everything on-disk.** Forces every domain-state enum to ship a custom `ValueConverter` instead of the default, and pushes the casing-translation surface to ~7 column-touching sites instead of 2. No real-world benefit until the project has live operators writing ad-hoc SQL — and PacketReady has none.

✕ **PascalCase everything on-disk.** Stores `'Nppes'`, `'StateBoard'`, `'NotFound'` for what are visibly external API identifiers. Reads wrong, and breaks the agent's wire-format symmetry (the same string identifies the tool result, the cache key, and the Langfuse trace tag).

✕ **Per-column opt-in via attribute.** `[ColumnCase(Lower)]` or similar — looks tidy until you need cross-field CHECKs, at which point the SQL has to know the casing anyway and the attribute is just hiding the decision. Explicit configuration in the EF type config keeps the schema readable from one file.

✕ **A `EnumStorageBase` helper class with virtual methods.** Premature abstraction for two converter sites. Three would be the time to extract.

---

## 9 · How to use this

When adding a new enum-stored column:

1. Answer the §1 question. The §6 inventory is precedent, not a vote.
2. Mirror §2 or §3's template verbatim. Do not invent a third pattern. If the template doesn't fit, the column is doing something this doc doesn't cover — add a §10 before you ship the code, then use the new section.
3. Add the column to §6.
4. Add the CHECK constraint per §4 / §5.

→ The conventions live in code. This doc just makes them findable.
