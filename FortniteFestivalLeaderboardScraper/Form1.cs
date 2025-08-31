using FortniteFestivalLeaderboardScraper.Helpers;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using FortniteFestivalLeaderboardScraper.Helpers.Data;
using Newtonsoft.Json;

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
        SeasonAchieved, // retained for UI compatibility but no longer used
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

        private string SettingsPath => Path.Combine(Path.GetDirectoryName(Assembly.GetEntryAssembly().Location), "FNFLS_settings.json");

        public Form1()
        {
            InitializeComponent();
            SqlRepository.Initialize(); // init DB
        }

        private void button1_Click(object sender, EventArgs e)
        {
            ProcessStartInfo sInfo = new ProcessStartInfo("https://www.epicgames.com/id/api/redirect?clientId=ec684b8c687f479fadea3cb2ad83f5c6&responseType=code");
            Process.Start(sInfo);
        }

        private async void button2_Click(object sender, EventArgs e)
        {
            if (supportedInstruments.Count == 0)
            { textBox2.AppendText(Environment.NewLine + "At least one instrument must be selected in Options."); return; }
            tabControl1.TabPages.Remove(tabPage2); tabControl1.TabPages.Remove(tabPage4); tabControl1.TabPages.Remove(tabPage3);
            button1.Enabled = button2.Enabled = false;
            textBox2.Clear(); textBox2.AppendText("Generating Fortnite bearer token...");
            var tokenGenerator = new EpicGamesExchangeTokenGenerator();
            var token = await tokenGenerator.GetTokenWithPermissions(textBox1.Text);
            if (!token.Item1 || token.Item2.access_token == null)
            { textBox2.AppendText(Environment.NewLine + "An error occurred during authentication. Please try a new exchange code."); RestoreTabs(); return; }
            if (_sparkTracks.Count != 0) textBox2.AppendText(Environment.NewLine + "Retrieving list of songs...");
            _previousData = LoadAllScores();
            var stRetriever = new SparkTrackRetriever();
            var sparkTracks = _sparkTracks.Count != 0 ? _sparkTracks : (await stRetriever.GetSparkTracks(_previousData));
            var scoreRetriever = new LeaderboardAPI();
            textBox2.AppendText(Environment.NewLine + "Fetching all-time leaderboards in parallel...");
            Action<LeaderboardData> perSong = ld => { try { SqlRepository.Upsert(ld); } catch { } try { if (InvokeRequired) BeginInvoke(new Action(() => UpdateLocalDataFromDbAndMaybeRefresh(ld.songId))); else UpdateLocalDataFromDbAndMaybeRefresh(ld.songId); } catch { } };
            var parallelScores = await scoreRetriever.GetLeaderboardsParallel(sparkTracks, token.Item2.access_token, token.Item2.account_id, 0, _previousData, textBox2, songIds, degreeOfParallelism: 16, perSongCompleted: perSong);
            if (!parallelScores.Item1)
            { textBox2.AppendText(Environment.NewLine + "Parallel fetch failed (likely unauthorized). Aborting."); RestoreTabs(); return; }
            PersistScores(parallelScores.Item2);
            textBox2.AppendText(Environment.NewLine + "(Excel export removed) Fetch complete.");
            RestoreTabs();
        }

        private void RestoreTabs()
        { tabControl1.TabPages.Add(tabPage2); tabControl1.TabPages.Add(tabPage4); tabControl1.TabPages.Add(tabPage3); button1.Enabled = button2.Enabled = true; }

        private void UpdateLocalDataFromDbAndMaybeRefresh(string songId)
        {
            _previousData = LoadAllScores();
            foreach (var s in _sparkTracks) s.isInLocalData = _previousData.Exists(x => x.songId == s.track.su) ? "✔" : "❌";
            if (tabControl1.SelectedTab == tabPage4) sortScoreViewData(_previousData);
            if (tabControl1.SelectedTab == tabPage2) onSongSelectFocused(tabControl1, EventArgs.Empty);
        }

        private async void onSongSelectFocused(object sender, EventArgs e)
        {
            if (((TabControl)sender).SelectedTab.Name != "tabPage2" || _sparkTracks.Count > 0)
            { if (((TabControl)sender).SelectedTab.Name != "tabPage2") onScoreViewFocused(sender, e); }
            _previousData = LoadAllScores();
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

            _previousData = LoadAllScores();
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
                    case Instruments.Lead: data = song.guitar; break;
                    case Instruments.Vocals: data = song.vocals; break;
                    case Instruments.Bass: data = song.bass; break;
                    case Instruments.Drums: data = song.drums; break;
                    case Instruments.ProLead: data = song.pro_guitar; break;
                    case Instruments.ProBass: data = song.pro_bass; break;
                }

                var starsString = "";
                if (data.numStars == 6)
                    starsString = "⍟⍟⍟⍟⍟";
                else
                    for (int i = 0; i < data.numStars; i++) starsString += "⍟";
                if (starsString == "") starsString = "N/A";

                this.dataGridView2.Rows.Add(song.title, song.artist, data.isFullCombo ? "✔" : "❌", starsString, data.maxScore, (data.percentHit / 10000) + "%", (data.seasonAchieved > 0 ? data.seasonAchieved.ToString() : "All-Time"), data.difficulty);

                if (data.numStars == 6) this.dataGridView2.Rows[k].Cells[3].Style.ForeColor = Color.Gold;
                if (data.numStars != 0) this.dataGridView2.Rows[k].Cells[3].Style.Font = new Font("Verdana", 16, FontStyle.Bold);
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
                    sortOrder = SortOrder.AvailableLocally; break;
                case 2:
                    filteredTracks = filteredTracks.OrderBy(x => x.track.tt).ToList(); sortOrder = SortOrder.Title; break;
                case 3:
                    filteredTracks = filteredTracks.OrderBy(x => x.track.an).ThenBy(x => x.track.tt).ToList(); sortOrder = SortOrder.Artist; break;
                case 4:
                    filteredTracks = filteredTracks.OrderBy(x => x._activeDate).ThenBy(x => x.track.an).ThenBy(x => x.track.tt).ToList(); sortOrder = SortOrder.Availability; break;
                case 5:
                    filteredTracks = filteredTracks.OrderBy(x => x.track.@in.gr).ThenBy(x => x.track.an).ThenBy(x => x.track.tt).ToList(); sortOrder = SortOrder.LeadDiff; break;
                case 6:
                    filteredTracks = filteredTracks.OrderBy(x => x.track.@in.ba).ThenBy(x => x.track.an).ThenBy(x => x.track.tt).ToList(); sortOrder = SortOrder.BassDiff; break;
                case 7:
                    filteredTracks = filteredTracks.OrderBy(x => x.track.@in.vl).ThenBy(x => x.track.an).ThenBy(x => x.track.tt).ToList(); sortOrder = SortOrder.VocalsDiff; break;
                case 8:
                    filteredTracks = filteredTracks.OrderBy(x => x.track.@in.ds).ThenBy(x => x.track.an).ThenBy(x => x.track.tt).ToList(); sortOrder = SortOrder.DrumsDiff; break;
                case 9:
                    filteredTracks = filteredTracks.OrderBy(x => x.track.@in.pg).ThenBy(x => x.track.an).ThenBy(x => x.track.tt).ToList(); sortOrder = SortOrder.ProLeadDiff; break;
                case 10:
                    filteredTracks = filteredTracks.OrderBy(x => x.track.@in.pb).ThenBy(x => x.track.an).ThenBy(x => x.track.tt).ToList(); sortOrder = SortOrder.ProBassDiff; break;
            }
            if (isSparkTracksReversed) filteredTracks.Reverse();

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
            if (e.ColumnIndex == selectedSortColumn) isPreviousDataReversed = !isPreviousDataReversed; else isPreviousDataReversed = false;
            switch (e.ColumnIndex)
            {
                case 0: scoreSortOrder = SortOrder.Title; break;
                case 1: scoreSortOrder = SortOrder.Artist; break;
                case 2: scoreSortOrder = SortOrder.FullCombo; break;
                case 3: scoreSortOrder = SortOrder.Stars; break;
                case 4: scoreSortOrder = SortOrder.Score; break;
                case 5: scoreSortOrder = SortOrder.PercentageHit; break;
                case 6: scoreSortOrder = SortOrder.SeasonAchieved; break;
                case 7: scoreSortOrder = SortOrder.Difficulty; break;
            }
            selectedSortColumn = e.ColumnIndex;
            this.dataGridView2.Rows.Clear();
            sortScoreViewData(_previousData);
        }

        private void TextBox3_TextChanged(object sender, EventArgs e)
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
                    filteredTracks = filteredTracks.OrderBy(x => _previousData.FindIndex(y => y.songId == x.track.su) >= 0).ThenBy(x => x.track.an).ThenBy(x => x.track.tt).ToList(); break;
                case SortOrder.Title: filteredTracks = filteredTracks.OrderBy(x => x.track.tt).ToList(); break;
                case SortOrder.Artist: filteredTracks = filteredTracks.OrderBy(x => x.track.an).ThenBy(x => x.track.tt).ToList(); break;
                case SortOrder.Availability: filteredTracks = filteredTracks.OrderBy(x => x._activeDate).ThenBy(x => x.track.an).ThenBy(x => x.track.tt).ToList(); break;
                case SortOrder.LeadDiff: filteredTracks = filteredTracks.OrderBy(x => x.track.@in.gr).ThenBy(x => x.track.an).ThenBy(x => x.track.tt).ToList(); break;
                case SortOrder.BassDiff: filteredTracks = filteredTracks.OrderBy(x => x.track.@in.ba).ThenBy(x => x.track.an).ThenBy(x => x.track.tt).ToList(); break;
                case SortOrder.VocalsDiff: filteredTracks = filteredTracks.OrderBy(x => x.track.@in.vl).ThenBy(x => x.track.an).ThenBy(x => x.track.tt).ToList(); break;
                case SortOrder.DrumsDiff: filteredTracks = filteredTracks.OrderBy(x => x.track.@in.ds).ThenBy(x => x.track.an).ThenBy(x => x.track.tt).ToList(); break;
                case SortOrder.ProLeadDiff: filteredTracks = filteredTracks.OrderBy(x => x.track.@in.pg).ThenBy(x => x.track.an).ThenBy(x => x.track.tt).ToList(); break;
                case SortOrder.ProBassDiff: filteredTracks = filteredTracks.OrderBy(x => x.track.@in.pb).ThenBy(x => x.track.an).ThenBy(x => x.track.tt).ToList(); break;
            }
            if (isSparkTracksReversed) filteredTracks.Reverse();
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

        private void TextBox4_TextChanged(object sender, EventArgs e)
        {
            this.dataGridView2.Rows.Clear();
            sortScoreViewData(_previousData);
        }

        private void DataGridView1_CellContentClick(object sender, DataGridViewCellEventArgs e)
        {
            if (e.ColumnIndex != 0 || e.RowIndex == -1) return;
            string id = this.dataGridView1.Rows[e.RowIndex].Cells[11].Value.ToString();
            if (songIds.Contains(id)) songIds.Remove(id); else songIds.Add(id);
            int indexOfSong = _sparkTracks.FindIndex(x => x.track.su == id);
            _sparkTracks[indexOfSong].isSelected = !_sparkTracks[indexOfSong].isSelected;
        }

        private void button3_Click(object sender, EventArgs e)
        {
            for (int i = 0; i < this.dataGridView1.Rows.Count; i++)
            {
                dataGridView1.Rows[i].Cells[0].Value = true;
                var id = dataGridView1.Rows[i].Cells[11].Value.ToString();
                if (!songIds.Contains(id)) songIds.Add(id);
            }
            for (int i = 0; i < _sparkTracks.Count; i++) _sparkTracks[i].isSelected = true;
        }

        private void button4_Click(object sender, EventArgs e)
        {
            for (int i = 0; i < this.dataGridView1.Rows.Count; i++)
            {
                dataGridView1.Rows[i].Cells[0].Value = false;
                var id = dataGridView1.Rows[i].Cells[11].Value.ToString();
                if (songIds.Contains(id)) songIds.Remove(id);
            }
            for (int i = 0; i < _sparkTracks.Count; i++) _sparkTracks[i].isSelected = false;
        }

        private void onInstrumentOutputSelected(object sender, EventArgs e)
        {
            CheckBox element = (CheckBox)sender;
            if (element.Checked && !supportedInstruments.Contains(element.Text)) supportedInstruments.Add(element.Text); else supportedInstruments.Remove(element.Text);
        }

        private void onOutputFormatSelection(object sender, EventArgs e)
        {
            RadioButton element = (RadioButton)sender;
            if (element.Checked)
            {
                switch (element.Text)
                {
                    case "Full Combo": selection = OutputSelection.FullCombo; break;
                    case "Title": selection = OutputSelection.Title; break;
                    case "Artist": selection = OutputSelection.Artist; break;
                    case "Percentage": selection = OutputSelection.Percentage; break;
                    case "Score": selection = OutputSelection.Score; break;
                    case "Difficulty": selection = OutputSelection.Difficulty; break;
                    case "Stars": selection = OutputSelection.Stars; break;
                    default: selection = OutputSelection.FullCombo; break;
                }
            }
        }

        private void onInvertOutputSelected(object sender, EventArgs e)
        {
            CheckBox element = (CheckBox)sender;
            _invertOutput = element.Checked;
        }

        protected void OnMainWindowClosing(object sender, EventArgs e) => SaveSettings();

        protected void OnMainWindowLoad(object sender, EventArgs e)
        {
            var s = LoadSettings();
            this.leadCheck.Checked = s.writeLead; this.bassCheck.Checked = s.writeBass; this.vocalsCheck.Checked = s.writeVocals; this.drumsCheck.Checked = s.writeDrums; this.proBassCheck.Checked = s.writeProBass; this.proLeadCheck.Checked = s.writeProLead; this.invertOutput.Checked = s.invertOutput; this.selection = s.outputSelection;
            switch (selection)
            { case OutputSelection.FullCombo: fullCombo.Checked = true; break; case OutputSelection.Score: score.Checked = true; break; case OutputSelection.Percentage: percentage.Checked = true; break; case OutputSelection.Artist: artist.Checked = true; break; case OutputSelection.Title: title.Checked = true; break; case OutputSelection.Stars: stars.Checked = true; break; case OutputSelection.Difficulty: difficulty.Checked = true; break; default: fullCombo.Checked = true; break; }
            _previousData = LoadAllScores();
        }

        private void SaveSettings()
        {
            try
            {
                var s = new Settings
                {
                    writeLead = this.leadCheck.Checked,
                    writeBass = this.bassCheck.Checked,
                    writeVocals = this.vocalsCheck.Checked,
                    writeDrums = this.drumsCheck.Checked,
                    writeProLead = this.proLeadCheck.Checked,
                    writeProBass = this.proBassCheck.Checked,
                    invertOutput = this.invertOutput.Checked,
                    outputSelection = this.selection
                };
                File.WriteAllText(SettingsPath, JsonConvert.SerializeObject(s, Formatting.Indented));
            }
            catch { }
        }

        private Settings LoadSettings()
        {
            try
            {
                if (!File.Exists(SettingsPath)) return new Settings();
                return JsonConvert.DeserializeObject<Settings>(File.ReadAllText(SettingsPath)) ?? new Settings();
            }
            catch { return new Settings(); }
        }

        // Replace JSONReadWrite.ReadLeaderboardJSON
        private List<LeaderboardData> LoadAllScores() => SqlRepository.LoadAll();

        // Replace JSONReadWrite.WriteLeaderboardJSON
        private void PersistScores(List<LeaderboardData> list) => SqlRepository.BulkUpsert(list);

        // Added: handle instrument radio button changes in score viewer
        private void onInstrumentScoreChanged(object sender, EventArgs e)
        {
            var rb = sender as RadioButton;
            if (rb == null || !rb.Checked) return;
            switch (rb.Text)
            {
                case "Lead": scoreViewerInstrument = Instruments.Lead; break;
                case "Vocals": scoreViewerInstrument = Instruments.Vocals; break;
                case "Bass": scoreViewerInstrument = Instruments.Bass; break;
                case "Drums": scoreViewerInstrument = Instruments.Drums; break;
                case "Pro Lead": scoreViewerInstrument = Instruments.ProLead; break;
                case "Pro Bass": scoreViewerInstrument = Instruments.ProBass; break;
                default: scoreViewerInstrument = Instruments.Lead; break;
            }
            this.dataGridView2.Rows.Clear();
            sortScoreViewData(_previousData);
        }
    }
}
