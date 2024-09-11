using FortniteFestivalLeaderboardScraper.Helpers;
using FortniteFestivalLeaderboardScraper.Helpers.Excel;
using FortniteFestivalLeaderboardScraper.Helpers.FileIO;
using OfficeOpenXml.FormulaParsing.LexicalAnalysis;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace FortniteFestivalLeaderboardScraper
{
    public enum Instruments
    {
        Lead,
        Drums,
        Vocals,
        Bass,
        ProLead,
        ProBass
    }

    public enum OutputSelection
    {
        FullCombo,
        Title,
        Artist,
        Percentage,
        Score,
        Difficulty,
        Stars
    }

    public enum SortOrder
    {
        None,
        Title,
        Artist,
        Availability,
        AvailableLocally,
        Difficulty,
        LeadDiff,
        BassDiff,
        VocalsDiff,
        DrumsDiff,
        ProLeadDiff,
        ProBassDiff,
        Score,
        PercentageHit,
        SeasonAchieved,
        FullCombo,
        Stars
    }

    public partial class Form1 : Form
    {
        private List<Song> _sparkTracks = new List<Song>();
        private List<LeaderboardData> _previousData = new List<LeaderboardData>();
        private List<string> songIds = new List<string>();
        private List<string> supportedInstruments = new List<string>() { "Lead", "Vocals", "Bass", "Drums", "Pro Lead", "Pro Bass" };
        private int selectedColumn = -1;
        private int selectedSortColumn = -1;
        private OutputSelection selection = OutputSelection.FullCombo;
        private SortOrder sortOrder = SortOrder.None;
        private SortOrder scoreSortOrder = SortOrder.None;
        private Instruments scoreViewerInstrument = Instruments.Lead;
        private bool _invertOutput = false;
        private bool isSparkTracksReversed = false;
        private bool isPreviousDataReversed = false;
        private Settings _settings = new Settings();

        public Form1()
        {
            InitializeComponent();
        }

        private void button1_Click(object sender, EventArgs e)
        {
            ProcessStartInfo sInfo = new ProcessStartInfo("https://www.epicgames.com/id/api/redirect?clientId=ec684b8c687f479fadea3cb2ad83f5c6&responseType=code");
            Process.Start(sInfo);
        }

        private async void button2_Click(object sender, EventArgs e)
        {
            if (supportedInstruments.Count == 0)
            {
                textBox2.AppendText(Environment.NewLine + "At least one instrument must be selected in Options.");
                return;
            }

            tabControl1.TabPages.Remove(tabPage2);
            tabControl1.TabPages.Remove(tabPage4);
            tabControl1.TabPages.Remove(tabPage3);
            button1.Enabled = false;
            button2.Enabled = false;
            button5.Enabled = false;

            textBox2.Clear();
            textBox2.AppendText("Generating Fortnite bearer token...");
            var tokenGenerator = new EpicGamesExchangeTokenGenerator();
            var token = await tokenGenerator.GetTokenWithPermissions(textBox1.Text);
            if (token.Item1 == false || token.Item2.access_token == null)
            {
                textBox2.AppendText(Environment.NewLine + "An error occurred during authentication. Please try a new exchange code.");
                tabControl1.TabPages.Add(tabPage2);
                tabControl1.TabPages.Add(tabPage4);
                tabControl1.TabPages.Add(tabPage3);
                button1.Enabled = true;
                button2.Enabled = true;
                button5.Enabled = true;
                return;
            }
            if (_sparkTracks.Count != 0)
            {
                textBox2.AppendText(Environment.NewLine + "Retrieving list of songs...");
            }

            _previousData = JSONReadWrite.ReadLeaderboardJSON();
            var stRetriever = new SparkTrackRetriever();
            var seasonIdentifier = new MaxSeasonIdentifier();
            var sparkTracks = _sparkTracks.Count != 0 ? _sparkTracks : (await stRetriever.GetSparkTracks(_previousData));
            textBox2.AppendText(Environment.NewLine + "Attempting to find max season value...");
            var maxSeason = await seasonIdentifier.GetMaxSeason(token.Item2.access_token);
            _previousData = JSONReadWrite.ReadLeaderboardJSON();

            var scoreRetriever = new LeaderboardAPI();
            var scores = await scoreRetriever.GetLeaderboardsForInstrument(sparkTracks, token.Item2.access_token, token.Item2.account_id, maxSeason, _previousData, textBox2, songIds);
            if (scores.Item1 == false)
            {
                button1.Enabled = true;
                button2.Enabled = true;
                button5.Enabled = true;
                tabControl1.TabPages.Add(tabPage2);
                tabControl1.TabPages.Add(tabPage4);
                tabControl1.TabPages.Add(tabPage3);
                return;
            }
            JSONReadWrite.WriteLeaderboardJSON(scores.Item2);

            ExcelSpreadsheetGenerator.GenerateExcelSpreadsheet(scores.Item2, supportedInstruments, selection, _invertOutput);
            textBox2.AppendText(Environment.NewLine + "FortniteFestivalScores.xlsx written out to the directory your application is in.");
            button1.Enabled = true;
            button2.Enabled = true;
            button5.Enabled = true;
            tabControl1.TabPages.Add(tabPage2);
            tabControl1.TabPages.Add(tabPage4);
            tabControl1.TabPages.Add(tabPage3);
        }

        private async void onSongSelectFocused(object sender, EventArgs e)
        {
            if (((TabControl)sender).SelectedTab.Name != "tabPage2" || _sparkTracks.Count > 0)
            {
                if (((TabControl)sender).SelectedTab.Name != "tabPage2")
                {
                    onScoreViewFocused(sender, e);
                }
            }

            _previousData = JSONReadWrite.ReadLeaderboardJSON();
            var stRetriever = new SparkTrackRetriever();
            _sparkTracks = await stRetriever.GetSparkTracks(_previousData);
            var filteredTracks = _sparkTracks;

            if (textBox3.Text.Length != 0)
            {
                filteredTracks = _sparkTracks.Where(x => (x.track.tt.ToLowerInvariant().Contains(textBox3.Text.ToLowerInvariant()) || x.track.an.ToLowerInvariant().Contains(textBox3.Text.ToLowerInvariant()))).ToList();
            }
            switch (sortOrder)
            {
                case SortOrder.AvailableLocally:
                    filteredTracks = filteredTracks.OrderBy(x => _previousData.FindIndex(y => y.songId == x.track.su) >= 0).ThenBy(x => x.track.an).ThenBy(x => x.track.tt).ToList();
                    break;
                case SortOrder.Title:
                    filteredTracks = filteredTracks.OrderBy(x => x.track.tt).ToList();
                    break;
                case SortOrder.Artist:
                    filteredTracks = filteredTracks.OrderBy(x => x.track.an).ThenBy(x => x.track.tt).ToList();
                    break;
                case SortOrder.Availability:
                    filteredTracks = filteredTracks.OrderBy(x => x._activeDate).ThenBy(x => x.track.an).ThenBy(x => x.track.tt).ToList();
                    break;
                case SortOrder.LeadDiff:
                    filteredTracks = filteredTracks.OrderBy(x => x.track.@in.gr).ThenBy(x => x.track.an).ThenBy(x => x.track.tt).ToList();
                    break;
                case SortOrder.BassDiff:
                    filteredTracks = filteredTracks.OrderBy(x => x.track.@in.ba).ThenBy(x => x.track.an).ThenBy(x => x.track.tt).ToList();
                    break;
                case SortOrder.VocalsDiff:
                    filteredTracks = filteredTracks.OrderBy(x => x.track.@in.vl).ThenBy(x => x.track.an).ThenBy(x => x.track.tt).ToList();
                    break;
                case SortOrder.DrumsDiff:
                    filteredTracks = filteredTracks.OrderBy(x => x.track.@in.ds).ThenBy(x => x.track.an).ThenBy(x => x.track.tt).ToList();
                    break;
                case SortOrder.ProLeadDiff:
                    filteredTracks = filteredTracks.OrderBy(x => x.track.@in.pg).ThenBy(x => x.track.an).ThenBy(x => x.track.tt).ToList();
                    break;
                case SortOrder.ProBassDiff:
                    filteredTracks = filteredTracks.OrderBy(x => x.track.@in.pb).ThenBy(x => x.track.an).ThenBy(x => x.track.tt).ToList();
                    break;
                default:
                    break;
            }
            if (isSparkTracksReversed)
            {
                filteredTracks.Reverse();
            }

            this.dataGridView1.Rows.Clear();

            for (int i = 0; i < filteredTracks.Count; i++)
            {
                Song song = filteredTracks[i];
                this.dataGridView1.Rows.Add(song.isSelected, song.isInLocalData, song.track.tt, song.track.an, song._activeDate, song.track.@in.gr, song.track.@in.ba, song.track.@in.vl, song.track.@in.ds, song.track.@in.pg, song.track.@in.pb, song.track.su);
                this.dataGridView1.Rows[i].Cells[1].Style.ForeColor = _previousData.FindIndex(x => x.songId == song.track.su) >= 0 ? Color.Green : Color.Red;
                this.dataGridView1.Rows[i].Cells[1].Style.Alignment = DataGridViewContentAlignment.MiddleCenter;
            }

            this.label4.Visible = false;
            this.dataGridView1.Visible = true;
            this.dataGridView1.AutoResizeColumns(DataGridViewAutoSizeColumnsMode.AllCells);
            this.dataGridView1.Columns[0].MinimumWidth = 100;
        }

        private void onScoreViewFocused(object sender, EventArgs e)
        {
            if (((TabControl)sender).SelectedTab.Name != "tabPage4")
            {
                return;
            }

            _previousData = JSONReadWrite.ReadLeaderboardJSON();

            sortScoreViewData(_previousData);
        }

        private void sortScoreViewData(List<LeaderboardData> dataList)
        {
            this.dataGridView2.Rows.Clear();

            if (textBox4.Text.Length != 0)
            {
                dataList = dataList.Where(x => (x.title.ToLowerInvariant().Contains(textBox4.Text.ToLowerInvariant()) || x.artist.ToLowerInvariant().Contains(textBox4.Text.ToLowerInvariant()))).ToList();
            }

            switch (scoreSortOrder)
            {
                case SortOrder.Title:
                    dataList = dataList.OrderBy(x => x.title).ToList();
                    break;
                case SortOrder.Artist:
                    dataList = dataList.OrderBy(x => x.artist).ThenBy(x => x.title).ToList();
                    break;
                case SortOrder.Difficulty:
                    switch (scoreViewerInstrument)
                    {
                        case Instruments.Lead:
                            dataList = dataList.OrderBy(x => x.guitar.difficulty).ToList();
                            break;
                        case Instruments.Vocals:
                            dataList = dataList.OrderBy(x => x.vocals.difficulty).ToList();
                            break;
                        case Instruments.Bass:
                            dataList = dataList.OrderBy(x => x.bass.difficulty).ToList();
                            break;
                        case Instruments.Drums:
                            dataList = dataList.OrderBy(x => x.drums.difficulty).ToList();
                            break;
                        case Instruments.ProLead:
                            dataList = dataList.OrderBy(x => x.pro_guitar.difficulty).ToList();
                            break;
                        case Instruments.ProBass:
                            dataList = dataList.OrderBy(x => x.pro_bass.difficulty).ToList();
                            break;
                        default:
                            break;
                    }
                    break;
                case SortOrder.FullCombo:
                    switch (scoreViewerInstrument)
                    {
                        case Instruments.Lead:
                            dataList = dataList.OrderBy(x => x.guitar.isFullCombo).ToList();
                            break;
                        case Instruments.Vocals:
                            dataList = dataList.OrderBy(x => x.vocals.isFullCombo).ToList();
                            break;
                        case Instruments.Bass:
                            dataList = dataList.OrderBy(x => x.bass.isFullCombo).ToList();
                            break;
                        case Instruments.Drums:
                            dataList = dataList.OrderBy(x => x.drums.isFullCombo).ToList();
                            break;
                        case Instruments.ProLead:
                            dataList = dataList.OrderBy(x => x.pro_guitar.isFullCombo).ToList();
                            break;
                        case Instruments.ProBass:
                            dataList = dataList.OrderBy(x => x.pro_bass.isFullCombo).ToList();
                            break;
                        default:
                            break;
                    }
                    break;
                case SortOrder.Stars:
                    switch (scoreViewerInstrument)
                    {
                        case Instruments.Lead:
                            dataList = dataList.OrderBy(x => x.guitar.numStars).ToList();
                            break;
                        case Instruments.Vocals:
                            dataList = dataList.OrderBy(x => x.vocals.numStars).ToList();
                            break;
                        case Instruments.Bass:
                            dataList = dataList.OrderBy(x => x.bass.numStars).ToList();
                            break;
                        case Instruments.Drums:
                            dataList = dataList.OrderBy(x => x.drums.numStars).ToList();
                            break;
                        case Instruments.ProLead:
                            dataList = dataList.OrderBy(x => x.pro_guitar.numStars).ToList();
                            break;
                        case Instruments.ProBass:
                            dataList = dataList.OrderBy(x => x.pro_bass.numStars).ToList();
                            break;
                        default:
                            break;
                    }
                    break;
                case SortOrder.Score:
                    switch (scoreViewerInstrument)
                    {
                        case Instruments.Lead:
                            dataList = dataList.OrderBy(x => x.guitar.maxScore).ToList();
                            break;
                        case Instruments.Vocals:
                            dataList = dataList.OrderBy(x => x.vocals.maxScore).ToList();
                            break;
                        case Instruments.Bass:
                            dataList = dataList.OrderBy(x => x.bass.maxScore).ToList();
                            break;
                        case Instruments.Drums:
                            dataList = dataList.OrderBy(x => x.drums.maxScore).ToList();
                            break;
                        case Instruments.ProLead:
                            dataList = dataList.OrderBy(x => x.pro_guitar.maxScore).ToList();
                            break;
                        case Instruments.ProBass:
                            dataList = dataList.OrderBy(x => x.pro_bass.maxScore).ToList();
                            break;
                        default:
                            break;
                    }
                    break;
                case SortOrder.PercentageHit:
                    switch (scoreViewerInstrument)
                    {
                        case Instruments.Lead:
                            dataList = dataList.OrderBy(x => x.guitar.percentHit).ToList();
                            break;
                        case Instruments.Vocals:
                            dataList = dataList.OrderBy(x => x.vocals.percentHit).ToList();
                            break;
                        case Instruments.Bass:
                            dataList = dataList.OrderBy(x => x.bass.percentHit).ToList();
                            break;
                        case Instruments.Drums:
                            dataList = dataList.OrderBy(x => x.drums.percentHit).ToList();
                            break;
                        case Instruments.ProLead:
                            dataList = dataList.OrderBy(x => x.pro_guitar.percentHit).ToList();
                            break;
                        case Instruments.ProBass:
                            dataList = dataList.OrderBy(x => x.pro_bass.percentHit).ToList();
                            break;
                        default:
                            break;
                    }
                    break;
                case SortOrder.SeasonAchieved:
                    switch (scoreViewerInstrument)
                    {
                        case Instruments.Lead:
                            dataList = dataList.OrderBy(x => x.guitar.season).ToList();
                            break;
                        case Instruments.Vocals:
                            dataList = dataList.OrderBy(x => x.vocals.season).ToList();
                            break;
                        case Instruments.Bass:
                            dataList = dataList.OrderBy(x => x.bass.season).ToList();
                            break;
                        case Instruments.Drums:
                            dataList = dataList.OrderBy(x => x.drums.season).ToList();
                            break;
                        case Instruments.ProLead:
                            dataList = dataList.OrderBy(x => x.pro_guitar.season).ToList();
                            break;
                        case Instruments.ProBass:
                            dataList = dataList.OrderBy(x => x.pro_bass.season).ToList();
                            break;
                        default:
                            break;
                    }
                    break;
                default:
                    break;
            }

            if (isPreviousDataReversed)
            {
                dataList.Reverse();
            }

            for (int k = 0; k < dataList.Count; k++)
            {
                var song = dataList[k];
                var data = song.guitar;
                switch (scoreViewerInstrument)
                {
                    case Instruments.Lead:
                        data = song.guitar;
                        break;
                    case Instruments.Vocals:
                        data = song.vocals;
                        break;
                    case Instruments.Bass:
                        data = song.bass;
                        break;
                    case Instruments.Drums:
                        data = song.drums;
                        break;
                    case Instruments.ProLead:
                        data = song.pro_guitar;
                        break;
                    case Instruments.ProBass:
                        data = song.pro_bass;
                        break;
                    default:
                        break;
                }

                var starsString = "";
                if (data.numStars == 6)
                {
                    starsString = "⍟⍟⍟⍟⍟";
                }
                else
                {
                    for (int i = 0; i < data.numStars; i++)
                    {
                        starsString += "⍟";
                    }
                }

                if (starsString == "")
                {
                    starsString = "N/A";
                }

                this.dataGridView2.Rows.Add(song.title, song.artist, data.isFullCombo ? "✔" : "❌", starsString, data.maxScore, (data.percentHit / 10000) + "%", data.season, data.difficulty);

                if (data.numStars == 6)
                {
                    this.dataGridView2.Rows[k].Cells[3].Style.ForeColor = Color.Gold;
                }
                if (data.numStars != 0)
                {
                    this.dataGridView2.Rows[k].Cells[3].Style.Font = new Font("Verdana", 16, FontStyle.Bold);
                }

                this.dataGridView2.Rows[k].Cells[2].Style.ForeColor = data.isFullCombo ? Color.Green : Color.Red;
                this.dataGridView2.Rows[k].Cells[2].Style.Alignment = DataGridViewContentAlignment.MiddleCenter;
            }

            this.dataGridView2.Visible = true;
            this.dataGridView2.AutoResizeColumns(DataGridViewAutoSizeColumnsMode.AllCells);
        }

        private void onColumnHeaderMouseClick(object sender, DataGridViewCellMouseEventArgs e)
        {
            var filteredTracks = _sparkTracks;
            if (textBox3.Text.Length != 0)
            {
                filteredTracks = _sparkTracks.Where(x => (x.track.tt.ToLowerInvariant().Contains(textBox3.Text.ToLowerInvariant()) || x.track.an.ToLowerInvariant().Contains(textBox3.Text.ToLowerInvariant()))).ToList();
            }

            if (e.ColumnIndex == selectedColumn)
            {
                filteredTracks.Reverse();
                isSparkTracksReversed = !isSparkTracksReversed;
            }
                switch (e.ColumnIndex)
                {
                    case 1:
                        filteredTracks = filteredTracks.OrderBy(x => _previousData.FindIndex(y => y.songId == x.track.su) >= 0).ThenBy(x => x.track.an).ThenBy(x => x.track.tt).ToList();
                        sortOrder = SortOrder.AvailableLocally;
                        break;
                    case 2:
                        filteredTracks = filteredTracks.OrderBy(x => x.track.tt).ToList();
                        sortOrder = SortOrder.Title;
                        break;
                    case 3:
                        filteredTracks = filteredTracks.OrderBy(x => x.track.an).ThenBy(x => x.track.tt).ToList();
                        sortOrder = SortOrder.Artist;
                        break;
                    case 4:
                        filteredTracks = filteredTracks.OrderBy(x => x._activeDate).ThenBy(x => x.track.an).ThenBy(x => x.track.tt).ToList();
                        sortOrder = SortOrder.Availability;
                        break;
                    case 5:
                        filteredTracks = filteredTracks.OrderBy(x => x.track.@in.gr).ThenBy(x => x.track.an).ThenBy(x => x.track.tt).ToList();
                        sortOrder = SortOrder.LeadDiff;
                        break;
                    case 6:
                        filteredTracks = filteredTracks.OrderBy(x => x.track.@in.ba).ThenBy(x => x.track.an).ThenBy(x => x.track.tt).ToList();
                        sortOrder = SortOrder.BassDiff;
                        break;
                    case 7:
                        filteredTracks = filteredTracks.OrderBy(x => x.track.@in.vl).ThenBy(x => x.track.an).ThenBy(x => x.track.tt).ToList();
                        sortOrder = SortOrder.VocalsDiff;
                        break;
                    case 8:
                        filteredTracks = filteredTracks.OrderBy(x => x.track.@in.ds).ThenBy(x => x.track.an).ThenBy(x => x.track.tt).ToList();
                        sortOrder = SortOrder.DrumsDiff;
                        break;
                    case 9:
                        filteredTracks = filteredTracks.OrderBy(x => x.track.@in.pg).ThenBy(x => x.track.an).ThenBy(x => x.track.tt).ToList();
                        sortOrder = SortOrder.ProLeadDiff;
                        break;
                    case 10:
                        filteredTracks = filteredTracks.OrderBy(x => x.track.@in.pb).ThenBy(x => x.track.an).ThenBy(x => x.track.tt).ToList();
                        sortOrder = SortOrder.ProBassDiff;
                        break;
                    default:
                        break;
            }

            if (isSparkTracksReversed)
            {
                filteredTracks.Reverse();
            }
                

            selectedColumn = e.ColumnIndex;
            this.dataGridView1.Rows.Clear();
            for (int i = 0; i < filteredTracks.Count; i++)
            {
                Song song = filteredTracks[i];
                this.dataGridView1.Rows.Add(song.isSelected, song.isInLocalData, song.track.tt, song.track.an, song._activeDate, song.track.@in.gr, song.track.@in.ba, song.track.@in.vl, song.track.@in.ds, song.track.@in.pg, song.track.@in.pb, song.track.su);
                this.dataGridView1.Rows[i].Cells[1].Style.ForeColor = _previousData.FindIndex(x => x.songId == song.track.su) >= 0 ? Color.Green : Color.Red;
                this.dataGridView1.Rows[i].Cells[1].Style.Alignment = DataGridViewContentAlignment.MiddleCenter;
            }

            this.dataGridView1.AutoResizeColumns(DataGridViewAutoSizeColumnsMode.AllCells);
            this.dataGridView1.Columns[0].MinimumWidth = 100;
        }

        private void onScoreViewerColumnHeaderMouseClick(object sender, DataGridViewCellMouseEventArgs e)
        {
            if (e.ColumnIndex == selectedSortColumn)
            {
                isPreviousDataReversed = !isPreviousDataReversed;
            }
            else
            {
                isPreviousDataReversed = false;
            }
            switch (e.ColumnIndex)
            {
                case 0:
                    scoreSortOrder = SortOrder.Title;
                    break;
                case 1:
                    scoreSortOrder = SortOrder.Artist;
                    break;
                case 2:
                    scoreSortOrder = SortOrder.FullCombo;
                    break;
                case 3:
                    scoreSortOrder = SortOrder.Stars;
                    break;
                case 4:
                    scoreSortOrder = SortOrder.Score;
                    break;
                case 5:
                    scoreSortOrder = SortOrder.PercentageHit;
                    break;
                case 6:
                    scoreSortOrder = SortOrder.SeasonAchieved;
                    break;
                case 7:
                    scoreSortOrder = SortOrder.Difficulty;
                    break;
                default:
                    break;
            }


            selectedSortColumn = e.ColumnIndex;
            this.dataGridView2.Rows.Clear();

            sortScoreViewData(_previousData);
        }

        private void TextBox3_TextChanged(object sender, System.EventArgs e)
        {
            this.dataGridView1.Rows.Clear();
            var filteredTracks = _sparkTracks;
            if (textBox3.Text.Length != 0)
            {
                filteredTracks = _sparkTracks.Where(x => (x.track.tt.ToLowerInvariant().Contains(textBox3.Text.ToLowerInvariant()) || x.track.an.ToLowerInvariant().Contains(textBox3.Text.ToLowerInvariant()))).ToList();
            }

            switch (sortOrder)
            {
                case SortOrder.AvailableLocally:
                    filteredTracks = filteredTracks.OrderBy(x => _previousData.FindIndex(y => y.songId == x.track.su) >= 0).ThenBy(x => x.track.an).ThenBy(x => x.track.tt).ToList();
                    break;
                case SortOrder.Title:
                    filteredTracks = filteredTracks.OrderBy(x => x.track.tt).ToList();
                    break;
                case SortOrder.Artist:
                    filteredTracks = filteredTracks.OrderBy(x => x.track.an).ThenBy(x => x.track.tt).ToList();
                    break;
                case SortOrder.Availability:
                    filteredTracks = filteredTracks.OrderBy(x => x._activeDate).ThenBy(x => x.track.an).ThenBy(x => x.track.tt).ToList();
                    break;
                case SortOrder.LeadDiff:
                    filteredTracks = filteredTracks.OrderBy(x => x.track.@in.gr).ThenBy(x => x.track.an).ThenBy(x => x.track.tt).ToList();
                    break;
                case SortOrder.BassDiff:
                    filteredTracks = filteredTracks.OrderBy(x => x.track.@in.ba).ThenBy(x => x.track.an).ThenBy(x => x.track.tt).ToList();
                    break;
                case SortOrder.VocalsDiff:
                    filteredTracks = filteredTracks.OrderBy(x => x.track.@in.vl).ThenBy(x => x.track.an).ThenBy(x => x.track.tt).ToList();
                    break;
                case SortOrder.DrumsDiff:
                    filteredTracks = filteredTracks.OrderBy(x => x.track.@in.ds).ThenBy(x => x.track.an).ThenBy(x => x.track.tt).ToList();
                    break;
                case SortOrder.ProLeadDiff:
                    filteredTracks = filteredTracks.OrderBy(x => x.track.@in.pg).ThenBy(x => x.track.an).ThenBy(x => x.track.tt).ToList();
                    break;
                case SortOrder.ProBassDiff:
                    filteredTracks = filteredTracks.OrderBy(x => x.track.@in.pb).ThenBy(x => x.track.an).ThenBy(x => x.track.tt).ToList();
                    break;
                default:
                    break;
            }

            if (isSparkTracksReversed)
            {
                filteredTracks.Reverse();
            }

            for (int i = 0; i < filteredTracks.Count; i++)
            {
                Song song = filteredTracks[i];
                this.dataGridView1.Rows.Add(song.isSelected, song.isInLocalData, song.track.tt, song.track.an, song._activeDate, song.track.@in.gr, song.track.@in.ba, song.track.@in.vl, song.track.@in.ds, song.track.@in.pg, song.track.@in.pb, song.track.su);
                this.dataGridView1.Rows[i].Cells[1].Style.ForeColor = _previousData.FindIndex(x => x.songId == song.track.su) >= 0 ? Color.Green : Color.Red;
                this.dataGridView1.Rows[i].Cells[1].Style.Alignment = DataGridViewContentAlignment.MiddleCenter;
            }

            this.dataGridView1.AutoResizeColumns(DataGridViewAutoSizeColumnsMode.AllCells);
            this.dataGridView1.Columns[0].MinimumWidth = 100;
        }

        private void TextBox4_TextChanged(object sender, System.EventArgs e)
        {
            this.dataGridView2.Rows.Clear();

            sortScoreViewData(_previousData);
        }

        private void DataGridView1_CellContentClick(object sender, System.Windows.Forms.DataGridViewCellEventArgs e)
        {
            if (e.ColumnIndex != 0 || e.RowIndex == -1) return;

            string id = this.dataGridView1.Rows[e.RowIndex].Cells[11].Value.ToString();
            if (songIds.Contains(id))
            {
                songIds.Remove(id);
            }
            else
            {
                songIds.Add(id);
            }

            int indexOfSong = _sparkTracks.FindIndex(x => x.track.su == id);
            _sparkTracks[indexOfSong].isSelected = !_sparkTracks[indexOfSong].isSelected;
        }

        private void button3_Click(object sender, EventArgs e)
        {
            for (int i = 0; i < this.dataGridView1.Rows.Count; i++)
            {
                dataGridView1.Rows[i].Cells[0].Value = true;
                var id = dataGridView1.Rows[i].Cells[11].Value.ToString();
                if (!songIds.Contains(id))
                {
                    songIds.Add(id);
                }
            }

            for (int i = 0; i < _sparkTracks.Count; i++)
            {
                _sparkTracks[i].isSelected = true;
            }
        }

        private void button4_Click(object sender, EventArgs e)
        {
            for (int i = 0; i < this.dataGridView1.Rows.Count; i++)
            {
                dataGridView1.Rows[i].Cells[0].Value = false;
                var id = dataGridView1.Rows[i].Cells[11].Value.ToString();
                if (songIds.Contains(id))
                {
                    songIds.Remove(id);
                }
            }

            for (int i = 0; i < _sparkTracks.Count; i++)
            {
                _sparkTracks[i].isSelected = false;
            }
        }

        private void onInstrumentOutputSelected(object sender, EventArgs e)
        {
            CheckBox element = (CheckBox)sender;
            if (element.Checked && !supportedInstruments.Contains(element.Text))
            {
                supportedInstruments.Add(element.Text);
            }
            else
            {
                supportedInstruments.Remove(element.Text);
            }
        }

        private void onOutputFormatSelection(object sender, EventArgs e)
        {
            RadioButton element = (RadioButton)sender;
            if (element.Checked)
            {
                switch (element.Text)
                {
                    case "Full Combo":
                        selection = OutputSelection.FullCombo;
                        break;
                    case "Title":
                        selection = OutputSelection.Title;
                        break;
                    case "Artist":
                        selection = OutputSelection.Artist;
                        break;
                    case "Percentage":
                        selection = OutputSelection.Percentage;
                        break;
                    case "Score":
                        selection = OutputSelection.Score;
                        break;
                    case "Difficulty":
                        selection = OutputSelection.Difficulty;
                        break;
                    case "Stars":
                        selection = OutputSelection.Stars;
                        break;
                    default:
                        selection = OutputSelection.FullCombo;
                        break;
                }
            }
        }

        private void onInvertOutputSelected(object sender, EventArgs eventArgs)
        {
            CheckBox element = (CheckBox)sender;
            _invertOutput = element.Checked;
        }

        private void button5_Click(object sender, EventArgs e)
        {
            tabControl1.TabPages.Remove(tabPage2);
            tabControl1.TabPages.Remove(tabPage4);
            tabControl1.TabPages.Remove(tabPage3);
            button1.Enabled = false;
            button2.Enabled = false;
            button5.Enabled = false;

            textBox2.Clear();
            textBox2.AppendText("Regenerating your output from existing cached data");
            var previousData = JSONReadWrite.ReadLeaderboardJSON();

            if (previousData.Count == 0)
            {
                textBox2.AppendText(Environment.NewLine + "Cached data does not exist, contains no content, or encountered an error while loading. Please regenerate your output by querying your scores again.");

                tabControl1.TabPages.Add(tabPage2);
                tabControl1.TabPages.Add(tabPage4);
                tabControl1.TabPages.Add(tabPage3);
                button1.Enabled = true;
                button2.Enabled = true;
                button5.Enabled = true;
                return;
            }

            ExcelSpreadsheetGenerator.GenerateExcelSpreadsheet(previousData, supportedInstruments, selection, _invertOutput);
            tabControl1.TabPages.Add(tabPage2);
            tabControl1.TabPages.Add(tabPage4);
            tabControl1.TabPages.Add(tabPage3);
            button1.Enabled = true;
            button2.Enabled = true;
            button5.Enabled = true;
            textBox2.AppendText(Environment.NewLine + "FortniteFestivalScores.xlsx written out to the directory your application is in.");
        }

        protected void OnMainWindowClosing(object sender, EventArgs e)
        {
            _settings.writeLead = this.leadCheck.Checked;
            _settings.writeBass = this.bassCheck.Checked;
            _settings.writeVocals = this.vocalsCheck.Checked;
            _settings.writeDrums = this.drumsCheck.Checked;
            _settings.writeProLead = this.proLeadCheck.Checked;
            _settings.writeProBass = this.proBassCheck.Checked;
            _settings.invertOutput = this.invertOutput.Checked;
            _settings.outputSelection = this.selection;

            JSONReadWrite.WriteSettings(_settings);
        }

        protected void OnMainWindowLoad(object sender, EventArgs e)
        {
            _settings = JSONReadWrite.ReadSettings();

            this.leadCheck.Checked = _settings.writeLead;
            this.bassCheck.Checked = _settings.writeBass;
            this.vocalsCheck.Checked = _settings.writeVocals;
            this.drumsCheck.Checked = _settings.writeDrums;
            this.proBassCheck.Checked = _settings.writeProBass;
            this.proLeadCheck.Checked = _settings.writeProLead;
            this.invertOutput.Checked = _settings.invertOutput;
            this.selection = _settings.outputSelection;

            switch (this.selection)
            {
                case OutputSelection.FullCombo:
                    this.fullCombo.Checked = true;
                    break;
                case OutputSelection.Score:
                    this.score.Checked = true;
                    break;
                case OutputSelection.Percentage:
                    this.percentage.Checked = true;
                    break;
                case OutputSelection.Artist:
                    this.artist.Checked = true;
                    break;
                case OutputSelection.Title:
                    this.title.Checked = true;
                    break;
                case OutputSelection.Stars:
                    this.stars.Checked = true;
                    break;
                case OutputSelection.Difficulty:
                    this.difficulty.Checked = true;
                    break;
                default:
                    this.fullCombo.Checked = true;
                    break;
            }
        }

        private void onInstrumentScoreChanged(object sender, EventArgs e)
        {
            RadioButton element = sender as RadioButton;
            if (!element.Checked)
            {
                return;
            }

            switch (element.Name) 
            {
                case "radioButton1":
                    scoreViewerInstrument = Instruments.Lead;
                    break;
                case "radioButton2":
                    scoreViewerInstrument = Instruments.Drums;
                    break;
                case "radioButton3":
                    scoreViewerInstrument = Instruments.Vocals;
                    break;
                case "radioButton4":
                    scoreViewerInstrument = Instruments.Bass;
                    break;
                case "radioButton5":
                    scoreViewerInstrument = Instruments.ProLead;
                    break;
                case "radioButton6":
                    scoreViewerInstrument = Instruments.ProBass;
                    break;
                default:
                    break;
            }

            sortScoreViewData(_previousData);
        }
    }
}
