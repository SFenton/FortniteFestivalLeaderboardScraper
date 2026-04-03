# Agent Ownership Map
# Maps source directories to their owning agents.
# Used by the agent-evolution hook to detect when source changes
# may require agent file updates.

param(
    [string]$ChangedFile
)

$map = @{
    # FSTService sub-agents
    "FSTService/Scraping/"       = "fst-scrape-pipeline"
    "FSTService/Api/"            = "fst-api"
    "FSTService/Persistence/"    = "fst-persistence"
    "FSTService/Auth/"           = "fst-auth"
    "FSTService/Program.cs"      = "fst-head"
    "FSTService/ScraperWorker.cs"= "fst-head"
    "FSTService/FeatureOptions.cs"= "fst-head"

    # Web infra agents
    "FortniteFestivalWeb/src/components/" = "web-components"
    "FortniteFestivalWeb/src/styles/"     = "web-styling"
    "FortniteFestivalWeb/src/contexts/"   = "web-state"
    "FortniteFestivalWeb/src/hooks/"      = "web-state"
    "FortniteFestivalWeb/src/api/"        = "web-state"
    "FortniteFestivalWeb/src/App.tsx"     = "web-feat-shell"
    "FortniteFestivalWeb/src/routes.ts"   = "web-feat-shell"

    # Web feature agents
    "FortniteFestivalWeb/src/pages/rivals/"       = "web-feat-rivals"
    "FortniteFestivalWeb/src/pages/shop/"         = "web-feat-shop"
    "FortniteFestivalWeb/src/pages/songs/"        = "web-feat-songs"
    "FortniteFestivalWeb/src/pages/songinfo/"     = "web-feat-songs"
    "FortniteFestivalWeb/src/pages/player/"       = "web-feat-player"
    "FortniteFestivalWeb/src/pages/leaderboards/" = "web-feat-leaderboards"
    "FortniteFestivalWeb/src/pages/suggestions/"  = "web-feat-suggestions"
    "FortniteFestivalWeb/src/pages/compete/"      = "web-feat-suggestions"
    "FortniteFestivalWeb/src/pages/settings/"     = "web-feat-settings"

    # Test agents
    "FSTService.Tests/"                     = "fst-testing"
    "FortniteFestivalWeb/__test__/"         = "web-test-vitest"
    "FortniteFestivalWeb/e2e/"             = "web-test-playwright"

    # Cross-cutting
    ".github/workflows/"  = "cicd"
    "deploy/"             = "cicd"
    "packages/"           = "shared-packages"
}

# Normalize path separators
$normalizedFile = $ChangedFile -replace '\\', '/'

# Find the longest matching prefix (most specific match)
$bestMatch = $null
$bestLength = 0

foreach ($prefix in $map.Keys) {
    if ($normalizedFile -like "*$prefix*" -and $prefix.Length -gt $bestLength) {
        $bestMatch = $map[$prefix]
        $bestLength = $prefix.Length
    }
}

if ($bestMatch) {
    # Output JSON for the hook
    @{
        owningAgent = $bestMatch
        changedFile = $ChangedFile
    } | ConvertTo-Json -Compress
} else {
    # No specific agent owns this file
    @{
        owningAgent = $null
        changedFile = $ChangedFile
    } | ConvertTo-Json -Compress
}
