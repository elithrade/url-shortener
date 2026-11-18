#!/usr/bin/env bash
# Runs the migrate job with the image it already has (deploy.yml sets it first) and waits for it.
# Used by deploy.yml and start-postgres.yml. Needs RG and an Azure sign-in.
set -euo pipefail

execution=$(az containerapp job start -n shortener-migrate -g "$RG" --query name -o tsv)
for _ in $(seq 60); do
  status=$(az containerapp job execution show -n shortener-migrate -g "$RG" \
    --job-execution-name "$execution" --query properties.status -o tsv)
  echo "migrate: $status"
  case "$status" in
    Succeeded) exit 0 ;;
    Failed | Stopped | Degraded) exit 1 ;;
  esac
  sleep 10
done
echo "migrate: no result after 10 minutes"
exit 1
