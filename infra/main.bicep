// Stage 1 of the Azure plan (PLAN.md, "Deployment shape"): Container Apps with public ingress,
// Postgres Flexible Server with public access, logs in Log Analytics.
// Deploy into an existing resource group: az deployment group create -g <rg> -f infra/main.bicep -p infra/main.bicepparam

@description('Region for every resource; defaults to the resource group\'s region.')
param location string = resourceGroup().location

@description('Container image to run, e.g. ghcr.io/<owner>/url-shortener:<git sha>.')
param image string

@description('The only client address allowed through the ingress until stage 3 (APIM) protects POST /shorten.')
param allowedClientIp string

param postgresAdminLogin string = 'shortener_admin'

@secure()
@description('Postgres admin password, supplied at deploy time and never stored in the repo.')
param postgresAdminPassword string

var appName = 'shortener'
// Postgres server names are global DNS names, so add a suffix that is stable per resource group
var postgresName = 'shortener-pg-${uniqueString(resourceGroup().id)}'
var databaseName = 'shortener'

// ---- logs -------------------------------------------------------------------------------------

resource logs 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: 'shortener-logs'
  location: location
  properties: {
    sku: { name: 'PerGB2018' }
    retentionInDays: 30
    // the first 5 GB per month are free; 0.1 GB per day stays under that even on a busy month
    workspaceCapping: { dailyQuotaGb: json('0.1') }
  }
}

// ---- database ---------------------------------------------------------------------------------

resource postgres 'Microsoft.DBforPostgreSQL/flexibleServers@2024-08-01' = {
  name: postgresName
  location: location
  sku: {
    name: 'Standard_B1ms'
    tier: 'Burstable'
  }
  properties: {
    version: '17'
    administratorLogin: postgresAdminLogin
    administratorLoginPassword: postgresAdminPassword
    storage: { storageSizeGB: 32 }
    backup: {
      backupRetentionDays: 7
      geoRedundantBackup: 'Disabled'
    }
    highAvailability: { mode: 'Disabled' }
    network: { publicNetworkAccess: 'Enabled' }
  }
}

resource database 'Microsoft.DBforPostgreSQL/flexibleServers/databases@2024-08-01' = {
  parent: postgres
  name: databaseName
  properties: {
    charset: 'UTF8'
    collation: 'en_US.utf8'
  }
}

// 0.0.0.0 means "any Azure service": Container Apps on the consumption plan has no fixed outbound
// address. Weak (other Azure tenants pass too, the password still guards it); stage 2 replaces this
// with private access inside a VNet.
resource allowAzure 'Microsoft.DBforPostgreSQL/flexibleServers/firewallRules@2024-08-01' = {
  parent: postgres
  name: 'AllowAzureServices'
  properties: {
    startIpAddress: '0.0.0.0'
    endIpAddress: '0.0.0.0'
  }
}

// ---- container apps ---------------------------------------------------------------------------

resource environment 'Microsoft.App/managedEnvironments@2024-03-01' = {
  name: 'shortener-env'
  location: location
  properties: {
    appLogsConfiguration: {
      destination: 'log-analytics'
      logAnalyticsConfiguration: {
        customerId: logs.properties.customerId
        sharedKey: logs.listKeys().primarySharedKey
      }
    }
  }
}

var connectionString = 'Host=${postgres.properties.fullyQualifiedDomainName};Database=${databaseName};Username=${postgresAdminLogin};Password=${postgresAdminPassword};SSL Mode=Require'

// the app and the migrate job share the secret and the storage settings
var secrets = [
  { name: 'db-connection', value: connectionString }
]
var storageEnv = [
  { name: 'Storage', value: 'Postgres' }
  { name: 'ConnectionStrings__Shortener', secretRef: 'db-connection' }
]

// the app's public address: <app name>.<environment's default domain>, known before the app exists
var baseUrl = 'https://${appName}.${environment.properties.defaultDomain}'

resource app 'Microsoft.App/containerApps@2024-03-01' = {
  name: appName
  location: location
  properties: {
    managedEnvironmentId: environment.id
    configuration: {
      activeRevisionsMode: 'Single'
      secrets: secrets
      ingress: {
        external: true
        targetPort: 8080
        allowInsecure: false // plain http is redirected to https
        ipSecurityRestrictions: [
          {
            name: 'owner'
            action: 'Allow'
            ipAddressRange: '${allowedClientIp}/32'
          }
        ]
      }
    }
    template: {
      containers: [
        {
          name: appName
          image: image
          resources: {
            cpu: json('0.25')
            memory: '0.5Gi'
          }
          env: concat(storageEnv, [
            { name: 'ASPNETCORE_ENVIRONMENT', value: 'Production' }
            { name: 'Shortener__BaseUrl', value: baseUrl }
            // the app is reachable only through the ingress, so any peer is the ingress
            { name: 'ForwardedHeaders__Enabled', value: 'true' }
            { name: 'ForwardedHeaders__TrustAnyProxy', value: 'true' }
          ])
          probes: [
            {
              type: 'Liveness'
              httpGet: { path: '/health/live', port: 8080 }
            }
            {
              type: 'Readiness'
              httpGet: { path: '/health/ready', port: 8080 }
            }
          ]
        }
      ]
      scale: {
        minReplicas: 0 // scale to zero when idle: no cost
        // the rate-limit buckets live in each replica's memory, so 2 replicas would double the limit;
        // raising this needs a shared limiter first
        maxReplicas: 1
      }
    }
  }
}

// `migrate` as a one-shot job: same image, started by hand or by the deploy pipeline before the app
resource migrate 'Microsoft.App/jobs@2024-03-01' = {
  name: 'shortener-migrate'
  location: location
  properties: {
    environmentId: environment.id
    configuration: {
      triggerType: 'Manual'
      replicaTimeout: 600
      replicaRetryLimit: 0 // a failed migration needs a person, not a retry
      manualTriggerConfig: {
        parallelism: 1
        replicaCompletionCount: 1
      }
      secrets: secrets
    }
    template: {
      containers: [
        {
          name: 'migrate'
          image: image
          args: ['migrate'] // appended to the image's ENTRYPOINT, like `command:` in compose.yaml
          resources: {
            cpu: json('0.25')
            memory: '0.5Gi'
          }
          env: storageEnv
        }
      ]
    }
  }
}

output appUrl string = baseUrl
output postgresHost string = postgres.properties.fullyQualifiedDomainName
