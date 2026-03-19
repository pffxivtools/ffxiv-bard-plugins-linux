#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PROFILE="${1:-default}"
ARTIFACT_DIR="$ROOT_DIR/docker/artifacts"

set_runtime_env_from_snapshot() {
  local env_name="$1"
  local path_file="$2"
  if [[ -z "${!env_name:-}" && -f "$path_file" ]]; then
    export "$env_name=$(<"$path_file")"
  fi
}

run_profile() {
  local profile="$1"
  local service
  service="$(
    case "$profile" in
      unix) echo test-unix ;;
      unix-benchmark) echo test-unix-benchmark ;;
      wine) echo test-wine ;;
      wine-benchmark) echo test-wine-benchmark ;;
      proton) echo test-proton ;;
      proton-benchmark) echo test-proton-benchmark ;;
      windows-vm) echo windows-vm ;;
    esac
  )"
  case "$profile" in
    unix-benchmark) rm -f "$ARTIFACT_DIR/linux-benchmarks.txt" ;;
    wine-benchmark) rm -f "$ARTIFACT_DIR/wine-benchmarks.txt" ;;
    proton-benchmark) rm -f "$ARTIFACT_DIR/proton-benchmarks.txt" ;;
  esac
  docker compose -f "$ROOT_DIR/compose.yml" rm -sf "$service" >/dev/null 2>&1 || true
  docker compose -f "$ROOT_DIR/compose.yml" --profile "$profile" up --build --force-recreate --abort-on-container-exit --exit-code-from "$service"
  docker compose -f "$ROOT_DIR/compose.yml" rm -sf "$service" >/dev/null 2>&1 || true
}

append_summary_rows() {
  local runtime="$1"
  local source_file="$2"

  [[ -f "$source_file" ]] || return 0

  awk -v runtime="$runtime" '
    {
      method=$2
      count=min=mean=p50=p95=p99=max=""
      for (i = 3; i <= NF; i++) {
        split($i, kv, "=")
        key=kv[1]
        value=kv[2]
        if (key == "count") count=value
        else if (key == "min_us") min=value
        else if (key == "mean_us") mean=value
        else if (key == "p50_us") p50=value
        else if (key == "p95_us") p95=value
        else if (key == "p99_us") p99=value
        else if (key == "max_us") max=value
      }
      printf "%-8s %-18s %8s %8s %8s %8s %8s %8s %8s\n", runtime, method, count, min, mean, p50, p95, p99, max
    }
  ' "$source_file" >> "$SUMMARY_FILE"
}

print_benchmark_summary() {
  SUMMARY_FILE="$ARTIFACT_DIR/benchmarks-summary.txt"
  mkdir -p "$ARTIFACT_DIR"
  {
    printf "%-8s %-18s %8s %8s %8s %8s %8s %8s %8s\n" "runtime" "method" "count" "min_us" "mean_us" "p50_us" "p95_us" "p99_us" "max_us"
    append_summary_rows "linux" "$ARTIFACT_DIR/linux-benchmarks.txt"
    append_summary_rows "wine" "$ARTIFACT_DIR/wine-benchmarks.txt"
    append_summary_rows "proton" "$ARTIFACT_DIR/proton-benchmarks.txt"
  } > "$SUMMARY_FILE"

  if [[ -s "$SUMMARY_FILE" ]]; then
    cat "$SUMMARY_FILE"
  fi
}

run_default_sequence() {
  mkdir -p "$ARTIFACT_DIR"
  rm -f "$ARTIFACT_DIR"/linux-benchmarks.txt "$ARTIFACT_DIR"/wine-benchmarks.txt "$ARTIFACT_DIR"/proton-benchmarks.txt "$ARTIFACT_DIR"/benchmarks-summary.txt
  set_runtime_env_from_snapshot TINYIPC_HOST_WINE_PATH "$ROOT_DIR/docker/.xlcore-wine-snapshot/wine-runtime-path.txt"

  run_profile unix
  run_profile unix-benchmark
  run_profile wine
  run_profile wine-benchmark
  print_benchmark_summary
}

run_all_sequence() {
  set_runtime_env_from_snapshot TINYIPC_HOST_PROTON_PATH "$ROOT_DIR/docker/.xlcore-proton-snapshot/proton-runtime-path.txt"
  run_default_sequence
  run_profile proton
  run_profile proton-benchmark
  print_benchmark_summary
}

case "$PROFILE" in
  default)
    run_default_sequence
    exit 0
    ;;
  all)
    run_all_sequence
    exit 0
    ;;
  unix|unix-benchmark)
    ;;
  wine|wine-benchmark|proton|proton-benchmark)
    set_runtime_env_from_snapshot TINYIPC_HOST_WINE_PATH "$ROOT_DIR/docker/.xlcore-wine-snapshot/wine-runtime-path.txt"
    set_runtime_env_from_snapshot TINYIPC_HOST_PROTON_PATH "$ROOT_DIR/docker/.xlcore-proton-snapshot/proton-runtime-path.txt"
    ;;
  windows-vm)
    ;;
  *)
    echo "Unknown compose profile: $PROFILE" >&2
    exit 1
    ;;
esac

run_profile "$PROFILE"
case "$PROFILE" in
  unix-benchmark|wine-benchmark|proton-benchmark)
    print_benchmark_summary
    ;;
esac
