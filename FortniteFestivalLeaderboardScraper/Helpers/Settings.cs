using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace FortniteFestivalLeaderboardScraper.Helpers
{
    public class Settings
    {
        public OutputSelection outputSelection { get; set; } = OutputSelection.FullCombo;
        public bool writeLead { get; set; } = true;
        public bool writeBass { get; set; } = true;
        public bool writeVocals { get; set; } = true;
        public bool writeDrums { get; set; } = true;
        public bool writeProLead { get; set; } = true;
        public bool writeProBass { get; set; } = true;
        public bool invertOutput { get; set; } = false;
    }
}
