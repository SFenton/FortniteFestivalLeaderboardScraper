namespace FortniteFestival.LeaderboardScraper.MAUI.ViewModels;

public class ScoreRowModel
{
    public string Title { get; set; } = string.Empty;
    public string Artist { get; set; } = string.Empty;
    public int Score { get; set; }
    public string Percent { get; set; } = string.Empty;
    public string StarText { get; set; } = string.Empty;
    public bool MaxStars { get; set; }
    public string FullComboSymbol { get; set; } = string.Empty;
    public bool IsFullCombo { get; set; }
    public string Season { get; set; } = string.Empty;
    public string SongId { get; set; } = string.Empty;
}
