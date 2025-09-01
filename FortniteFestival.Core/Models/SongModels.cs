using System;
using System.Collections.Generic;

namespace FortniteFestival.Core
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
    public int bd { get; set; } // pro vocals difficulty (may be absent; treat 0 as missing until normalized)
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

        // Friendly aliases (not serialized automatically) for clearer internal usage
        public int ReleaseYear
        {
            get => ry;
            set => ry = value;
        }
        public int Tempo
        {
            get => mt;
            set => mt = value;
        }
        // Plastic instrument difficulty aliases (mapped from intensity object 'in')
        public int PlasticGuitarDifficulty
        {
            get => @in?.pg ?? 0;
            set
            {
                if (@in == null) @in = new In();
                @in.pg = value;
            }
        }
        public int PlasticBassDifficulty
        {
            get => @in?.pb ?? 0;
            set
            {
                if (@in == null) @in = new In();
                @in.pb = value;
            }
        }
        public int PlasticDrumsDifficulty
        {
            get => @in?.pd ?? 0;
            set
            {
                if (@in == null) @in = new In();
                @in.pd = value;
            }
        }
        public int ProVocalsDifficulty
        {
            get => @in == null ? -1 : (@in.bd == 0 ? -1 : @in.bd);
            set
            {
                if (@in == null) @in = new In();
                @in.bd = value;
            }
        }
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
        public bool isSelected { get; set; }
        public string isInLocalData { get; set; } = "?";

        // Local path to downloaded artwork image (saved as <_title>.jpg)
        public string imagePath { get; set; }
    }
}
