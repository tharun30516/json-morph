#!/usr/bin/env bash
# End-to-end smoke test: starts the service, drives REST + MCP (HTTP and stdio), checks results.
# Needs: dotnet 8, curl, python3.   Usage: scripts/smoke.sh
set -euo pipefail
cd "$(dirname "$0")/.."

PORT=${PORT:-5099}
BASE="http://127.0.0.1:$PORT"
DATA=$(mktemp -d)
SINK_PORT=$((PORT + 1))
pass=0
cleanup() { kill "${API_PID:-}" "${SINK_PID:-}" 2>/dev/null || true; rm -rf "$DATA"; }
trap cleanup EXIT

ok()   { pass=$((pass + 1)); echo "  PASS  $1"; }
fail() { echo "  FAIL  $1"; echo "        $2"; exit 1; }
# check <name> <json> <python-expression over j>
check() {
  if python3 -c "import json,sys; j=json.loads(sys.argv[1]); sys.exit(0 if ($3) else 1)" "$2"; then ok "$1"
  else fail "$1" "$(echo "$2" | head -c 600)"; fi
}
post() { curl -sS -X POST "$BASE$1" -H 'Content-Type: application/json' -d "$2"; }
mcp()  { post /mcp "$1"; }

cp samples/legacy-users.json "$DATA/legacy-users.json"

# Tiny HTTP sink that stores whatever is POSTed to it.
python3 - "$SINK_PORT" "$DATA/http-sink.json" <<'PY' &
import http.server, json, sys
port, out = int(sys.argv[1]), sys.argv[2]
got = []
class H(http.server.BaseHTTPRequestHandler):
    def do_POST(self):
        got.extend(json.loads(self.rfile.read(int(self.headers['Content-Length']))))
        open(out, 'w').write(json.dumps(got))
        self.send_response(200); self.end_headers()
    def log_message(self, *a): pass
http.server.HTTPServer(('127.0.0.1', port), H).serve_forever()
PY
SINK_PID=$!

dotnet build -nologo -v q src/JsonMorph.Api >/dev/null
dotnet run --no-build --no-launch-profile --project src/JsonMorph.Api -- --urls "$BASE" \
  --JsonMorph:DataRoot="$DATA" --JsonMorph:AllowedHttpHosts:0=127.0.0.1 >"$DATA/api.log" 2>&1 &
API_PID=$!
for _ in $(seq 1 60); do curl -sf "$BASE/health" >/dev/null 2>&1 && break; sleep 0.5; done
curl -sf "$BASE/health" >/dev/null || { cat "$DATA/api.log"; fail "service starts" "no /health"; }
ok "service starts"

MAPPING=$(cat samples/users-v1-to-v2.mapping.json)

echo "REST"
r=$(post /api/mappings "$MAPPING");                           check "save mapping -> v1" "$r" "j['version']==1"
r=$(post /api/mappings "$MAPPING");                           check "save again -> v2"   "$r" "j['version']==2"
r=$(curl -sS "$BASE/api/mappings");                           check "list mappings"     "$r" "j[0]['latestVersion']==2"
r=$(post /api/transform "{\"mappingName\":\"users-v1-to-v2\",\"input\":$(python3 -c 'import json;print(json.dumps(json.load(open("samples/legacy-users.json"))[0]))')}")
check "transform one record" "$r" "j['success'] and j['output']['profile']['email']=='jane.doe@example.com'"
r=$(post /api/transform "{\"mappingName\":\"users-v1-to-v2\",\"input\":$(cat samples/legacy-users.json)}")
check "transform batch (1 bad record)" "$r" "j['total']==3 and j['failed']==1"
r=$(post /api/mappings/infer '{"source":{"fn":"A","n":"5"},"target":{"name":{"first":"A"},"count":5}}')
check "infer mapping reproduces target" "$r" "j['reproducesTarget'] is True"
r=$(post /api/mappings '{"name":"../x","rules":[]}')
check "invalid mapping rejected" "$r" "len(j['errors'])==2"

r=$(post /api/migrations '{"mappingName":"users-v1-to-v2","source":{"type":"file","path":"legacy-users.json"},"sink":{"type":"file","path":"out/users-v2.ndjson"},"deadLetterPath":"out/failed.ndjson","batchSize":2}')
ID=$(python3 -c "import json,sys;print(json.loads(sys.argv[1])['id'])" "$r")
for _ in $(seq 1 50); do
  r=$(curl -sS "$BASE/api/migrations/$ID")
  python3 -c "import json,sys;sys.exit(0 if json.loads(sys.argv[1])['status'] not in ('queued','running') else 1)" "$r" && break
  sleep 0.2
done
check "file migration finishes with errors" "$r" "j['status']=='completedWithErrors' and j['written']==2 and j['failed']==1 and j['batches']==1"
[ "$(wc -l <"$DATA/out/users-v2.ndjson")" -eq 2 ] && ok "output file has 2 records" || fail "output file" "$(cat "$DATA/out/users-v2.ndjson")"
[ "$(wc -l <"$DATA/out/failed.ndjson")" -eq 1 ] && ok "dead-letter file has 1 record" || fail "dead-letter file" "missing"
r=$(curl -sS "$BASE/api/migrations/$ID/dead-letters")
check "dead letters via API" "$r" "j[0]['record']==2 and len(j[0]['errors'])==5"

