using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FortniteFestival.Core
{
    public class In
    {
        private readonly HashSet<string> _presentProviderProperties =
            new(StringComparer.Ordinal);
        private int _pb;
        private int _pd;
        private int _vl;
        private int _pg;
        private int _gr;
        private int _ds;
        private int _ba;
        private int _bd;

        public int pb
        {
            get => _pb;
            set { _pb = value; _presentProviderProperties.Add(nameof(pb)); }
        }
        public int pd
        {
            get => _pd;
            set { _pd = value; _presentProviderProperties.Add(nameof(pd)); }
        }
        public int vl
        {
            get => _vl;
            set { _vl = value; _presentProviderProperties.Add(nameof(vl)); }
        }
        public int pg
        {
            get => _pg;
            set { _pg = value; _presentProviderProperties.Add(nameof(pg)); }
        }
        public string _type { get; set; }
        public int gr
        {
            get => _gr;
            set { _gr = value; _presentProviderProperties.Add(nameof(gr)); }
        }
        public int ds
        {
            get => _ds;
            set { _ds = value; _presentProviderProperties.Add(nameof(ds)); }
        }
        public int ba
        {
            get => _ba;
            set { _ba = value; _presentProviderProperties.Add(nameof(ba)); }
        }
        public int bd
        {
            get => _bd;
            set { _bd = value; _presentProviderProperties.Add(nameof(bd)); }
        } // pro vocals difficulty (may be absent; treat 0 as missing until normalized)

        [JsonExtensionData]
        public Dictionary<string, JsonElement> providerFields { get; set; }

        public bool HasProviderProperty(string propertyName)
            => _presentProviderProperties.Contains(propertyName);
    }

    public class Track
    {
        public static bool HasChartedDifficulty(int? difficulty)
            => difficulty.HasValue && difficulty.Value >= 0 && difficulty.Value != 99;

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

        [JsonExtensionData]
        public Dictionary<string, JsonElement> providerFields { get; set; }

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
            get => HasChartedDifficulty(@in?.bd) ? @in!.bd : -1;
            set
            {
                if (@in == null) @in = new In();
                @in.bd = value;
            }
        }

        public bool HasProVocalsChart => HasChartedDifficulty(@in?.bd);
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

        [JsonExtensionData]
        public Dictionary<string, JsonElement> providerFields { get; set; }

        [JsonIgnore]
        public JsonElement? providerJson { get; set; }

        public void ReplaceProviderDataFrom(Song incoming)
        {
            if (incoming == null)
                throw new ArgumentNullException(nameof(incoming));

            _title = incoming._title;
            track = incoming.track;
            _noIndex = incoming._noIndex;
            _activeDate = incoming._activeDate;
            lastModified = incoming.lastModified;
            _locale = incoming._locale;
            _templateName = incoming._templateName;
            providerFields = incoming.providerFields;
            providerJson = incoming.providerJson;
        }
    }
}
