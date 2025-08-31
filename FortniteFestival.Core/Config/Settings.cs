namespace FortniteFestival.Core.Config
{
    public class Settings
    {
        public int DegreeOfParallelism { get; set; } = 16;
        public bool QueryLead { get; set; } = true;
        public bool QueryDrums { get; set; } = true;
        public bool QueryVocals { get; set; } = true;
        public bool QueryBass { get; set; } = true;
        public bool QueryProLead { get; set; } = true;
        public bool QueryProBass { get; set; } = true;
    }
}
