using System.Collections.ObjectModel;
using System.Linq;
using FortniteFestival.Core.Models; // adjust if needed
using FortniteFestival.Core; // for ScoreTracker
using FortniteFestival.Core.Services; // for IFestivalService
using FortniteFestival.LeaderboardScraper.MAUI.ViewModels;

namespace FortniteFestival.LeaderboardScraper.MAUI.ViewModels;

public class SongInfoInstrumentRow
{
    public string Key { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Icon { get; set; } = string.Empty;
    public int StarsCount { get; set; }
    public bool HasScore { get; set; }
    public bool IsFullCombo { get; set; }
    public ObservableCollection<StarVisual> Stars { get; } = new();
    public string ScoreDisplay { get; set; } = string.Empty;
    public string PercentDisplay { get; set; } = string.Empty;
    public string SeasonDisplay { get; set; } = string.Empty;
    public bool ShowNA => !HasScore; // convenience for UI visibility
}

public class StarVisual
{
    public string Image { get; set; } = string.Empty; // star-white or star-gold
    public string OutlineColor { get; set; } = "Transparent"; // gold, red, transparent
    public string BackgroundColor { get; set; } = "Transparent"; // for missing score red fill
}

public class SongInfoViewModel : BaseViewModel // if a BaseViewModel exists; otherwise implement INotifyPropertyChanged
{
    public string Title { get; }
    public string Artist { get; }
    public string YearDisplay { get; } = string.Empty; // source model doesn't currently expose year; leave blank
    public string AlbumArtPath { get; }

    public ObservableCollection<SongInfoInstrumentRow> InstrumentRows { get; } = new();

    private bool _showSeason = true;
    public bool ShowSeason { get => _showSeason; set => Set(ref _showSeason, value); }
    private bool _showPercent = true;
    public bool ShowPercent { get => _showPercent; set => Set(ref _showPercent, value); }
    private bool _showScore = true;
    public bool ShowScore { get => _showScore; set => Set(ref _showScore, value); }

    public SongInfoViewModel(SongDisplayRow row, IList<string> instrumentOrder, IFestivalService service)
    {
        Title = row.Title;
        Artist = row.Artist;
        AlbumArtPath = row.AlbumArtPath;

        foreach (var key in instrumentOrder)
        {
            var status = row.InstrumentStatuses.FirstOrDefault(s => s.InstrumentKey == key);
            if (status == null) continue;
            int? score = null;
            int percentRaw = 0;
            int starsCount = 0;
            bool hasScore = false;
            bool isFC = status.IsFullCombo;
            if (service.ScoresIndex.TryGetValue(row.Song.track.su, out var ld))
            {
                var tr = key switch
                {
                    "guitar" => ld.guitar,
                    "drums" => ld.drums,
                    "vocals" => ld.vocals,
                    "bass" => ld.bass,
                    "pro_guitar" => ld.pro_guitar,
                    "pro_bass" => ld.pro_bass,
                    _ => null
                };
                if (tr is { initialized: true })
                {
                    score = tr.maxScore;
                    starsCount = tr.numStars;
                    percentRaw = tr.percentHit;
                    hasScore = true;
                    if (tr.isFullCombo) isFC = true;
                }
            }
            var instRow = new SongInfoInstrumentRow
            {
                Key = key,
                Name = KeyToDisplayName(key),
                Icon = status.Icon,
                StarsCount = starsCount,
                HasScore = hasScore,
                IsFullCombo = isFC,
                ScoreDisplay = hasScore && score.HasValue ? score.Value.ToString("N0") : "0",
                PercentDisplay = hasScore ? (isFC ? "100%" : FormatPercent(percentRaw)) : "0%",
                SeasonDisplay = hasScore ? (isFC ? row.SeasonDisplay : row.SeasonDisplay) : "N/A"
            };

            // Build star visuals per rules; if no score we show N/A text instead of a placeholder star
            if (hasScore)
            {
                int displayCount;
                bool allGold;
                if (starsCount >= 6)
                {
                    // Six stars: show 5 gold stars
                    displayCount = 5;
                    allGold = true;
                }
                else
                {
                    displayCount = Math.Max(1, starsCount); // ensure at least 1 if has score
                    allGold = false;
                }
                for (int i = 0; i < displayCount; i++)
                {
                    var star = new StarVisual
                    {
                        Image = allGold ? "star_gold.png" : "star_white.png",
                        OutlineColor = isFC ? "#FFD700" : "Transparent",
                        BackgroundColor = "Transparent" // remove glow
                    };
                    instRow.Stars.Add(star);
                }
            }

            InstrumentRows.Add(instRow);
        }
    }

    // Called by page when size changes to adapt column visibility.
    public void AdaptForWidth(double width)
    {
        // thresholds (can tweak): remove season below 980, percent below 900, score below 780
        bool showSeason = width >= 980;
        bool showPercent = width >= 900;
        bool showScore = width >= 780;
        if (ShowSeason != showSeason) ShowSeason = showSeason;
        if (ShowPercent != showPercent) ShowPercent = showPercent;
        if (ShowScore != showScore) ShowScore = showScore;
    }

    private static string FormatPercent(int raw)
    {
        Console.WriteLine("Raw value: " + raw);
        if (raw <= 0) return "0%";
        // Assume raw is scaled by 100 (e.g., 9990 => 99.90). If different scaling, adjust.
        // Try to preserve one or two decimals where meaningful.
        double value = raw / 10000.0; // raw already *100 => two decimals
        if (value % 1 == 0) return ((int)value).ToString() + "%";
        if (value * 10 % 1 == 0) return value.ToString("0.0") + "%";
        return value.ToString("0.00") + "%";
    }

    private static string KeyToDisplayName(string key) => key switch
    {
        "guitar" => "Lead",
        "drums" => "Drums",
        "vocals" => "Vocals",
        "bass" => "Bass",
        "pro_guitar" => "Pro Guitar",
        "pro_bass" => "Pro Bass",
        _ => key
    };
}
