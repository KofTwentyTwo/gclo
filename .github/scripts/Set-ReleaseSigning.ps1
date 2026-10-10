# Copyright (c) 2026 James Maes (KofTwentyTwo)
# SPDX-License-Identifier: MIT

<#
.SYNOPSIS
   Configures the Azure side of gclo's release signing (ADR 0008) and prints the
   GitHub `release`-environment variables the workflow needs.

.DESCRIPTION
   Idempotent. Run interactively by the owner after `Connect-AzAccount` into the
   KofTwentyTwo tenant (Az.Accounts, Az.Resources and Az.CodeSigning modules).
   It never creates a secret, key or certificate and never touches the signing
   account, the publisher validation or the certificate profile themselves:

   1. Reads the Artifact Signing account and its certificate profile and fails if
      either is missing or the profile is not Active.
   2. Ensures an Entra application + service principal for this repository.
   3. Ensures a federated credential on it that trusts GitHub's OIDC issuer for
      subject `repo:<owner/repo>:environment:<environment>` only.
   4. Ensures the service principal holds "Artifact Signing Certificate Profile
      Signer" scoped to the certificate profile, and nothing broader.
   5. Prints the variable values to set with `gh variable set --env release`.

.PARAMETER Repository
   The GitHub repository the credential is bound to (owner/name).

.PARAMETER Environment
   The GitHub environment whose jobs may sign (its deployment rule limits it to
   v* tags).

.PARAMETER ResourceGroup
   Resource group that holds the signing account.

.PARAMETER AccountName
   The Artifact Signing account name.

.PARAMETER ProfileName
   The certificate profile name on that account.

.PARAMETER ApplicationName
   Display name of the Entra application that represents this repository's
   release job.

.EXAMPLE
   Connect-AzAccount -Tenant koftwentytwooutlook.onmicrosoft.com
   ./.github/scripts/Set-ReleaseSigning.ps1
