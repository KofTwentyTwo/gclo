#Requires -Version 7.4
<#
.SYNOPSIS
   Writes release notes for a tag from the Conventional Commit subjects merged since
   the previous release (K22-REL-02).

.DESCRIPTION
   Pull requests squash-merge with their Conventional Commit title as the subject,
   so the commits between the previous published release and this tag are the
   changelog. Subjects are grouped by type with breaking changes and security
   fixes first; subjects that are not Conventional Commits land under "Other
   changes" so nothing is silently dropped. Each entry links its pull request
   ("(#123)" suffix) or, failing that, its commit. When an issue titled
   "Release <tag>" exists (the release checklist, K22-REL-12) it is linked too.

   Needs the GitHub CLI authenticated for the repository (GH_TOKEN in Actions).

.PARAMETER Tag
   The release tag, e.g. v1.2.3 or v1.2.3-beta.1.

.PARAMETER Repository
   owner/name on GitHub.

.PARAMETER OutputPath
   Where the Markdown notes are written.

.PARAMETER PreviousTag
   The tag to compare from. Default: the most recent published release before
   this tag, or the repository's first commit when there is none.
#>
[CmdletBinding()]
param(
   [Parameter(Mandatory)]
   [ValidatePattern('^v\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$')]
   [string] $Tag,

   [Parameter(Mandatory)]
   [ValidatePattern('^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$')]
   [string] $Repository,

   [Parameter(Mandatory)]
   [string] $OutputPath,

   [string] $PreviousTag
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

<#
.SYNOPSIS
   Calls the GitHub REST API through gh and returns the parsed JSON, or $null when
   the call fails (the caller decides whether that matters).
#>
function Invoke-GitHubApi
{
   param(
      [Parameter(Mandatory)] [string] $Path,
      [switch] $Paginate
   )

   $arguments = @('api', '-H', 'Accept: application/vnd.github+json')
   if($Paginate)
   {
      $arguments += @('--paginate', '--slurp')
   }
   $arguments += $Path

   $output = & gh @arguments 2>$null
   if($LASTEXITCODE -ne 0)
   {
      return $null
   }
   return (($output | ForEach-Object { "$_" }) -join "`n") | ConvertFrom-Json -Depth 50
}

<#
.SYNOPSIS
   Finds the tag of the most recent published release that precedes the one being
   released, so the notes cover exactly what changed since users last got a build.
#>
function Find-PreviousReleaseTag
{
   param([Parameter(Mandatory)] [string] $CurrentTag)

   $pages = Invoke-GitHubApi -Path "repos/$Repository/releases?per_page=100" -Paginate
   if($null -eq $pages)
   {
      return $null
   }

   $releases = @($pages | ForEach-Object { $_ }) |
      Where-Object { -not $_.draft -and $_.tag_name -ne $CurrentTag } |
      Sort-Object { [DateTime] $_.created_at } -Descending
   if(@($releases).Count -eq 0)
   {
      return $null
   }
   return @($releases)[0].tag_name
}

<#
.SYNOPSIS
   Lists the commits between two refs through the compare API: subject, body, sha,
   and the pull request number when the subject carries a "(#123)" suffix.
#>
function Get-CommitsBetween
{
   param(
      [string] $From,
      [Parameter(Mandatory)] [string] $To
   )

   if([string]::IsNullOrEmpty($From))
   {
      $pages = Invoke-GitHubApi -Path "repos/$Repository/commits?sha=$To&per_page=100" -Paginate
      $commits = @($pages | ForEach-Object { $_ })
   }
   else
   {
      $compare = Invoke-GitHubApi -Path "repos/$Repository/compare/$From...$To"
      if($null -eq $compare)
      {
         throw "Cannot compare $From...$To in $Repository."
      }
      $commits = @($compare.commits)
   }

   foreach($commit in $commits)
   {
      $lines = $commit.commit.message -split "`r?`n"
      $subject = $lines[0].Trim()
      if($subject -match '^Merge (pull request|branch) ')
      {
         continue
      }
      $pullNumber = $null
      if($subject -match '\s\(#(\d+)\)$')
      {
         $pullNumber = [int] $Matches[1]
         $subject = $subject -replace '\s\(#\d+\)$', ''
      }
      $body = ($lines | Select-Object -Skip 1) -join "`n"
      [pscustomobject]@{ Sha = $commit.sha; Subject = $subject; Body = $body; PullNumber = $pullNumber }
   }
}

<#
.SYNOPSIS
   Classifies one commit into a notes section from its Conventional Commit header.
   Breaking changes ("!" or a BREAKING CHANGE footer) and security fixes come
   first; anything that is not a Conventional Commit goes to "Other changes".
#>
function Get-NotesSection
{
   param([Parameter(Mandatory)] [pscustomobject] $Commit)

   $header = '^(?<type>[a-z]+)(\((?<scope>[^)]+)\))?(?<bang>!)?: (?<description>.+)$'
   if($Commit.Subject -notmatch $header)
   {
      return [pscustomobject]@{ Section = 'Other changes'; Text = $Commit.Subject }
   }

   $type = $Matches['type']
   $scope = $Matches['scope']
   $description = $Matches['description']
   $text = if($scope) { "**$scope**: $description" } else { $description }

   if($Matches['bang'] -eq '!' -or $Commit.Body -match '(?m)^BREAKING[ -]CHANGE:')
   {
      return [pscustomobject]@{ Section = 'Breaking changes'; Text = $text }
   }
   if($type -eq 'security' -or $scope -eq 'security')
   {
      return [pscustomobject]@{ Section = 'Security'; Text = $text }
   }

   $section = switch($type)
   {
      'feat' { 'Features' }
      'fix' { 'Fixes' }
      'perf' { 'Performance' }
      'refactor' { 'Refactoring' }
      'docs' { 'Documentation' }
      'build' { 'Build and dependencies' }
      'ci' { 'CI' }
      'test' { 'Tests' }
      'chore' { 'Chores' }
      'style' { 'Style' }
      'revert' { 'Reverts' }
      default { 'Other changes' }
   }
   return [pscustomobject]@{ Section = $section; Text = $text }
}

<#
.SYNOPSIS
   Finds the release-checklist issue ("Release <tag>") so the notes can link it.
#>
function Find-ChecklistIssue
{
   param([Parameter(Mandatory)] [string] $CurrentTag)

   $query = [uri]::EscapeDataString("repo:$Repository is:issue in:title `"Release $CurrentTag`"")
   $result = Invoke-GitHubApi -Path "search/issues?q=$query&per_page=5"
   if($null -eq $result)
   {
      return $null
   }
   $match = @($result.items) | Where-Object { $_.title -eq "Release $CurrentTag" } | Select-Object -First 1
   return $match
}

################################################################################
## Resolve the range, classify every commit, and write the notes in section   ##
## order with breaking changes and security fixes first (K22-REL-02).         ##
################################################################################
if([string]::IsNullOrEmpty($PreviousTag))
{
   $PreviousTag = Find-PreviousReleaseTag -CurrentTag $Tag
}

$commits = @(Get-CommitsBetween -From $PreviousTag -To $Tag)
$order = @('Breaking changes', 'Security', 'Features', 'Fixes', 'Performance', 'Refactoring',
   'Documentation', 'Build and dependencies', 'CI', 'Tests', 'Style', 'Chores', 'Reverts', 'Other changes')
$grouped = @{}
foreach($commit in $commits)
{
   $entry = Get-NotesSection -Commit $commit
   $link = if($null -ne $commit.PullNumber)
   {
      "[#$($commit.PullNumber)](https://github.com/$Repository/pull/$($commit.PullNumber))"
   }
   else
   {
      "[$($commit.Sha.Substring(0, 7))](https://github.com/$Repository/commit/$($commit.Sha))"
   }
   if(-not $grouped.ContainsKey($entry.Section))
   {
      $grouped[$entry.Section] = [System.Collections.Generic.List[string]]::new()
   }
   $grouped[$entry.Section].Add("- $($entry.Text) ($link)")
}

$notes = [System.Collections.Generic.List[string]]::new()
foreach($section in $order)
{
   if(-not $grouped.ContainsKey($section))
   {
      continue
   }
   $notes.Add("## $section")
   $notes.Add('')
   foreach($line in $grouped[$section])
   {
      $notes.Add($line)
   }
   $notes.Add('')
}
if($commits.Count -eq 0)
{
   $notes.Add('No changes since the previous release.')
   $notes.Add('')
}

$checklist = Find-ChecklistIssue -CurrentTag $Tag
if($null -ne $checklist)
{
   $notes.Add("Release checklist: [#$($checklist.number)]($($checklist.html_url))")
   $notes.Add('')
}
if(-not [string]::IsNullOrEmpty($PreviousTag))
{
   $notes.Add("**Full changelog**: https://github.com/$Repository/compare/$PreviousTag...$Tag")
}
else
{
   $notes.Add("**Full changelog**: https://github.com/$Repository/commits/$Tag")
}
$notes.Add('')
$notes.Add('Every asset is listed in `SHA256SUMS` and carries a build-provenance attestation; a CycloneDX SBOM per artifact is attached. See "Verifying a release" in the README.')

$directory = Split-Path -Parent ([System.IO.Path]::GetFullPath($OutputPath))
if(-not (Test-Path -LiteralPath $directory))
{
   New-Item -ItemType Directory -Path $directory | Out-Null
}
[System.IO.File]::WriteAllText($OutputPath, (($notes -join "`n") + "`n"), [System.Text.UTF8Encoding]::new($false))
Write-Verbose "Wrote $($commits.Count) commit(s) of notes to $OutputPath"
