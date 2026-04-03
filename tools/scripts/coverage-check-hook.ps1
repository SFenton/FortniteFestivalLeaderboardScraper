# PostToolUse hook: Remind about coverage after running tests.
# Only emits when a test-running tool was used.

$input = $Input | Out-String
if (-not $input) { exit 0 }

$json = $input | ConvertFrom-Json 2>$null
if (-not $json) { exit 0 }

# Only trigger on test execution tools
$testTools = @('runTests', 'run_in_terminal')
$toolName = $json.tool_name
if ($toolName -notin $testTools) { exit 0 }

# For run_in_terminal, only trigger if the command looks like a test run
if ($toolName -eq 'run_in_terminal') {
    $command = $json.tool_input.command
    if (-not $command) { exit 0 }
    if ($command -notlike '*dotnet test*' -and $command -notlike '*FSTService.Tests*') { exit 0 }
}

@{
    systemMessage = "Tests completed. Check coverage: FSTService requires 94% line coverage. If coverage dropped, add tests for uncovered paths."
} | ConvertTo-Json -Compress
