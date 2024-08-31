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
        LeadDiff,
        BassDiff,
        VocalsDiff,
        DrumsDiff,
        ProLeadDiff,
        ProBassDiff
    }

    public partial class Form1 : Form
    {
        private List<Song> _sparkTracks = new List<Song>();
        private List<string> songIds = new List<string>();
        private List<string> supportedInstruments = new List<string>() { "Lead", "Vocals", "Bass", "Drums", "Pro Lead", "Pro Bass" };
        private int selectedColumn = -1;
        private OutputSelection selection = OutputSelection.FullCombo;
        private SortOrder sortOrder = SortOrder.None;
        private bool _invertOutput = false;
        private bool isSparkTracksReversed = false;

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
            tabControl1.TabPages.Remove(tabPage3);
            button1.Enabled = false;
            button2.Enabled = false;
            button5.Enabled = false;

            textBox2.Clear();
            textBox2.AppendText("Generating Fortnite bearer token...");
            var token = await EpicGamesExchangeTokenGenerator.GetTokenWithPermissions(textBox1.Text);
            if (token.Item1 == false || token.Item2.access_token == null)
            {
                textBox2.AppendText(Environment.NewLine + "An error occurred during authentication. Please try a new exchange code.");
                button1.Enabled = true;
                button2.Enabled = true;
                button5.Enabled = true;
                return;
            }
            if (_sparkTracks.Count != 0)
            {
                textBox2.AppendText(Environment.NewLine + "Retrieving list of songs...");
            }
            var sparkTracks = _sparkTracks.Count != 0 ? _sparkTracks : (await SparkTrackRetriever.GetSparkTracks());
            textBox2.AppendText(Environment.NewLine + "Attempting to find max season value...");
            var maxSeason = await MaxSeasonIdentifier.GetMaxSeason(token.Item2.access_token);
            var previousData = JSONReadWrite.ReadLeaderboardJSON();
            var scores = await LeaderboardAPI.GetLeaderboardsForInstrument(sparkTracks, token.Item2.access_token, token.Item2.account_id, maxSeason, previousData, textBox2, songIds, supportedInstruments);
            if (scores.Item1 == false)
            {
                button1.Enabled = true;
                button2.Enabled = true;
                button5.Enabled = true;
                return;
            }
            JSONReadWrite.WriteLeaderboardJSON(scores.Item2);

            ExcelSpreadsheetGenerator.GenerateExcelSpreadsheet(scores.Item2, supportedInstruments, selection, _invertOutput);
            textBox2.AppendText(Environment.NewLine + "FortniteFestivalScores.xlsx written out to the directory your application is in.");
            button1.Enabled = true;
            button2.Enabled = true;
            button5.Enabled = true;
            tabControl1.TabPages.Add(tabPage2);
            tabControl1.TabPages.Add(tabPage3);
        }

        private async void onSongSelectFocused(object sender, EventArgs e)
        {
            if (((TabControl)sender).SelectedTab.Name != "tabPage2" || _sparkTracks.Count > 0)
            {
                return;
            }

            _sparkTracks = await SparkTrackRetriever.GetSparkTracks();
            this.dataGridView1.Rows.Clear();

            foreach (Song song in _sparkTracks)
            {
                this.dataGridView1.Rows.Add(song.isSelected, song.track.tt, song.track.an, song._activeDate, song.track.@in.gr, song.track.@in.ba, song.track.@in.vl, song.track.@in.ds, song.track.@in.pg, song.track.@in.pb, song.track.su);
            }

            this.label4.Visible = false;
            this.dataGridView1.Visible = true;
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
                        filteredTracks = filteredTracks.OrderBy(x => x.track.tt).ToList();
                        sortOrder = SortOrder.Title;
                        break;
                    case 2:
                        filteredTracks = filteredTracks.OrderBy(x => x.track.an).ThenBy(x => x.track.tt).ToList();
                        sortOrder = SortOrder.Artist;
                        break;
                    case 3:
                        filteredTracks = filteredTracks.OrderBy(x => x._activeDate).ThenBy(x => x.track.an).ThenBy(x => x.track.tt).ToList();
                        sortOrder = SortOrder.Availability;
                        break;
                    case 4:
                        filteredTracks = filteredTracks.OrderBy(x => x.track.@in.gr).ThenBy(x => x.track.an).ThenBy(x => x.track.tt).ToList();
                        sortOrder = SortOrder.LeadDiff;
                        break;
                    case 5:
                        filteredTracks = filteredTracks.OrderBy(x => x.track.@in.ba).ThenBy(x => x.track.an).ThenBy(x => x.track.tt).ToList();
                        sortOrder = SortOrder.BassDiff;
                        break;
                    case 6:
                        filteredTracks = filteredTracks.OrderBy(x => x.track.@in.vl).ThenBy(x => x.track.an).ThenBy(x => x.track.tt).ToList();
                        sortOrder = SortOrder.VocalsDiff;
                        break;
                    case 7:
                        filteredTracks = filteredTracks.OrderBy(x => x.track.@in.ds).ThenBy(x => x.track.an).ThenBy(x => x.track.tt).ToList();
                        sortOrder = SortOrder.DrumsDiff;
                        break;
                    case 8:
                        filteredTracks = filteredTracks.OrderBy(x => x.track.@in.pg).ThenBy(x => x.track.an).ThenBy(x => x.track.tt).ToList();
                        sortOrder = SortOrder.ProLeadDiff;
                        break;
                    case 9:
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
            foreach (Song song in filteredTracks)
            {
                this.dataGridView1.Rows.Add(song.isSelected, song.track.tt, song.track.an, song._activeDate, song.track.@in.gr, song.track.@in.ba, song.track.@in.vl, song.track.@in.ds, song.track.@in.pg, song.track.@in.pb, song.track.su);
            }
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

            foreach (Song song in filteredTracks)
            {
                this.dataGridView1.Rows.Add(song.isSelected, song.track.tt, song.track.an, song._activeDate, song.track.@in.gr, song.track.@in.ba, song.track.@in.vl, song.track.@in.ds, song.track.@in.pg, song.track.@in.pb, song.track.su);
            }
        }

        private void DataGridView1_CellContentClick(object sender, System.Windows.Forms.DataGridViewCellEventArgs e)
        {
            if (e.ColumnIndex != 0 || e.RowIndex == -1) return;

            string id = this.dataGridView1.Rows[e.RowIndex].Cells[10].Value.ToString();
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
                var id = dataGridView1.Rows[i].Cells[10].Value.ToString();
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
                var id = dataGridView1.Rows[i].Cells[10].Value.ToString();
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
                tabControl1.TabPages.Add(tabPage3);
                button1.Enabled = true;
                button2.Enabled = true;
                button5.Enabled = true;
                return;
            }

            ExcelSpreadsheetGenerator.GenerateExcelSpreadsheet(previousData, supportedInstruments, selection, _invertOutput);
            tabControl1.TabPages.Add(tabPage2);
            tabControl1.TabPages.Add(tabPage3);
            button1.Enabled = true;
            button2.Enabled = true;
            button5.Enabled = true;
            textBox2.AppendText(Environment.NewLine + "FortniteFestivalScores.xlsx written out to the directory your application is in.");
        }
    }
}
