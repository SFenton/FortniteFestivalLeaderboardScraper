using FSTService.Auth;
using Microsoft.Extensions.Logging.Abstractions;

namespace FSTService.Tests.Unit;

public sealed class EpicAuthServiceCredentialTests
{
    [Theory]
    [InlineData("", "test-secret")]
    [InlineData("test-client", "")]
    [InlineData(" ", "test-secret")]
    [InlineData("test-client", " ")]
    public void Constructor_RejectsMissingClientCredentials(string clientId, string clientSecret)
    {
        var action = () => new EpicAuthService(
            new HttpClient(),
            NullLogger<EpicAuthService>.Instance,
            clientId,
            clientSecret);

        Assert.Throws<ArgumentException>(action);
    }
}
