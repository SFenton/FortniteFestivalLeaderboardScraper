using OfficeOpenXml;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using static FortniteFestivalLeaderboardScraper.Helpers.LeaderboardAPI;

namespace FortniteFestivalLeaderboardScraper.Helpers.Excel
{
    static class ExcelSpreadsheetGenerator
    {
        public static void GenerateExcelSpreadsheet(List<LeaderboardData> data)
        {
            ExcelPackage.LicenseContext = LicenseContext.NonCommercial;
            ExcelPackage excel = new ExcelPackage();

            for (int i = 0; i < 6; i++)
            {
                var instrumentName = "";
                var scoreTracker = new ScoreTracker();
                var orderedData = new List<LeaderboardData>();
                switch (i)
                {
                    case 0:
                        instrumentName = "Drums";
                        orderedData = data.OrderBy(c => c.drums.isFullCombo).ThenBy(c => c.artist).ThenBy(c => c.title).ToList();
                        break;
                    case 1:
                        instrumentName = "Guitar";
                        orderedData = data.OrderBy(c => c.guitar.isFullCombo).ThenBy(c => c.artist).ThenBy(c => c.title).ToList();
                        break;
                    case 2:
                        instrumentName = "Bass";
                        orderedData = data.OrderBy(c => c.bass.isFullCombo).ThenBy(c => c.artist).ThenBy(c => c.title).ToList();
                        break;
                    case 3:
                        instrumentName = "Vocals";
                        orderedData = data.OrderBy(c => c.vocals.isFullCombo).ThenBy(c => c.artist).ThenBy(c => c.title).ToList();
                        break;
                    case 4:
                        instrumentName = "Pro Guitar";
                        orderedData = data.OrderBy(c => c.pro_guitar.isFullCombo).ThenBy(c => c.artist).ThenBy(c => c.title).ToList();
                        break;
                    case 5:
                        instrumentName = "Pro Bass";
                        orderedData = data.OrderBy(c => c.pro_bass.isFullCombo).ThenBy(c => c.artist).ThenBy(c => c.title).ToList();
                        break;
                }

                var worksheet = excel.Workbook.Worksheets.Add(instrumentName);
                worksheet.Cells[1, 1].RichText.Add("Artist").Bold = true;
                worksheet.Cells[1, 2].RichText.Add("Song Name").Bold = true;
                worksheet.Cells[1, 3].RichText.Add("Full Combo").Bold = true;
                worksheet.Cells[1, 4].RichText.Add("Stars").Bold = true;
                worksheet.Cells[1, 5].RichText.Add("Score").Bold = true;
                worksheet.Cells[1, 6].RichText.Add("Percentage Hit").Bold = true;
                worksheet.Cells[1, 7].RichText.Add("Season Achieved").Bold = true;

                int recordIndex = 2;
                foreach (var song in orderedData)
                {
                    worksheet.Cells[recordIndex, 1].Value = song.artist;
                    worksheet.Cells[recordIndex, 2].Value = song.title;
                    switch (i)
                    {
                        case 0:
                            scoreTracker = song.drums;
                        break;
                        case 1:
                            scoreTracker = song.guitar;
                            break;
                        case 2:
                            scoreTracker = song.bass;
                            break;
                        case 3:
                            scoreTracker = song.vocals;
                            break;
                        case 4:
                            scoreTracker = song.pro_guitar;
                            break;
                        case 5:
                            scoreTracker = song.pro_bass;
                            break;
                    }


                    worksheet.Cells[recordIndex, 3].Value = scoreTracker.isFullCombo;

                    if (scoreTracker.numStars == 6)
                    {
                        worksheet.Cells[recordIndex, 4].Value = "⍟⍟⍟⍟⍟";
                        worksheet.Cells[recordIndex, 4].Style.Font.Color.SetColor(0, 255, 223, 0);
                    } 
                    else
                    {
                        if (scoreTracker.numStars != 0)
                        {
                            var str = "";
                            for (int j = 0; j < scoreTracker.numStars; j++)
                            {
                                str += "⍟";
                            }
                            worksheet.Cells[recordIndex, 4].RichText.Add(str).Bold = true;
                        } 
                        else
                        {
                            worksheet.Cells[recordIndex, 4].Value = "N/A";
                        }
                    }
                    worksheet.Cells[recordIndex, 5].Value = scoreTracker.maxScore;
                    worksheet.Cells[recordIndex, 6].Value = (scoreTracker.percentHit / 10000) + "%";
                    worksheet.Cells[recordIndex, 7].Value = scoreTracker.season;
                    recordIndex++;
                }

                worksheet.Column(1).AutoFit();
                worksheet.Column(2).AutoFit();
                worksheet.Column(3).AutoFit();
                worksheet.Column(4).AutoFit();
                worksheet.Column(5).AutoFit();
                worksheet.Column(6).AutoFit();
                worksheet.Column(7).AutoFit();
            }

            var exePath = Path.GetDirectoryName(Assembly.GetEntryAssembly().Location);
            string p_strPath = exePath + "\\FortniteFestivalScores.xlsx";

            if (File.Exists(p_strPath))
                File.Delete(p_strPath);

            // Create excel file on physical disk  
            FileStream objFileStrm = File.Create(p_strPath);
            objFileStrm.Close();

            // Write content to excel file  
            File.WriteAllBytes(p_strPath, excel.GetAsByteArray());
            //Close Excel package 
            excel.Dispose();
        }
    }
}
