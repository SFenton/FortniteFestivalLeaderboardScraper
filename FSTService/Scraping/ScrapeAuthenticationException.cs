namespace FSTService.Scraping;

/// <summary>
/// Raised when leaderboard scraping cannot recover from an authorization failure.
/// The scrape pass must not be completed or published after this exception.
/// </summary>
public sealed class ScrapeAuthenticationException : Exception
{
    public ScrapeAuthenticationException(string message) : base(message)
    {
    }

    public ScrapeAuthenticationException(string message, Exception innerException) : base(message, innerException)
    {
    }
}