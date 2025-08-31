using System;
using System.Windows.Forms;

namespace FortniteFestivalLeaderboardScraper.UI.Views
{
    public class ProcessView : UserControl
    {
        public TextBox ExchangeCodeTextBox { get; private set; }
        public Button GenerateCodeButton { get; private set; }
        public Button FetchScoresButton { get; private set; }
        public TextBox LogTextBox { get; private set; }
        public ProgressBar ProgressBar { get; private set; }
        public Label ProgressLabel { get; private set; }

        public event EventHandler ExchangeCodeChanged;
        public event EventHandler GenerateCodeClicked;
        public event EventHandler FetchScoresClicked;

        public ProcessView()
        {
            Initialize();
        }

        private void Initialize()
        {
            Dock = DockStyle.Fill;

            ExchangeCodeTextBox = new TextBox { Left = 16, Top = 32, Width = 200 };
            GenerateCodeButton = new Button { Left = 246, Top = 25, Width = 223, Height = 33, Text = "Generate Exchange Code" };
            FetchScoresButton = new Button { Left = 505, Top = 25, Width = 223, Height = 33, Text = "Retrieve Scores", Enabled = false };
            ProgressBar = new ProgressBar { Left = 16, Top = 80, Width = 600, Height = 16, Minimum = 0, Maximum = 100, Value = 0, Visible = true };
            ProgressLabel = new Label { Left = ProgressBar.Right + 12, Top = 78, AutoSize = true, Text = "0%" };
            var lbl = new Label { Left = 12, Top = 9, Text = "Enter Exchange Code Here" };
            var logLbl = new Label { Left = 16, Top = 100, Text = "Console Output" };
            LogTextBox = new TextBox { Left = 16, Top = 120, Multiline = true, ScrollBars = ScrollBars.Vertical };

            Controls.AddRange(new Control[] { ExchangeCodeTextBox, GenerateCodeButton, FetchScoresButton, ProgressBar, ProgressLabel, LogTextBox, lbl, logLbl });

            ExchangeCodeTextBox.TextChanged += (s, e) => { FetchScoresButton.Enabled = ExchangeCodeTextBox.TextLength > 0; ExchangeCodeChanged?.Invoke(this, EventArgs.Empty); };
            GenerateCodeButton.Click += (s, e) => GenerateCodeClicked?.Invoke(this, EventArgs.Empty);
            FetchScoresButton.Click += (s, e) => FetchScoresClicked?.Invoke(this, EventArgs.Empty);

            Resize += (s, e) => AdjustLayout();
            AdjustLayout();
        }

        private void AdjustLayout()
        {
            // Provide padding of 16px at right/bottom
            int rightPadding = 16;
            int bottomPadding = 16;
            LogTextBox.Width = Math.Max(200, ClientSize.Width - LogTextBox.Left - rightPadding);
            LogTextBox.Height = Math.Max(100, ClientSize.Height - LogTextBox.Top - bottomPadding);
        }

        public void ClearLog() => LogTextBox.Clear();
        public void AppendLog(string text) { LogTextBox.AppendText(text); }
    }
}