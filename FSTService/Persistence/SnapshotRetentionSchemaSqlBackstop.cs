using System.Text.RegularExpressions;

namespace FSTService.Persistence;

internal static class SnapshotRetentionSchemaSqlBackstop
{
    private const string Trivia = @"(?:\s|/\*.*?\*/|--[^\r\n]*(?:\r?\n|$))";
    private const string Gap = Trivia + "+";
    private const string Space = Trivia + "*";
    private const string Identifier = """(?:[A-Za-z_][A-Za-z0-9_$]*|"(?:[^"]|"")+")""";
    private const string Relation = Identifier + "(?:" + Space + @"\." + Space + Identifier + ")*";
    private static readonly Regex Mutation = new(
        @"(?<![\w$])(?:"
        + "INSERT" + Gap + @"INTO\b|DELETE" + Gap + @"FROM\b|MERGE" + Gap + @"INTO\b|"
        + "UPDATE" + Gap + "(?:ONLY" + Gap + ")?" + Relation
        + "(?:" + Gap + "(?:AS" + Gap + ")?" + Identifier + ")?" + Gap + @"SET\b|"
        + "TRUNCATE" + Gap + "(?:TABLE" + Gap + ")?(?:ONLY" + Gap + @")?(?!ON\b|OR\b)" + Relation + "|"
        + "COPY" + Gap + "(?:BINARY" + Gap + ")?" + Relation
        + "(?:" + Space + @"\([^;]*?\))?" + Gap + @"FROM\b)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline,
        TimeSpan.FromSeconds(1));

    internal static void RequireDdlOnly(string sql)
    {
        try
        {
            if (Mutation.IsMatch(sql))
                throw new SnapshotRetentionSchemaDmlRefusal("retention_schema_not_ddl_only");
        }
        catch (RegexMatchTimeoutException)
        {
            throw new SnapshotRetentionSchemaDmlRefusal("retention_schema_static_validation_unavailable");
        }
    }
}
