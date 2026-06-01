#!/bin/bash
# Polyglot SDK Validation - TypeScript parameter handle round-trip
# Creates a focused TypeScript AppHost that passes a ParameterResource returned
# from the server back into server-side capabilities, then verifies a running
# resource received the resolved parameter value.
set -euo pipefail

echo "=== TypeScript Parameter Handle Round-Trip Validation ==="

if ! command -v aspire &> /dev/null; then
    echo "❌ Aspire CLI not found in PATH"
    exit 1
fi

if ! command -v jq &> /dev/null; then
    echo "❌ jq not found in PATH"
    exit 1
fi

echo "Aspire CLI version:"
aspire --version

WORK_DIR="$(mktemp -d)"
ASPIRE_PID=""

cleanup() {
    if [ -n "${ASPIRE_PID:-}" ]; then
        kill "$ASPIRE_PID" 2>/dev/null || true
        wait "$ASPIRE_PID" 2>/dev/null || true
    fi
    rm -rf "$WORK_DIR"
}

trap cleanup EXIT

echo "Working directory: $WORK_DIR"
cd "$WORK_DIR"

echo "Creating TypeScript apphost project..."
aspire init --language typescript --non-interactive -d

APPHOST_FILE="$(jq -r '.appHost.path // empty' aspire.config.json)"
if [ -z "$APPHOST_FILE" ] || [ ! -f "$APPHOST_FILE" ]; then
    if [ -f apphost.mts ]; then
        APPHOST_FILE="apphost.mts"
    elif [ -f apphost.ts ]; then
        APPHOST_FILE="apphost.ts"
    else
        echo "❌ Could not find generated TypeScript AppHost file"
        find . -maxdepth 2 -type f | sort
        exit 1
    fi
fi

CREATE_BUILDER_IMPORT="$(grep -m1 '^import { createBuilder }' "$APPHOST_FILE")"
if [ -z "$CREATE_BUILDER_IMPORT" ]; then
    echo "❌ Could not find createBuilder import in $APPHOST_FILE"
    cat "$APPHOST_FILE"
    exit 1
fi

echo "Writing parameter round-trip AppHost to $APPHOST_FILE..."
cat > "$APPHOST_FILE" <<EOF
$CREATE_BUILDER_IMPORT

const builder = await createBuilder();

const endpoint = await builder.addParameter("registry-endpoint", { value: "localhost:5001" });
const probe = await builder.addExecutable("probe", "node", ".", ["-e", "setInterval(() => {}, 1000)"]);
await probe.withEnvironment("REGISTRY_ENDPOINT", endpoint);

await builder.build().run();
EOF

echo "=== $APPHOST_FILE ==="
cat "$APPHOST_FILE"

echo "Restoring generated TypeScript SDK..."
aspire restore --non-interactive --apphost "$APPHOST_FILE"

echo "Starting apphost in background..."
aspire run -d --non-interactive --apphost "$APPHOST_FILE" > aspire.log 2>&1 &
ASPIRE_PID=$!
echo "Aspire PID: $ASPIRE_PID"

RESULT=1
for i in {1..12}; do
    echo "Attempt $i/12: Checking for probe resource..."

    if ! kill -0 "$ASPIRE_PID" 2>/dev/null; then
        echo "❌ FAILURE: aspire run exited before probe resource was available"
        break
    fi

    if aspire resources --include-hidden --format json --apphost "$APPHOST_FILE" > resources.json 2> resources.err; then
        if jq -e '.resources[] | select(.displayName == "probe" and .environment.REGISTRY_ENDPOINT == "localhost:5001")' resources.json > /dev/null; then
            echo "✅ SUCCESS: probe resource received REGISTRY_ENDPOINT from the parameter handle"
            jq '.resources[] | select(.displayName == "probe")' resources.json
            RESULT=0
            break
        fi
    else
        cat resources.err || true
    fi

    echo "Probe resource not ready yet, waiting 10 seconds..."
    sleep 10
done

if [ "$RESULT" -ne 0 ]; then
    echo "❌ FAILURE: probe resource with parameter-backed environment variable not found after 2 minutes"
    echo "=== Resources output ==="
    cat resources.json 2>/dev/null || true
    echo "=== Resources error ==="
    cat resources.err 2>/dev/null || true
    echo "=== Aspire log ==="
    cat aspire.log || true
fi

exit "$RESULT"
