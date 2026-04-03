# PostToolUse hook: Detect cross-territory edits and route to owning agent.
# Called by .github/hooks/agent-evolution.json

$input = $Input | Out-String
if (-not $input) { exit 0 }

$json = $input | ConvertFrom-Json 2>$null
if (-not $json) { exit 0 }

# Only trigger on file-editing tools
$editTools = @('replace_string_in_file', 'create_file', 'multi_replace_string_in_file')
$toolName = $json.tool_name
if ($toolName -notin $editTools) { exit 0 }

# Get the edited file path
$file = $json.tool_input.filePath
if (-not $file) { exit 0 }

# Skip agent/instruction files — those are self-managed
if ($file -like '*.agent.md' -or $file -like '*.instructions.md' -or $file -like '*AGENTS.md') {
    exit 0
}

# Call agent-ownership-map.ps1 to determine which agent owns this file
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$mapScript = Join-Path $scriptDir 'agent-ownership-map.ps1'

$result = & powershell -NoProfile -File $mapScript -ChangedFile $file 2>$null
$parsed = $result | ConvertFrom-Json 2>$null

if ($parsed -and $parsed.owningAgent) {
    @{
        systemMessage = "Source file edited in $($parsed.owningAgent) territory ($file). Self-assess: if you ARE $($parsed.owningAgent), check if your own .agent.md ownership/constraints need updating (self-update, no escalation needed). If you are NOT $($parsed.owningAgent), this is a cross-territory edit - note for the owning agent's parent."
    } | ConvertTo-Json -Compress
}
