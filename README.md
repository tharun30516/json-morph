# JsonMorph: JSON A → B migration microservice (+ MCP server)

JsonMorph turns JSON in shape **A** into shape **B** using a declarative mapping, then moves your data with batched migration jobs. It is also an **MCP server**, so Claude (or any MCP client) can infer, test, save and run migrations itself.

- **.NET 8 minimal API.** Zero NuGet dependencies.
- **Mapping engine:** JSONPath sources, nesting, templates, coalesce, defaults, required fields, conditions, nested arrays (`each`) and 30+ transforms.
- **Inference:** give it one A record and the B you want, and it writes the mapping and proves it reproduces B.
- **Migrations:** sources are inline, file (`.json` streamed / `.ndjson`) or HTTP GET. Sinks are memory, file or HTTP POST. You get batching, dry runs, a dead-letter queue, `maxErrors` abort, all-or-nothing file output, retry with backoff and cancellation.
- **MCP:** Streamable HTTP at `POST /mcp`, or stdio with `--stdio`.
- **Safety:** optional API key, file I/O jailed to the data folder, HTTP host allowlist, Origin check on `/mcp`.

## Run

```bash
cd json-morph
dotnet run --project src/JsonMorph.Api          # http://localhost:5080
dotnet run --project tests/JsonMorph.Tests      # 18 unit tests
scripts/smoke.sh                                # 27 end-to-end checks (REST + MCP http + stdio)
docker build -t json-morph . && docker run -p 8080:8080 -v $PWD/data:/data json-morph
```

## Use it from Claude (MCP)

**Claude Code, HTTP** (service running):
```bash
claude mcp add --transport http json-morph http://localhost:5080/mcp
```

**Claude Desktop / Claude Code, stdio:** add this to your MCP config:
```json
{
  "mcpServers": {
    "json-morph": {
      "command": "dotnet",
      "args": ["run", "--project", "/path/to/json-morph/src/JsonMorph.Api", "--", "--stdio",
               "--JsonMorph:DataRoot=/path/to/data"]
    }
  }
}
```

Then just ask: *"Here's a record from the old API and what the new API expects. Build the mapping, dry-run it on `exports/users.json`, then migrate it to `out/users.ndjson`."*

| Tool | What it does |
|---|---|
| `infer_mapping` | A + B example → draft mapping, `reproducesTarget`, unmatched fields |
| `transform_json` | Map one record or an array (inline or saved mapping) |
| `validate_mapping` / `save_mapping` / `get_mapping` / `list_mappings` | Versioned mapping management |
| `start_migration` | Run a migration job (`waitSeconds` to block until done) |
| `get_migration` / `list_migrations` / `cancel_migration` | Status, output sample, dead letters |

## Mapping format

```json
{
  "name": "users-v1-to-v2",
  "rules": [
    { "target": "id", "source": "$.user_id", "transform": ["toString"], "required": true },
    { "target": "profile.fullName", "template": "{$.first} {$.last}" },
    { "target": "profile.email", "sources": ["$.work_email", "$.email"], "transform": ["trim", "lower"] },
    { "target": "status", "source": "$.active", "transform": ["toBool", { "op": "map", "values": { "true": "ACTIVE", "false": "INACTIVE" } }], "default": "UNKNOWN" },
    { "target": "createdAt", "source": "$.created", "transform": [{ "op": "date", "from": "MM/dd/yyyy", "to": "yyyy-MM-dd" }] },
    { "target": "vipSince", "source": "$.vip_date", "when": "$.is_vip" },
    { "target": "orders", "source": "$.orders", "each": { "rules": [ { "target": "orderId", "source": "$.order_no" } ] } },
    { "target": "meta.migratedAt", "source": "$now" },
    { "target": "meta.source", "const": "legacy-crm" }
  ]
}
```

Each rule takes exactly one of `source`, `sources` (first non-null wins), `template` or `const`. Optional fields are `default`, `required`, `when` (`!` negates), `transform` and `each`.

**Paths:** `$.a.b`, `$['odd key']`, `$.list[0]`, `$.list[-1]`, `$.items[*].sku`, `$` (whole record), `$now`.

**Transforms:** `trim lower upper toString toNumber toInt toBool nullIfEmpty date map split join replace regex round multiply add prefix suffix hash mask json stringify first last count distinct compact`. Scalar transforms apply per element when the value is an array.

Full example: [`samples/users-v1-to-v2.mapping.json`](samples/users-v1-to-v2.mapping.json) on [`samples/legacy-users.json`](samples/legacy-users.json).

## REST API

| Method | Path | |
|---|---|---|
| POST | `/api/transform` | `{ input, mappingName? \| mapping? }` |
| POST | `/api/mappings/infer` | `{ source, target, name?, save? }` |
| POST/GET/DELETE | `/api/mappings[/{name}?version=]` | Save (new version), list, get, delete |
| POST | `/api/mappings/validate` | Check a mapping |
| POST | `/api/migrations` | Start a job → `202` + status |
| GET | `/api/migrations[/{id}]` | Status and counters |
| GET | `/api/migrations/{id}/output`, `/dead-letters` | `?skip=&take=` |
| POST | `/api/migrations/{id}/cancel` | |

Migration request:
```json
{
  "mappingName": "users-v1-to-v2",
  "source": { "type": "file", "path": "exports/users.json", "recordsPath": "$.data" },
  "sink":   { "type": "file", "path": "out/users.ndjson", "format": "ndjson" },
  "batchSize": 500, "dryRun": false, "maxErrors": 100,
  "deadLetterPath": "out/failed.ndjson"
}
```
More examples are in [`JsonMorph.http`](JsonMorph.http).

## Config (`appsettings.json` → `JsonMorph`, or env `JsonMorph__ApiKey=...`)

| Key | Default | |
|---|---|---|
| `DataRoot` | `data` | Mappings and all file sources/sinks live here |
| `ApiKey` | none | Required as `X-Api-Key` or `Authorization: Bearer` (except `/health`) |
| `AllowedHttpHosts` | `localhost, 127.0.0.1` | Hosts for HTTP sources/sinks; `*` = any |
| `MaxConcurrentJobs` / `MaxBatchSize` / `MaxMemoryRecords` | 2 / 5000 / 10000 | |

## Notes

- Jobs live in memory, so job history resets on restart. Mappings are persisted to `DataRoot/mappings`.
- Inference is a draft generator. Always check `reproducesTarget` and `unmatched` before running a real migration.
