# Deploying to Azure (stage 1)

How to deploy the app to Azure Container Apps by hand, with the Bicep in this folder. Stage 1 of the Azure plan in `PLAN.md`. Later, GitHub Actions runs the same steps.

What `main.bicep` creates in one resource group:

```
Log Analytics workspace (daily cap)        container logs
Container Apps environment                 shared host for the app and the job
├─ Container App "shortener"               public https ingress, only one allowed client IP, scale 0..1
└─ Container Apps Job "shortener-migrate"  the same image run with `migrate`, started by hand
Postgres Flexible Server (B1ms)            database "shortener", firewall open to Azure services
```

No secrets or personal values live in the repo. `main.bicepparam` reads three environment variables at deploy time:

| Variable | Value |
|---|---|
| `SHORTENER_IMAGE` | `ghcr.io/<owner>/url-shortener:<git short sha>` |
| `SHORTENER_ALLOWED_IP` | the public IP allowed through the ingress (until stage 3 puts APIM in front) |
| `POSTGRES_ADMIN_PASSWORD` | the Postgres admin password. **It must be the same on every deploy**: a different one resets the server's password. |

Keep the password somewhere outside the repo, for example a file with mode 600 that contains `POSTGRES_ADMIN_PASSWORD=...`, or a password manager.

## One-time setup

```bash
az login
az group create -n rg-shortener -l australiaeast

# resource providers are registered per subscription; needs subscription-level rights,
# which the CI identity won't have
az provider register -n Microsoft.App --wait
az provider register -n Microsoft.DBforPostgreSQL --wait
az provider register -n Microsoft.OperationalInsights --wait
```

A budget with email alerts (it only alerts, it never stops spending), easiest in the portal: Cost Management → Budgets → Add, scoped to the subscription. Cost data lags by up to a day. To check whether the account still has the 12-month free services (B1ms Postgres is one of them): portal → Free services, or after a day, Cost analysis shows whether Postgres compute is charged.

Check that the region offers what the template uses (B1ms and Postgres 17):

```bash
az postgres flexible-server list-skus -l australiaeast -o table
```

## 1. Build and push the image

The image goes to GitHub Container Registry (GHCR), tagged with the commit, so every deploy points at exact code.

```bash
gh auth refresh -h github.com -s write:packages            # once: lets the gh token push packages
gh auth token | docker login ghcr.io -u <owner> --password-stdin

IMAGE=ghcr.io/<owner>/url-shortener:$(git rev-parse --short HEAD)
docker build -t $IMAGE .
docker push $IMAGE
docker logout ghcr.io
```

The first push creates a **private** package. Make it public once (profile → Packages → url-shortener → Package settings → Change visibility), so Container Apps can pull it without registry credentials. To check it without being signed in: `docker manifest inspect $IMAGE`.

## 2. Preview and deploy

```bash
set -a; . <path to the password file>; set +a        # exports POSTGRES_ADMIN_PASSWORD
export SHORTENER_IMAGE=$IMAGE
export SHORTENER_ALLOWED_IP=$(curl -s https://api.ipify.org)

az deployment group what-if -g rg-shortener -f infra/main.bicep -p infra/main.bicepparam
az deployment group create  -g rg-shortener -n deploy-$(git rev-parse --short HEAD) \
  -f infra/main.bicep -p infra/main.bicepparam
```

- `what-if` lists what would change (`+` create, `~` modify, `-` delete) and changes nothing.
- `create` is incremental: resources the template doesn't mention are left alone, and re-running it with no changes does nothing. The first run takes about 5 to 10 minutes, mostly for Postgres.
- Every deployment is recorded under the given `-n` name (portal: resource group → Deployments).

The template's outputs give the app's address:

```bash
az deployment group show -g rg-shortener -n deploy-<sha> --query properties.outputs
```

## 3. Apply the schema

```bash
az containerapp job start -n shortener-migrate -g rg-shortener
az containerapp job execution list -n shortener-migrate -g rg-shortener -o table   # wait for Succeeded
```

