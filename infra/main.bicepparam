// Personal and secret values come from environment variables at deploy time, never from this file:
//   SHORTENER_IMAGE, SHORTENER_ALLOWED_IP, POSTGRES_ADMIN_PASSWORD
using './main.bicep'

param image = readEnvironmentVariable('SHORTENER_IMAGE')
param allowedClientIp = readEnvironmentVariable('SHORTENER_ALLOWED_IP')
param postgresAdminPassword = readEnvironmentVariable('POSTGRES_ADMIN_PASSWORD')
