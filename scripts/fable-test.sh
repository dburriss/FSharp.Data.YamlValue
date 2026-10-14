#!/usr/bin/env bash
# Runs the shared test suite under Fable (JS and/or Python).
#
#   scripts/fable-test.sh [js|python|all]      (default: all)
#
# Prerequisites (see docs/fable.md): .NET SDK 8+, `bun` or `node` for the JS run (any JS runtime
# that runs ES modules), Python 3.12+ for the Python run (3.13 tested). The Fable tool itself is
# pinned in .config/dotnet-tools.json and restored here; the Python run creates
# tests/FSharp.Data.YamlValue.Fable/.venv and installs `fable-library` at the same version.
# Exit code is non-zero if any test fails.
set -euo pipefail

target="${1:-all}"
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
proj="$root/tests/FSharp.Data.YamlValue.Fable"
fable_version="$(grep -A1 '"fable"' "$root/.config/dotnet-tools.json" | grep -o '[0-9][0-9.]*' | head -1)"

cd "$root"
dotnet tool restore >/dev/null
dotnet fsi scripts/fable-gen-runner.fsx

status=0

check_output() { # fails unless the summary line reports 0 failed (runtimes differ on exit codes)
  if ! grep -q "Fable run: .* 0 failed" "$1"; then status=1; fi
}

run_js() {
  local out="$proj/out-js"
  rm -rf "$out"
  dotnet fable "$proj" --lang js -o "$out" --noCache
  local runtime
  runtime="$(command -v bun || command -v node || true)"
  if [ -z "$runtime" ]; then echo "need bun or node for the JS run" >&2; status=1; return; fi
  "$runtime" "$out/Program.js" | tee "$out/result.txt" || true
  check_output "$out/result.txt"
}

run_python() {
  local out="$proj/out-python"
  rm -rf "$out"
  dotnet fable "$proj" --lang python -o "$out" --noCache
  if [ ! -x "$proj/.venv/bin/python" ]; then
    local py
    py="$(command -v python3.13 || command -v python3.12 || command -v python3)"
    "$py" -m venv "$proj/.venv"
  fi
  "$proj/.venv/bin/pip" install --quiet "fable-library==$fable_version"
  (cd "$out" && "$proj/.venv/bin/python" program.py) | tee "$out/result.txt" || true
  check_output "$out/result.txt"
}

case "$target" in
  js) run_js ;;
  python) run_python ;;
  all) run_js; run_python ;;
  *) echo "usage: $0 [js|python|all]" >&2; exit 2 ;;
esac
exit $status
