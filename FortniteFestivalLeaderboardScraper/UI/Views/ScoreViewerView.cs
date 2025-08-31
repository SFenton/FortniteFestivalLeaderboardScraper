using System;
using System.Windows.Forms;

namespace FortniteFestivalLeaderboardScraper.UI.Views
{
    public class ScoreViewerView : UserControl
    {
        public DataGridView ScoresGrid { get; private set; }
        public TextBox SearchTextBox { get; private set; }
        public RadioButton LeadRadio { get; private set; }
        public RadioButton VocalsRadio { get; private set; }
        public RadioButton BassRadio { get; private set; }
        public RadioButton DrumsRadio { get; private set; }
        public RadioButton ProLeadRadio { get; private set; }
        public RadioButton ProBassRadio { get; private set; }

        public event EventHandler SearchChanged;
        public event DataGridViewCellMouseEventHandler HeaderClicked;
        public event EventHandler InstrumentChanged;

        private FlowLayoutPanel _instrumentPanel;

        public ScoreViewerView()
        {
            Initialize();
        }

        private void Initialize()
        {
            Dock = DockStyle.Fill;

            SearchTextBox = new TextBox
            {
                Left = 8,
                Top = 12,
                Width = 304,
            };
            var searchLbl = new Label
            {
                Left = 338,
                Top = 16,
                Text = "Search by Title/Artist",
            };

            // Flow panel for instrument radios to ensure spacing & clickability
            _instrumentPanel = new FlowLayoutPanel
            {
                Left = 8,
                Top = 44,
                Width = 800,
                Height = 28,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                Margin = new Padding(0),
                Padding = new Padding(0),
            };

            LeadRadio = CreateRadio("Lead", true);
            VocalsRadio = CreateRadio("Vocals");
            DrumsRadio = CreateRadio("Drums");
            BassRadio = CreateRadio("Bass");
            ProLeadRadio = CreateRadio("Pro Lead");
            ProBassRadio = CreateRadio("Pro Bass");

            _instrumentPanel.Controls.AddRange(
                new Control[]
                {
                    LeadRadio,
                    VocalsRadio,
                    DrumsRadio,
                    BassRadio,
                    ProLeadRadio,
                    ProBassRadio,
                }
            );

            ScoresGrid = new DataGridView
            {
                Left = 8,
                Top = _instrumentPanel.Bottom + 8,
                Width = 1600,
                Height = 700,
                Anchor =
                    AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom,
                AllowUserToAddRows = false,
                RowHeadersVisible = false,
                Visible = false,
            };

            Controls.AddRange(
                new Control[] { SearchTextBox, searchLbl, _instrumentPanel, ScoresGrid }
            );

            Resize += (s, e) =>
            {
                ScoresGrid.Top = _instrumentPanel.Bottom + 8;
                ScoresGrid.Width = Width - 16;
                ScoresGrid.Height = Height - ScoresGrid.Top - 8;
            };

            SearchTextBox.TextChanged += (s, e) => SearchChanged?.Invoke(this, EventArgs.Empty);
            EventHandler inst = (s, e) => InstrumentChanged?.Invoke(this, EventArgs.Empty);
            LeadRadio.CheckedChanged += inst;
            VocalsRadio.CheckedChanged += inst;
            DrumsRadio.CheckedChanged += inst;
            BassRadio.CheckedChanged += inst;
            ProLeadRadio.CheckedChanged += inst;
            ProBassRadio.CheckedChanged += inst;
            ScoresGrid.ColumnHeaderMouseClick += (s, e) => HeaderClicked?.Invoke(this, e);
        }

        private RadioButton CreateRadio(string text, bool isChecked = false)
        {
            return new RadioButton
            {
                Text = text,
                AutoSize = true,
                Checked = isChecked,
                Margin = new Padding(8, 3, 8, 3),
            };
        }
    }
}
