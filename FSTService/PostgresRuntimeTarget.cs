using System.Text.RegularExpressions;
using Npgsql;

namespace FSTService;

public sealed class PostgresRuntimeTarget
{
    private static readonly Regex ReadOnlyOptionPattern = new(
        @"(?:^|\s)-c\s+default_transaction_read_only\s*=\s*on(?:\s|$)",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    public string Host { get; init; } = "";
    public int Port { get; init; }
    public string Database { get; init; } = "";
    public string Username { get; init; } = "";
    public bool DefaultTransactionReadOnlyOption { get; init; }

    public static PostgresRuntimeTarget FromConnectionString(string connectionString)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        return new PostgresRuntimeTarget
        {
            Host = builder.Host ?? "",
            Port = builder.Port,
            Database = builder.Database ?? "",
            Username = builder.Username ?? "",
            DefaultTransactionReadOnlyOption =
                ReadOnlyOptionPattern.IsMatch(builder.Options ?? ""),
        };
    }
}
