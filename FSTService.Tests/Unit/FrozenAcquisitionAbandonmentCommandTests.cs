using FSTService.Persistence;

namespace FSTService.Tests.Unit;

public sealed class FrozenAcquisitionAbandonmentCommandTests
{
    internal static IReadOnlyList<string> CommandArguments() =>
        InterruptedAcquisitionNormalizationCommandTests.CommandArguments(execute: false)
            .Select(x => x.Replace("--interrupted-acquisition-", "--frozen-acquisition-", StringComparison.Ordinal)
                .Replace("normalization", "abandonment", StringComparison.Ordinal))
            .Concat([FrozenAcquisitionAbandonmentCommand.AttemptWorkerFlag + "=previous-worker",
                FrozenAcquisitionAbandonmentCommand.MessageFlag + "=Operator abandoned the acquisition."])
            .ToArray();

    [Fact]
    public void Flag_translation_preserves_literal_worker_identity_values()
    {
        var args = CommandArguments().Select(x =>
            x.StartsWith("--frozen-acquisition-worker-instance-id=", StringComparison.Ordinal)
                ? "--frozen-acquisition-worker-instance-id=worker-abandonment"
                : x).ToArray();
        Assert.Equal("worker-abandonment",
            FrozenAcquisitionAbandonmentCommand.Parse(args)!.Identity.ExpectedWorkerInstanceId);
    }

    [Fact]
    public void Parses_all_explicit_identities_and_check_mode()
    {
        var command = FrozenAcquisitionAbandonmentCommand.Parse(CommandArguments());
        Assert.NotNull(command);
        Assert.False(command.Identity.Execute);
        Assert.True(command.Identity.CheckOnly);
        Assert.Equal(1407, command.Identity.ScrapeId);
        Assert.Equal("previous-worker", command.ExpectedAttemptWorkerInstanceId);
    }

    [Theory]
    [InlineData("--api-only")]
    [InlineData("--frozen-acquisition-abandonment-execute")]
    [InlineData("--frozen-acquisition-attempt-worker-instance-id=duplicate")]
    [InlineData("--frozen-acquisition-failure-message=duplicate")]
    [InlineData("--interrupted-acquisition-normalization")]
    public void Rejects_mixed_modes_unknown_flags_and_duplicate_identities(string arg)
        => Assert.Throws<ArgumentException>(() =>
            FrozenAcquisitionAbandonmentCommand.Parse(CommandArguments().Append(arg).ToArray()));

    [Fact]
    public void Requires_attempt_worker_identity_and_failure_message()
    {
        foreach (var flag in new[] { FrozenAcquisitionAbandonmentCommand.AttemptWorkerFlag,
            FrozenAcquisitionAbandonmentCommand.MessageFlag })
            Assert.Throws<ArgumentException>(() => FrozenAcquisitionAbandonmentCommand.Parse(
                CommandArguments().Where(x => !x.StartsWith(flag, StringComparison.Ordinal)).ToArray()));
    }
}
