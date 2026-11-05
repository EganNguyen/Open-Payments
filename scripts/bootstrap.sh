#!/usr/bin/env bash
# Reports which tools this repo needs and how to install the missing ones.
# It only prints. It never installs anything.
set -u

missing=0

report() {
  local name=$1 found=$2 install=$3
  if [ -n "$found" ]; then
    printf 'ok       %-12s %s\n' "$name" "$found"
  else
    printf 'MISSING  %-12s install with: %s\n' "$name" "$install"
    missing=1
  fi
}

docker_state=""
if docker info --format '{{.ServerVersion}}' >/dev/null 2>&1; then
  docker_state="server $(docker info --format '{{.ServerVersion}}')"
fi
report "docker" "$docker_state" "install Docker Desktop, then start it"

psql_version=""
command -v psql >/dev/null 2>&1 && psql_version=$(psql --version)
report "psql" "$psql_version" "brew install libpq"

pgbench_version=""
command -v pgbench >/dev/null 2>&1 && pgbench_version=$(pgbench --version)
report "pgbench" "$pgbench_version" "brew install libpq"

dotnet_version=""
if command -v dotnet >/dev/null 2>&1 && dotnet --list-sdks 2>/dev/null | grep -q '^10\.'; then
  dotnet_version=$(dotnet --list-sdks | grep '^10\.' | tail -1)
fi
report "dotnet 10" "$dotnet_version" "curl -sSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 10.0"

k6_version=""
command -v k6 >/dev/null 2>&1 && k6_version=$(k6 version | head -1)
report "k6" "$k6_version" "brew install k6"

pytest_version=""
python3 -m pytest --version >/dev/null 2>&1 && pytest_version=$(python3 -m pytest --version)
report "pytest" "$pytest_version" "python3 -m venv .venv && .venv/bin/pip install pytest"

exit "$missing"
