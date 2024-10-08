﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace FortniteFestivalLeaderboardScraper.Helpers
{
    public class In
    {
        public int pb { get; set; }
        public int pd { get; set; }
        public int vl { get; set; }
        public int pg { get; set; }
        public string _type { get; set; }
        public int gr { get; set; }
        public int ds { get; set; }
        public int ba { get; set; }
    }

    public class Song
    {
        public string _title { get; set; }
        public Track track { get; set; }
        public bool _noIndex { get; set; }
        public DateTime _activeDate { get; set; }
        public DateTime lastModified { get; set; }
        public string _locale { get; set; }
        public string _templateName { get; set; }
        public Boolean isSelected { get; set; } = false;
        public string isInLocalData { get; set; } = "❌";
    }

    public class Track
    {
        public string tt { get; set; }
        public int ry { get; set; }
        public int dn { get; set; }
        public string sib { get; set; }
        public string sid { get; set; }
        public string sig { get; set; }
        public string qi { get; set; }
        public string sn { get; set; }
        public List<string> ge { get; set; }
        public string mk { get; set; }
        public string mm { get; set; }
        public string ab { get; set; }
        public string siv { get; set; }
        public string su { get; set; }
        public In @in { get; set; }
        public int mt { get; set; }
        public string _type { get; set; }
        public string mu { get; set; }
        public string an { get; set; }
        public List<string> gt { get; set; }
        public string ar { get; set; }
        public string au { get; set; }
        public string ti { get; set; }
        public string ld { get; set; }
        public string jc { get; set; }
    }
}
