#!/usr/bin/env bash
# Checks that the app answers /health/ready (so it reaches Postgres). The ingress admits only the
# owner's IP, so this lets the runner in for the check and always takes the rule out again; if the
# removal ever fails, the next deploy resets the list from Bicep.
# Used by deploy.yml and start-postgres.yml. Needs RG and an Azure sign-in.
set -euo pipefail

fqdn=$(az containerapp show -n shortener -g "$RG" --query properties.configuration.ingress.fqdn -o tsv)
echo "::add-mask::$fqdn"

trap 'az containerapp ingress access-restriction remove -n shortener -g "$RG" --rule-name ci-smoke -o none || true' EXIT

runner_ip=$(curl -s https://api.ipify.org)
az containerapp ingress access-restriction set -n shortener -g "$RG" \
  --rule-name ci-smoke --ip-address "$runner_ip/32" --action Allow -o none
# retries cover the cold start from zero replicas and the rule taking effect
curl --fail --silent --show-error --retry 10 --retry-all-errors --retry-delay 6 \
  -o /dev/null -w "ready: %{http_code}\n" "https://$fqdn/health/ready"
