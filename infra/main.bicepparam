// Personal and secret values come from environment variables at deploy time, never from this file:
//   SHORTENER_IMAGE, SHORTENER_ALLOWED_IP, POSTGRES_ADMIN_PASSWORD
// DEPLOY_POSTGRES=true creates or updates the Postgres server (start-postgres.yml); unset, the server is left alone
using './main.bicep'

param image = readEnvironmentVariable('SHORTENER_IMAGE')
param allowedClientIp = readEnvironmentVariable('SHORTENER_ALLOWED_IP')
param postgresAdminPassword = readEnvironmentVariable('POSTGRES_ADMIN_PASSWORD')
param deployPostgres = bool(readEnvironmentVariable('DEPLOY_POSTGRES', 'false'))
