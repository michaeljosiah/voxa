#!/usr/bin/env bash
# Run each given test project and gate on its TRX result rather than the test-host exit code.
#
# Why: Voxa.Studio.Tests passes every test, but its Avalonia.Headless.XUnit session thread does not
# reliably terminate, so the test host can hang AFTER the run completes. Confirmed from a hang dump —
# the xUnit runner parks in RunTestsInAssembly -> WaitHandle.WaitOne() with NO Voxa code on any stack:
# a framework session-shutdown issue, not a failing test. `--blame-hang-timeout` aborts that post-run
# hang and the TRX still records the (passing) results, so we read pass/fail from the TRX <Counters>
# instead of trusting a process exit code that a clean run can leave non-zero.
#
# Usage: run-tests-gated.sh <proj.csproj>...   (env TEST_FILTER overrides the default category filter)
set -uo pipefail

filter="${TEST_FILTER:-Category!=LocalModels}"

# Bound each run so a hang can't burn the job; prefer coreutils `timeout` when present (it is on the
# ubuntu and git-bash-on-windows runners), else lean on --blame-hang-timeout + the job's timeout-minutes.
run() {
  if command -v timeout >/dev/null 2>&1; then timeout -k 30 300 "$@"; else "$@"; fi
}

failed=""
for proj in "$@"; do
  name="$(basename "$proj" .csproj)"
  echo "::group::test ${name}"
  rc=0
  run dotnet test "$proj" --configuration Release --no-build --verbosity normal \
    --filter "${filter}" --blame-hang-timeout 120s --logger "trx;LogFileName=${name}.trx" || rc=$?
  echo "::endgroup::"

  trx="$(find . -name "${name}.trx" -print -quit 2>/dev/null || true)"
  if [ -z "${trx}" ]; then
    echo "::error::${name}: no TRX produced (rc=${rc})"; failed="${failed} ${name}"; continue
  fi

  counters="$(grep -om1 '<Counters[^>]*/>' "${trx}" || true)"
  fcount="$(printf '%s' "${counters}" | grep -o 'failed="[0-9]*"' | grep -o '[0-9]*' || true)"
  pcount="$(printf '%s' "${counters}" | grep -o 'passed="[0-9]*"' | grep -o '[0-9]*' || true)"

  if [ "${fcount:-1}" != "0" ] || [ "${pcount:-0}" = "0" ]; then
    echo "::error::${name} failed (rc=${rc}; ${counters:-no <Counters> in TRX})"; failed="${failed} ${name}"
  elif [ "${rc}" -ne 0 ]; then
    echo "::warning::${name}: ${pcount} passed, 0 failed, but the host exited non-zero (rc=${rc}) — known Avalonia.Headless session-shutdown hang; gated on the TRX result."
  else
    echo "${name}: ${pcount} passed"
  fi
done

[ -z "${failed}" ] || { echo "::error::Tests failed:${failed}"; exit 1; }
