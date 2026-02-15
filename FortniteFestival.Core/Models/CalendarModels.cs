using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace FortniteFestival.Core.Models
{
    // Calendar / event related models migrated from UI layer.
    public class ActiveEvent
    {
        public string eventType { get; set; }
        public DateTime activeUntil { get; set; }
        public DateTime activeSince { get; set; }
        public string instanceId { get; set; }
        public string devName { get; set; }
        public string eventName { get; set; }
        public DateTime eventStart { get; set; }
        public DateTime eventEnd { get; set; }
    }

    public class ASIA
    {
        public List<string> eventFlagsForcedOff { get; set; }
    }

    public class BR
    {
        public List<string> eventFlagsForcedOn { get; set; }
        public List<string> eventFlagsForcedOff { get; set; }
    }

    public class StandaloneStore
    {
        public List<State> states { get; set; }
        public DateTime cacheExpire { get; set; }
    }

    public class ClientMatchmaking
    {
        public List<State> states { get; set; }
        public DateTime cacheExpire { get; set; }
    }

    public class ClientEvents
    {
        public List<State> states { get; set; }
        public DateTime cacheExpire { get; set; }
    }

    public class FeaturedIslands
    {
        public List<State> states { get; set; }
        public DateTime cacheExpire { get; set; }
    }

    public class CommunityVotes
    {
        public List<State> states { get; set; }
        public DateTime cacheExpire { get; set; }
    }

    public class Tk
    {
        public List<State> states { get; set; }
        public DateTime cacheExpire { get; set; }
    }

    public class Channels
    {
        [JsonProperty("standalone-store")]
        public StandaloneStore standalonestore { get; set; }

        [JsonProperty("client-matchmaking")]
        public ClientMatchmaking clientmatchmaking { get; set; }

        [JsonProperty("featured-islands")]
        public FeaturedIslands featuredislands { get; set; }

        [JsonProperty("community-votes")]
        public CommunityVotes communityvotes { get; set; }

        [JsonProperty("client-events")]
        public ClientEvents clientevents { get; set; }
        public Tk tk { get; set; }
    }

    public class EU
    {
        public List<string> eventFlagsForcedOn { get; set; }
    }

    public class ME
    {
        public List<string> eventFlagsForcedOff { get; set; }
    }

    public class NAC
    {
        public List<string> eventFlagsForcedOn { get; set; }
    }

    public class NAE
    {
        public List<string> eventFlagsForcedOn { get; set; }
    }

    public class NAW
    {
        public List<string> eventFlagsForcedOn { get; set; }
    }

    public class OCE
    {
        public List<string> eventFlagsForcedOff { get; set; }
    }

    public class Region
    {
        public OCE OCE { get; set; }
        public ASIA ASIA { get; set; }
        public BR BR { get; set; }
        public ME ME { get; set; }
        public NAE NAE { get; set; }
        public NAW NAW { get; set; }
        public NAC NAC { get; set; }
        public EU EU { get; set; }
    }

    public class SectionStoreEnds { }

    public class EventNamedWeights { }

    public class PlaylistCuratedContent { }

    public class PlaylistCuratedHub { }

    public class Storefront { }

    public class State
    {
        public DateTime validFrom { get; set; }
        public List<object> activeEvents { get; set; }
        public StateData state { get; set; }
    }

    public class StateData
    {
        public string electionId { get; set; }
        public List<object> candidates { get; set; }
        public DateTime electionEnds { get; set; }
        public int numWinners { get; set; }
        public List<object> activeStorefronts { get; set; }
        public EventNamedWeights eventNamedWeights { get; set; }
        public List<ActiveEvent> activeEvents { get; set; }
        public int seasonNumber { get; set; }
        public string seasonTemplateId { get; set; }
        public int matchXpBonusPoints { get; set; }
        public string eventPunchCardTemplateId { get; set; }
        public DateTime seasonBegin { get; set; }
        public DateTime seasonEnd { get; set; }
        public DateTime seasonDisplayedEnd { get; set; }
        public DateTime weeklyStoreEnd { get; set; }
        public DateTime stwEventStoreEnd { get; set; }
        public DateTime stwWeeklyStoreEnd { get; set; }
        public SectionStoreEnds sectionStoreEnds { get; set; }
        public string rmtPromotion { get; set; }
        public DateTime dailyStoreEnd { get; set; }
        public List<object> activePurchaseLimitingEventIds { get; set; }
        public Storefront storefront { get; set; }
        public List<object> rmtPromotionConfig { get; set; }
        public DateTime storeEnd { get; set; }
        public Region region { get; set; }
        public List<string> k { get; set; }
        public List<object> islandCodes { get; set; }
        public PlaylistCuratedContent playlistCuratedContent { get; set; }
        public PlaylistCuratedHub playlistCuratedHub { get; set; }
        public List<object> islandTemplates { get; set; }
    }

    public class CalendarResponse
    {
        public Channels channels { get; set; }
        public double cacheIntervalMins { get; set; }
        public DateTime currentTime { get; set; }
    }
}
