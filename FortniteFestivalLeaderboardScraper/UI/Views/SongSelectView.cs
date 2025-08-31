using System;
using System.Windows.Forms;

namespace FortniteFestivalLeaderboardScraper.UI.Views
{
    public class SongSelectView : UserControl
    {
        public DataGridView SongsGrid { get; private set; }
        public TextBox SearchTextBox { get; private set; }
        public Button SelectAllButton { get; private set; }
        public Button DeselectAllButton { get; private set; }

        public event EventHandler SearchChanged;
        public event DataGridViewCellEventHandler ToggleQuerySong;
        public event EventHandler SelectAllClicked;
        public event EventHandler DeselectAllClicked;
        public event DataGridViewCellMouseEventHandler HeaderClicked;

        public SongSelectView()
        {
            Initialize();
        }

        private void Initialize()
        {
            Dock = DockStyle.Fill;

            SearchTextBox = new TextBox
            {
                Left = 8,
                Top = 16,
                Width = 304,
            };

            var searchLbl = new Label
            {
                Left = 338,
                Top = 21,
                Text = "Search by Title/Artist",
            };

            SelectAllButton = new Button
            {
                Left = 8,
                Width = 189,
                Height = 42,
                Anchor = AnchorStyles.Left | AnchorStyles.Bottom,
                Text = "Select All",
            };

            DeselectAllButton = new Button
            {
                Width = 189,
                Height = 42,
                Anchor = AnchorStyles.Right | AnchorStyles.Bottom,
                Text = "Deselect All",
            };

            SongsGrid = new DataGridView
            {
                Left = 8,
                Top = 64,
                Width = 1600,
                Height = 740,
                Anchor =
                    AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom,
                AllowUserToAddRows = false,
                RowHeadersVisible = false,
                Visible = false,
            };

            Controls.AddRange(
                new Control[]
                {
                    SearchTextBox,
                    searchLbl,
                    SelectAllButton,
                    DeselectAllButton,
                    SongsGrid,
                }
            );

            // Initial layout for bottom buttons
            DeselectAllButton.Left = Width - DeselectAllButton.Width - 16;
            DeselectAllButton.Top = Height - DeselectAllButton.Height - 16;
            SelectAllButton.Top = DeselectAllButton.Top;

            Resize += (s, e) =>
            {
                DeselectAllButton.Left = Width - DeselectAllButton.Width - 24;
                DeselectAllButton.Top = Height - DeselectAllButton.Height - 16;
                SelectAllButton.Top = DeselectAllButton.Top;
                SongsGrid.Width = Width - 32;
                SongsGrid.Height = SelectAllButton.Top - 72;
            };

            SearchTextBox.TextChanged += (s, e) => SearchChanged?.Invoke(this, EventArgs.Empty);
            SelectAllButton.Click += (s, e) => SelectAllClicked?.Invoke(this, EventArgs.Empty);
            DeselectAllButton.Click += (s, e) => DeselectAllClicked?.Invoke(this, EventArgs.Empty);
            SongsGrid.CellContentClick += (s, e) => ToggleQuerySong?.Invoke(this, e);
            SongsGrid.ColumnHeaderMouseClick += (s, e) => HeaderClicked?.Invoke(this, e);
        }
    }
}