r=$(post /api/migrations '{"mappingName":"users-v1-to-v2","source":{"type":"file","path":"legacy-users.json"},"sink":{"type":"file","path":"out/all-or-nothing.json","format":"json"},"maxErrors":0}')
sleep 1.5
r=$(curl -sS "$BASE/api/migrations/$(python3 -c "import json,sys;print(json.loads(sys.argv[1])['id'])" "$r")")
check "maxErrors aborts" "$r" "j['status']=='failed' and 'maxErrors' in j['failureReason']"
[ ! -e "$DATA/out/all-or-nothing.json" ] && ok "aborted run left no partial file" || fail "atomic output" "file exists"

r=$(post /api/migrations '{"mappingName":"users-v1-to-v2","source":{"type":"file","path":"../../etc/passwd"}}')
check "path traversal blocked" "$r" "'inside the data folder' in j['errors'][0]"
r=$(post /api/migrations '{"mappingName":"users-v1-to-v2","source":{"type":"http","url":"http://example.com/x"}}')
check "non-allowlisted host blocked" "$r" "'AllowedHttpHosts' in j['errors'][0]"

echo "MCP over HTTP"
r=$(mcp '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"smoke","version":"1"}}}')
check "initialize" "$r" "j['result']['serverInfo']['name']=='json-morph' and j['result']['protocolVersion']=='2025-06-18'"
code=$(curl -s -o /dev/null -w '%{http_code}' -X POST "$BASE/mcp" -H 'Content-Type: application/json' -d '{"jsonrpc":"2.0","method":"notifications/initialized"}')
[ "$code" = 202 ] && ok "notification -> 202" || fail "notification" "$code"
r=$(mcp '{"jsonrpc":"2.0","id":2,"method":"tools/list"}')
check "tools/list has 10 tools" "$r" "len(j['result']['tools'])==10 and 'start_migration' in [t['name'] for t in j['result']['tools']]"
r=$(mcp '{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"infer_mapping","arguments":{"source":{"user":{"mail":" A@B.COM "},"tags":"x,y"},"target":{"email":"a@b.com","labels":["x","y"]},"name":"mcp-inferred","save":true}}}')
check "tool infer_mapping (+save)" "$r" "j['result']['structuredContent']['reproducesTarget'] and j['result']['structuredContent']['mapping']['version']==1"
r=$(mcp '{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"start_migration","arguments":{"mappingName":"mcp-inferred","source":{"type":"inline","records":[{"user":{"mail":"X@Y.Z"},"tags":"q"},{"user":{"mail":"P@Q.R"},"tags":"r,s"}]},"sink":{"type":"http","url":"http://127.0.0.1:'"$SINK_PORT"'/ingest"},"waitSeconds":10}}}')
check "tool start_migration -> http sink" "$r" "j['result']['structuredContent']['status']=='completed' and j['result']['structuredContent']['written']==2"
check "http sink received mapped records" "$(cat "$DATA/http-sink.json")" "j[1]=={'email':'p@q.r','labels':['r','s']}"
r=$(mcp '{"jsonrpc":"2.0","id":5,"method":"tools/call","params":{"name":"start_migration","arguments":{"mappingName":"users-v1-to-v2","source":{"type":"file","path":"legacy-users.json"},"dryRun":true,"waitSeconds":10}}}')
MID=$(python3 -c "import json,sys;print(json.loads(sys.argv[1])['result']['structuredContent']['id'])" "$r")
r=$(mcp '{"jsonrpc":"2.0","id":6,"method":"tools/call","params":{"name":"get_migration","arguments":{"id":"'"$MID"'","outputSample":1,"deadLetterSample":1}}}')
check "tool get_migration with samples" "$r" "j['result']['structuredContent']['dryRun'] and len(j['result']['structuredContent']['outputSample'])==1 and j['result']['structuredContent']['deadLetterSample'][0]['record']==2"
r=$(mcp '{"jsonrpc":"2.0","id":7,"method":"tools/call","params":{"name":"get_mapping","arguments":{"name":"nope"}}}')
check "tool error is isError, not crash" "$r" "j['result']['isError'] is True"
r=$(mcp '{"jsonrpc":"2.0","id":8,"method":"bogus"}')
check "unknown method -> -32601" "$r" "j['error']['code']==-32601"

echo "MCP over stdio"
r=$(printf '%s\n' \
  '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"smoke","version":"1"}}}' \
  '{"jsonrpc":"2.0","method":"notifications/initialized"}' \
  '{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"transform_json","arguments":{"input":{"a":" Hi "},"mapping":{"rules":[{"target":"b","source":"$.a","transform":["trim","upper"]}]}}}}' \
  | dotnet run --no-build --no-launch-profile --project src/JsonMorph.Api -- --stdio --JsonMorph:DataRoot="$DATA" 2>/dev/null)
lines=$(echo "$r" | wc -l)
[ "$lines" -eq 2 ] && ok "stdout carries only 2 JSON-RPC responses" || fail "stdio output" "$r"
check "stdio transform_json" "$(echo "$r" | tail -1)" "j['result']['structuredContent']['output']=={'b':'HI'}"

echo
echo "$pass checks passed"
