# PostToolUse hook: Remind to run tests after editing FSTService files.
# Only emits when a file-editing tool was used on an FSTService source file.

$input = $Input | Out-String
if (-not $input) { exit 0 }

$json = $input | ConvertFrom-Json 2>$null
if (-not $json) { exit 0 }

# Only trigger on file-editing tools
$editTools = @('replace_string_in_file', 'create_file', 'multi_replace_string_in_file')
$toolName = $json.tool_name
if ($toolName -notin $editTools) { exit 0 }

# Check if the edited file is in FSTService (not Tests)
$filePath = $json.tool_input.filePath
if (-not $filePath) { exit 0 }

$normalized = $filePath -replace '\\', '/'
if ($normalized -notlike '*FSTService/*' -or $normalized -like '*FSTService.Tests/*') { exit 0 }

@{
    systemMessage = "File edited in FSTService. Remember to run tests: dotnet test FSTService.Tests\FSTService.Tests.csproj and verify 94% coverage threshold."
} | ConvertTo-Json -Compress
