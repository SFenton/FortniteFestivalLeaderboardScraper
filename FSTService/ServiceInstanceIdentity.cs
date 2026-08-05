namespace FSTService;

public sealed class ServiceInstanceIdentity
{
    public string Nonce { get; } = Guid.NewGuid().ToString("N");
    public string HostName { get; } = Environment.MachineName;
    public int ProcessId { get; } = Environment.ProcessId;
    public DateTimeOffset StartedAtUtc { get; } = DateTimeOffset.UtcNow;
}