On the first deploy, the app starts before the schema exists, so requests fail until the job has run. For later releases, run the job **before** the app moves to the new image.

## 4. Smoke test

```bash
BASE=<appUrl from the outputs>
curl -i $BASE/health/live                  # 200
curl -i $BASE/health/ready                 # 200 when Postgres is reachable
curl -i -X POST $BASE/shorten -H 'Content-Type: application/json' -d '{"url":"https://example.com"}'
curl -i $BASE/<code from the response>     # 302 to example.com
```

From any other IP, the ingress answers `403`.

## Continuous deployment

`.github/workflows/deploy.yml` releases every push to `main` once CI (`ci.yml`) has passed on it: build and push the image with the run's `GITHUB_TOKEN`, sign in to Azure with OIDC, run the migrate job on the new image (a failure stops here, and the current revision keeps serving), deploy `main.bicep`, then check `/health/ready` through a temporary ingress rule for the runner's IP. Docs-only pushes run neither.

One-time setup (as the owner):

```bash
az identity create -g rg-shortener -n id-shortener-deploy
PRINCIPAL=$(az identity show -g rg-shortener -n id-shortener-deploy --query principalId -o tsv)
az role assignment create --assignee-object-id $PRINCIPAL --assignee-principal-type ServicePrincipal \
  --role Contributor --scope $(az group show -n rg-shortener --query id -o tsv)

# the subject must match GitHub's token exactly; GitHub now sends the form with numeric IDs
# (repo:<owner>@<owner id>/<repo>@<repo id>:ref:refs/heads/main). A failed login's
# AADSTS700213 error shows the exact value to use.
az identity federated-credential create -g rg-shortener --identity-name id-shortener-deploy -n github-main \
  --issuer https://token.actions.githubusercontent.com --subject '<subject>' --audiences api://AzureADTokenExchange
```

Repository secrets (secrets, not variables: the repo's Actions logs are public and only secrets are masked): `AZURE_CLIENT_ID`, `AZURE_TENANT_ID`, `AZURE_SUBSCRIPTION_ID`, `SHORTENER_ALLOWED_IP`, `POSTGRES_ADMIN_PASSWORD`. Pipe values in (`... | gh secret set NAME`) to keep them out of shell history. The GHCR package needs the repo under Package settings → Manage Actions access (role Write), or the `org.opencontainers.image.source` label from the Dockerfile on a first push.

Before pushing:
- Postgres must be running (`az postgres flexible-server start ...`), or the migrate step fails and nothing is deployed.
- If your public IP changed, update `SHORTENER_ALLOWED_IP` first, or the deploy locks you out.

A failed run can be repeated without redoing the jobs that passed: `gh run rerun <run id> --failed`.

## Releasing a new version by hand

Normally the pipeline does this. By hand: build and push a new image (step 1), run the migrate job if the schema changed, and deploy again with the new `SHORTENER_IMAGE`. The app gets a new revision. With `activeRevisionsMode: Single`, traffic moves to it once it is ready and the old one is stopped.

## Troubleshooting

```bash
az containerapp logs show -n shortener -g rg-shortener --tail 50                  # app console output (JSON lines)
az containerapp logs show -n shortener -g rg-shortener --type system --tail 50    # pulls, probes, restarts
az containerapp revision list -n shortener -g rg-shortener -o table               # revision health
```

Job logs: portal → the job → Execution history → Console logs, or `az containerapp job logs show -n shortener-migrate -g rg-shortener` (needs the `containerapp` CLI extension, which `az` offers to install on first use).

## Keeping the cost down

```bash
az postgres flexible-server stop  -g rg-shortener -n <server name>   # stopped servers pay only for storage
az postgres flexible-server start -g rg-shortener -n <server name>   # Azure restarts a stopped server after 7 days
az group delete -n rg-shortener                                      # removes everything
```

The app scales to zero by itself when idle. The server name is in the deployment outputs (`postgresHost`, before the first dot).
