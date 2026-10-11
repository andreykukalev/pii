using '../stages/application.bicep'
param location = 'northeurope'
param registryName = 'acrpiidevreplace'
param sqlServerFqdn = 'pii-dev-replace-sql.database.windows.net'
param blobServiceUri = 'https://stpiidevreplace.blob.core.windows.net/'
param keyRingUri = 'https://stpiidevreplace.blob.core.windows.net/keys/keyring.xml'
param tenantId = '22222222-2222-2222-2222-222222222222'
param allowedObjectId = '33333333-3333-3333-3333-333333333333'
param webClientId = '44444444-4444-4444-4444-444444444444'
param deployWorkloads = false
param scheduleEnabled = false