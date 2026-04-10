using System.Text.Json.Serialization;
using FSTService.Scraping;

namespace FSTService.Persistence;

/// <summary>
/// Source-generated JSON serialization context for band member stats.
/// Used by <see cref="InstrumentDatabase"/> to serialize band data into the
/// <c>band_members_json</c> JSONB column on <c>leaderboard_entries</c>.
/// </summary>
[JsonSerializable(typeof(List<BandMemberStats>))]
internal partial class BandMembersJsonContext : JsonSerializerContext
{
}