#>
[CmdletBinding(SupportsShouldProcess)]
param(
   [string] $Repository = 'KofTwentyTwo/gclo',
   [string] $Environment = 'release',
   [string] $ResourceGroup = 'rg-code-signing',
   [string] $AccountName = 'kof22signing',
   [string] $ProfileName = 'releases',
   [string] $ApplicationName = 'gclo-release-signing'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$signerRole = 'Artifact Signing Certificate Profile Signer'
$issuer = 'https://token.actions.githubusercontent.com'
$subject = "repo:$Repository`:environment:$Environment"

$context = Get-AzContext
if($null -eq $context)
{
   throw 'Not signed in. Run Connect-AzAccount into the KofTwentyTwo tenant first.'
}
Write-Output "Tenant $($context.Tenant.Id), subscription '$($context.Subscription.Name)' ($($context.Subscription.Id))"

# 1. The signing account and profile must already exist; this script never creates them.
#    Az.CodeSigning has no account/profile cmdlets, so this reads the ARM resources.
$apiVersion = '2024-09-30-preview'
$accountPath = "/subscriptions/$($context.Subscription.Id)/resourceGroups/$ResourceGroup/providers/Microsoft.CodeSigning/codeSigningAccounts/$AccountName"
$accountResponse = Invoke-AzRestMethod -Method GET -Path "$accountPath`?api-version=$apiVersion"
if($accountResponse.StatusCode -ne 200)
{
   throw "Signing account '$AccountName' not found in '$ResourceGroup' (HTTP $($accountResponse.StatusCode)): $($accountResponse.Content)"
}
$account = $accountResponse.Content | ConvertFrom-Json
$certProfileResponse = Invoke-AzRestMethod -Method GET -Path "$accountPath/certificateProfiles/$ProfileName`?api-version=$apiVersion"
if($certProfileResponse.StatusCode -ne 200)
{
   throw "Certificate profile '$ProfileName' not found on '$AccountName' (HTTP $($certProfileResponse.StatusCode)): $($certProfileResponse.Content)"
}
$certProfile = $certProfileResponse.Content | ConvertFrom-Json
if($certProfile.properties.status -ne 'Active')
{
   throw "Certificate profile '$ProfileName' is '$($certProfile.properties.status)', not Active."
}
$endpoint = $account.properties.accountUri
$subjectName = $certProfile.properties.commonName
Write-Output "Signing account '$AccountName' in $($account.location): endpoint $endpoint."
Write-Output "Profile '$ProfileName' is Active (type $($certProfile.properties.profileType)); certificate subject CN='$subjectName'."

# 2. One application per repository, so the trust can be revoked per repository.
$app = Get-AzADApplication -DisplayName $ApplicationName | Select-Object -First 1
if($null -eq $app)
{
   if($PSCmdlet.ShouldProcess($ApplicationName, 'Create Entra application'))
   {
      $app = New-AzADApplication -DisplayName $ApplicationName -SignInAudience AzureADMyOrg
   }
}
$sp = Get-AzADServicePrincipal -ApplicationId $app.AppId
if($null -eq $sp)
{
   if($PSCmdlet.ShouldProcess($ApplicationName, 'Create service principal'))
   {
      $sp = New-AzADServicePrincipal -ApplicationId $app.AppId
   }
}
Write-Output "Application '$ApplicationName': client id $($app.AppId), service principal $($sp.Id)."

# 3. Federated credential: GitHub OIDC, this repository's release environment only.
$credentialName = "github-$($Repository.Replace('/', '-'))-$Environment"
$existing = Get-AzADAppFederatedCredential -ApplicationObjectId $app.Id | Where-Object { $_.Subject -eq $subject -and $_.Issuer -eq $issuer }
if($null -eq $existing)
{
   if($PSCmdlet.ShouldProcess($credentialName, "Create federated credential for $subject"))
   {
      New-AzADAppFederatedCredential -ApplicationObjectId $app.Id -Name $credentialName -Issuer $issuer -Subject $subject -Audience 'api://AzureADTokenExchange' | Out-Null
   }
}
Write-Output "Federated credential trusts $issuer for subject '$subject'."

# 4. Signer role on the certificate profile only. Owner/Contributor grant no signing right.
$profileScope = $certProfile.Id
$assignment = Get-AzRoleAssignment -ObjectId $sp.Id -Scope $profileScope -RoleDefinitionName $signerRole -ErrorAction SilentlyContinue
if($null -eq $assignment)
{
   if($PSCmdlet.ShouldProcess($profileScope, "Assign '$signerRole'"))
   {
      New-AzRoleAssignment -ObjectId $sp.Id -Scope $profileScope -RoleDefinitionName $signerRole | Out-Null
   }
}
$broader = Get-AzRoleAssignment -ObjectId $sp.Id | Where-Object { $_.Scope -ne $profileScope }
if($broader)
{
   Write-Warning "The service principal also holds: $(($broader | ForEach-Object { "$($_.RoleDefinitionName) @ $($_.Scope)" }) -join '; '). Remove anything it does not need."
}
Write-Output "'$signerRole' is assigned at $profileScope."

# 5. The values the workflow reads (none of them is a secret).
Write-Output ''
Write-Output "gh variable set AZURE_TENANT_ID          --env $Environment --repo $Repository --body '$($context.Tenant.Id)'"
Write-Output "gh variable set AZURE_CLIENT_ID          --env $Environment --repo $Repository --body '$($app.AppId)'"
Write-Output "gh variable set AZURE_SUBSCRIPTION_ID    --env $Environment --repo $Repository --body '$($context.Subscription.Id)'"
Write-Output "gh variable set TRUSTED_SIGNING_ENDPOINT --env $Environment --repo $Repository --body '$endpoint'"
Write-Output "gh variable set TRUSTED_SIGNING_ACCOUNT  --env $Environment --repo $Repository --body '$AccountName'"
Write-Output "gh variable set TRUSTED_SIGNING_PROFILE  --env $Environment --repo $Repository --body '$ProfileName'"
Write-Output "gh variable set TRUSTED_SIGNING_SUBJECT  --env $Environment --repo $Repository --body '$subjectName'"
