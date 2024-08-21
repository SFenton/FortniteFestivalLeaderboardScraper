using FortniteFestivalLeaderboardScraper.Helpers;
using FortniteFestivalLeaderboardScraper.Helpers.Excel;
using FortniteFestivalLeaderboardScraper.Helpers.FileIO;
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
    public partial class Form1 : Form
    {
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
            button1.Enabled = false;
            button2.Enabled = false;
            textBox2.Clear();
            textBox2.AppendText("Generating Fortnite bearer token...");
            var token = await EpicGamesExchangeTokenGenerator.GetTokenWithPermissions(textBox1.Text);
            if (token.Item1 == false || token.Item2.access_token == null)
            {
                textBox2.AppendText(Environment.NewLine + "An error occurred during authentication. Please try a new exchange code.");
                button1.Enabled = true;
                button2.Enabled = true;
                return;
            }
            textBox2.AppendText(Environment.NewLine + "Retrieving list of songs...");
            var sparkTracks = await SparkTrackRetriever.GetSparkTracks();
            textBox2.AppendText(Environment.NewLine + "Attempting to find max season value...");
            var maxSeason = await MaxSeasonIdentifier.GetMaxSeason(token.Item2.access_token);
            var previousData = JSONReadWrite.ReadLeaderboardJSON();
            var scores = await LeaderboardAPI.GetLeaderboardsForInstrument(sparkTracks, token.Item2.access_token, token.Item2.account_id, maxSeason, previousData, textBox2);
            if (scores.Item1 == false)
            {
                button1.Enabled = true;
                button2.Enabled = true;
                return;
            }
            JSONReadWrite.WriteLeaderboardJSON(scores.Item2);

            ExcelSpreadsheetGenerator.GenerateExcelSpreadsheet(scores.Item2);
            textBox2.AppendText(Environment.NewLine + "FortniteFestivalScores.xlsx written out to the directory your application is in.");
            button1.Enabled = true;
            button2.Enabled = true;
        }
    }
}
