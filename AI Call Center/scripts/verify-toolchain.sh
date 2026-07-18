#!/usr/bin/env bash

set -euo pipefail

required_dotnet="10.0.302"
required_node_major="24"

require_command() {
  local command_name="$1"

  if ! command -v "$command_name" >/dev/null 2>&1; then
    echo "Missing required command: $command_name" >&2
    exit 1
  fi
}

require_command dotnet
require_command node
require_command npm
require_command docker

actual_dotnet="$(dotnet --version)"
actual_node="$(node --version)"
actual_node_major="${actual_node#v}"
actual_node_major="${actual_node_major%%.*}"

if [[ "$actual_dotnet" != "$required_dotnet" ]]; then
  echo "Expected .NET SDK $required_dotnet, found $actual_dotnet." >&2
  exit 1
fi

if [[ "$actual_node_major" != "$required_node_major" ]]; then
  echo "Expected Node.js major $required_node_major, found $actual_node." >&2
  exit 1
fi

docker compose version >/dev/null

echo "Toolchain verified: .NET $actual_dotnet, Node.js $actual_node, npm $(npm --version), $(docker compose version --short)."
