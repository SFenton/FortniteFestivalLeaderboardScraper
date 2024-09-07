using System.Windows.Forms;
using System;

namespace FortniteFestivalLeaderboardScraper
{
    partial class Form1
    {
        /// <summary>
        /// Required designer variable.
        /// </summary>
        private System.ComponentModel.IContainer components = null;

        /// <summary>
        /// Clean up any resources being used.
        /// </summary>
        /// <param name="disposing">true if managed resources should be disposed; otherwise, false.</param>
        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
            {
                components.Dispose();
            }
            base.Dispose(disposing);
        }

        #region Windows Form Designer generated code

        /// <summary>
        /// Required method for Designer support - do not modify
        /// the contents of this method with the code editor.
        /// </summary>
        private void InitializeComponent()
        {
            this.components = new System.ComponentModel.Container();
            this.textBox1 = new System.Windows.Forms.TextBox();
            this.label1 = new System.Windows.Forms.Label();
            this.button1 = new System.Windows.Forms.Button();
            this.button2 = new System.Windows.Forms.Button();
            this.contextMenuStrip1 = new System.Windows.Forms.ContextMenuStrip(this.components);
            this.textBox2 = new System.Windows.Forms.TextBox();
            this.label2 = new System.Windows.Forms.Label();
            this.tabControl1 = new System.Windows.Forms.TabControl();
            this.tabPage1 = new System.Windows.Forms.TabPage();
            this.button5 = new System.Windows.Forms.Button();
            this.tabPage2 = new System.Windows.Forms.TabPage();
            this.label8 = new System.Windows.Forms.Label();
            this.textBox3 = new System.Windows.Forms.TextBox();
            this.button4 = new System.Windows.Forms.Button();
            this.button3 = new System.Windows.Forms.Button();
            this.dataGridView1 = new System.Windows.Forms.DataGridView();
            this.label4 = new System.Windows.Forms.Label();
            this.label3 = new System.Windows.Forms.Label();
            this.tabPage4 = new System.Windows.Forms.TabPage();
            this.radioButton6 = new System.Windows.Forms.RadioButton();
            this.radioButton5 = new System.Windows.Forms.RadioButton();
            this.radioButton4 = new System.Windows.Forms.RadioButton();
            this.radioButton3 = new System.Windows.Forms.RadioButton();
            this.radioButton2 = new System.Windows.Forms.RadioButton();
            this.radioButton1 = new System.Windows.Forms.RadioButton();
            this.label9 = new System.Windows.Forms.Label();
            this.textBox4 = new System.Windows.Forms.TextBox();
            this.dataGridView2 = new System.Windows.Forms.DataGridView();
            this.dataGridViewTextBoxColumn1 = new System.Windows.Forms.DataGridViewTextBoxColumn();
            this.dataGridViewTextBoxColumn2 = new System.Windows.Forms.DataGridViewTextBoxColumn();
            this.FullComboColumn = new System.Windows.Forms.DataGridViewTextBoxColumn();
            this.StarsColumn = new System.Windows.Forms.DataGridViewTextBoxColumn();
            this.ScoreColumn = new System.Windows.Forms.DataGridViewTextBoxColumn();
            this.PercentageHitColumn = new System.Windows.Forms.DataGridViewTextBoxColumn();
            this.SeasonAchievedColumn = new System.Windows.Forms.DataGridViewTextBoxColumn();
            this.DifficultyColumn = new System.Windows.Forms.DataGridViewTextBoxColumn();
            this.tabPage3 = new System.Windows.Forms.TabPage();
            this.invertOutput = new System.Windows.Forms.CheckBox();
            this.label7 = new System.Windows.Forms.Label();
            this.stars = new System.Windows.Forms.RadioButton();
            this.difficulty = new System.Windows.Forms.RadioButton();
            this.percentage = new System.Windows.Forms.RadioButton();
            this.score = new System.Windows.Forms.RadioButton();
            this.artist = new System.Windows.Forms.RadioButton();
            this.title = new System.Windows.Forms.RadioButton();
            this.fullCombo = new System.Windows.Forms.RadioButton();
            this.label6 = new System.Windows.Forms.Label();
            this.label5 = new System.Windows.Forms.Label();
            this.proBassCheck = new System.Windows.Forms.CheckBox();
            this.proLeadCheck = new System.Windows.Forms.CheckBox();
            this.bassCheck = new System.Windows.Forms.CheckBox();
            this.drumsCheck = new System.Windows.Forms.CheckBox();
            this.vocalsCheck = new System.Windows.Forms.CheckBox();
            this.leadCheck = new System.Windows.Forms.CheckBox();
            this.dataGridViewCheckBoxColumn1 = new System.Windows.Forms.DataGridViewCheckBoxColumn();
            this.dataGridViewTextBoxColumn3 = new System.Windows.Forms.DataGridViewTextBoxColumn();
            this.isSelected = new System.Windows.Forms.DataGridViewCheckBoxColumn();
            this.inLocalData = new System.Windows.Forms.DataGridViewTextBoxColumn();
            this.tt = new System.Windows.Forms.DataGridViewTextBoxColumn();
            this.Column2 = new System.Windows.Forms.DataGridViewTextBoxColumn();
            this.DateActive = new System.Windows.Forms.DataGridViewTextBoxColumn();
            this.LD = new System.Windows.Forms.DataGridViewTextBoxColumn();
            this.BD = new System.Windows.Forms.DataGridViewTextBoxColumn();
            this.VD = new System.Windows.Forms.DataGridViewTextBoxColumn();
            this.DD = new System.Windows.Forms.DataGridViewTextBoxColumn();
            this.PGD = new System.Windows.Forms.DataGridViewTextBoxColumn();
            this.PBD = new System.Windows.Forms.DataGridViewTextBoxColumn();
            this.su = new System.Windows.Forms.DataGridViewTextBoxColumn();
            this.tabControl1.SuspendLayout();
            this.tabPage1.SuspendLayout();
            this.tabPage2.SuspendLayout();
            ((System.ComponentModel.ISupportInitialize)(this.dataGridView1)).BeginInit();
            this.tabPage4.SuspendLayout();
            ((System.ComponentModel.ISupportInitialize)(this.dataGridView2)).BeginInit();
            this.tabPage3.SuspendLayout();
            this.SuspendLayout();
            this.FormClosed += OnMainWindowClosing;
            this.Load += OnMainWindowLoad;
            // 
            // textBox1
            // 
            this.textBox1.Location = new System.Drawing.Point(16, 32);
            this.textBox1.Name = "textBox1";
            this.textBox1.Size = new System.Drawing.Size(200, 26);
            this.textBox1.TabIndex = 0;
            this.textBox1.TextChanged += TextBox1_TextChanged;
            // 
            // label1
            // 
            this.label1.AutoSize = true;
            this.label1.Location = new System.Drawing.Point(12, 9);
            this.label1.Name = "label1";
            this.label1.Size = new System.Drawing.Size(204, 20);
            this.label1.TabIndex = 1;
            this.label1.Text = "Enter Exchange Code Here";
            // 
            // button1
            // 
            this.button1.Location = new System.Drawing.Point(246, 25);
            this.button1.Name = "button1";
            this.button1.Size = new System.Drawing.Size(223, 33);
            this.button1.TabIndex = 2;
            this.button1.Text = "Generate Exchange Code";
            this.button1.UseVisualStyleBackColor = true;
            this.button1.Click += new System.EventHandler(this.button1_Click);
            // 
            // button2
            // 
            this.button2.Enabled = false;
            this.button2.Location = new System.Drawing.Point(505, 25);
            this.button2.Name = "button2";
            this.button2.Size = new System.Drawing.Size(223, 33);
            this.button2.TabIndex = 3;
            this.button2.Text = "Retrieve Scores";
            this.button2.UseVisualStyleBackColor = true;
            this.button2.Click += new System.EventHandler(this.button2_Click);
            // 
            // contextMenuStrip1
            // 
            this.contextMenuStrip1.ImageScalingSize = new System.Drawing.Size(24, 24);
            this.contextMenuStrip1.Name = "contextMenuStrip1";
            this.contextMenuStrip1.Size = new System.Drawing.Size(61, 4);
            // 
            // textBox2
            // 
            this.textBox2.Anchor = ((System.Windows.Forms.AnchorStyles)((((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Bottom) 
            | System.Windows.Forms.AnchorStyles.Left) 
            | System.Windows.Forms.AnchorStyles.Right)));
            this.textBox2.Location = new System.Drawing.Point(16, 116);
            this.textBox2.Multiline = true;
            this.textBox2.Name = "textBox2";
            this.textBox2.ScrollBars = System.Windows.Forms.ScrollBars.Vertical;
            this.textBox2.Size = new System.Drawing.Size(1638, 775);
            this.textBox2.TabIndex = 6;
            // 
            // label2
            // 
            this.label2.AutoSize = true;
            this.label2.Location = new System.Drawing.Point(16, 90);
            this.label2.Name = "label2";
            this.label2.Size = new System.Drawing.Size(120, 20);
            this.label2.TabIndex = 7;
            this.label2.Text = "Console Output";
            // 
            // tabControl1
            // 
            this.tabControl1.Controls.Add(this.tabPage1);
            this.tabControl1.Controls.Add(this.tabPage2);
            this.tabControl1.Controls.Add(this.tabPage4);
            this.tabControl1.Controls.Add(this.tabPage3);
            this.tabControl1.Dock = System.Windows.Forms.DockStyle.Fill;
            this.tabControl1.Location = new System.Drawing.Point(0, 0);
            this.tabControl1.Name = "tabControl1";
            this.tabControl1.SelectedIndex = 0;
            this.tabControl1.Size = new System.Drawing.Size(1668, 932);
            this.tabControl1.TabIndex = 8;
            this.tabControl1.Click += new System.EventHandler(this.onSongSelectFocused);
            // 
            // tabPage1
            // 
            this.tabPage1.Controls.Add(this.button5);
            this.tabPage1.Controls.Add(this.label2);
            this.tabPage1.Controls.Add(this.textBox2);
            this.tabPage1.Controls.Add(this.button2);
            this.tabPage1.Controls.Add(this.button1);
            this.tabPage1.Controls.Add(this.label1);
            this.tabPage1.Controls.Add(this.textBox1);
            this.tabPage1.Location = new System.Drawing.Point(4, 29);
            this.tabPage1.Name = "tabPage1";
            this.tabPage1.Padding = new System.Windows.Forms.Padding(3);
            this.tabPage1.Size = new System.Drawing.Size(1660, 899);
            this.tabPage1.TabIndex = 0;
            this.tabPage1.Text = "Process Scores";
            this.tabPage1.UseVisualStyleBackColor = true;
            // 
            // button5
            // 
            this.button5.Anchor = ((System.Windows.Forms.AnchorStyles)((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Right)));
            this.button5.Location = new System.Drawing.Point(1429, 25);
            this.button5.Name = "button5";
            this.button5.Size = new System.Drawing.Size(223, 33);
            this.button5.TabIndex = 8;
            this.button5.Text = "Regenerate Output";
            this.button5.UseVisualStyleBackColor = true;
            this.button5.Click += new System.EventHandler(this.button5_Click);
            // 
            // tabPage2
            // 
            this.tabPage2.Controls.Add(this.label8);
            this.tabPage2.Controls.Add(this.textBox3);
            this.tabPage2.Controls.Add(this.button4);
            this.tabPage2.Controls.Add(this.button3);
            this.tabPage2.Controls.Add(this.dataGridView1);
            this.tabPage2.Controls.Add(this.label4);
            this.tabPage2.Controls.Add(this.label3);
            this.tabPage2.Location = new System.Drawing.Point(4, 29);
            this.tabPage2.Name = "tabPage2";
            this.tabPage2.Padding = new System.Windows.Forms.Padding(3);
            this.tabPage2.Size = new System.Drawing.Size(1660, 899);
            this.tabPage2.TabIndex = 1;
            this.tabPage2.Text = "Select Songs";
            this.tabPage2.UseVisualStyleBackColor = true;
            // 
            // label8
            // 
            this.label8.AutoSize = true;
            this.label8.Location = new System.Drawing.Point(338, 21);
            this.label8.Name = "label8";
            this.label8.Size = new System.Drawing.Size(154, 20);
            this.label8.TabIndex = 6;
            this.label8.Text = "Search by Title/Artist";
            // 
            // textBox3
            // 
            this.textBox3.Location = new System.Drawing.Point(8, 16);
            this.textBox3.Name = "textBox3";
            this.textBox3.Size = new System.Drawing.Size(304, 26);
            this.textBox3.TabIndex = 5;
            this.textBox3.TextChanged += TextBox3_TextChanged;
            // 
            // button4
            // 
            this.button4.Anchor = ((System.Windows.Forms.AnchorStyles)((System.Windows.Forms.AnchorStyles.Bottom | System.Windows.Forms.AnchorStyles.Right)));
            this.button4.Location = new System.Drawing.Point(1463, 849);
            this.button4.Name = "button4";
            this.button4.Size = new System.Drawing.Size(189, 42);
            this.button4.TabIndex = 4;
            this.button4.Text = "Deselect All";
            this.button4.UseVisualStyleBackColor = true;
            this.button4.Click += new System.EventHandler(this.button4_Click);
            // 
            // button3
            // 
            this.button3.Anchor = ((System.Windows.Forms.AnchorStyles)((System.Windows.Forms.AnchorStyles.Bottom | System.Windows.Forms.AnchorStyles.Left)));
            this.button3.Location = new System.Drawing.Point(8, 849);
            this.button3.Name = "button3";
            this.button3.Size = new System.Drawing.Size(189, 42);
            this.button3.TabIndex = 3;
            this.button3.Text = "Select All";
            this.button3.UseVisualStyleBackColor = true;
            this.button3.Click += new System.EventHandler(this.button3_Click);
            // 
            // dataGridView1
            // 
            this.dataGridView1.AllowUserToAddRows = false;
            this.dataGridView1.Anchor = ((System.Windows.Forms.AnchorStyles)((((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Bottom) 
            | System.Windows.Forms.AnchorStyles.Left) 
            | System.Windows.Forms.AnchorStyles.Right)));
            this.dataGridView1.ColumnHeadersHeightSizeMode = System.Windows.Forms.DataGridViewColumnHeadersHeightSizeMode.AutoSize;
            this.dataGridView1.Columns.AddRange(new System.Windows.Forms.DataGridViewColumn[] {
            this.isSelected,
            this.inLocalData,
            this.tt,
            this.Column2,
            this.DateActive,
            this.LD,
            this.BD,
            this.VD,
            this.DD,
            this.PGD,
            this.PBD,
            this.su});
            this.dataGridView1.Location = new System.Drawing.Point(8, 64);
            this.dataGridView1.Name = "dataGridView1";
            this.dataGridView1.RowHeadersVisible = false;
            this.dataGridView1.RowHeadersWidth = 62;
            this.dataGridView1.RowTemplate.Height = 28;
            this.dataGridView1.Size = new System.Drawing.Size(1656, 771);
            this.dataGridView1.TabIndex = 2;
            this.dataGridView1.CellContentClick += DataGridView1_CellContentClick;
            this.dataGridView1.ColumnHeaderMouseClick += onColumnHeaderMouseClick;
            this.dataGridView1.Visible = false;
            // 
            // label4
            // 
            this.label4.Anchor = ((System.Windows.Forms.AnchorStyles)((((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Bottom) 
            | System.Windows.Forms.AnchorStyles.Left) 
            | System.Windows.Forms.AnchorStyles.Right)));
            this.label4.Location = new System.Drawing.Point(0, 0);
            this.label4.Name = "label4";
            this.label4.Size = new System.Drawing.Size(1056, 214);
            this.label4.TabIndex = 1;
            this.label4.Text = "Loading available jam tracks...";
            this.label4.TextAlign = System.Drawing.ContentAlignment.MiddleCenter;
            this.label4.Visible = false;
            // 
            // label3
            // 
            this.label3.Anchor = ((System.Windows.Forms.AnchorStyles)((((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Bottom) 
            | System.Windows.Forms.AnchorStyles.Left) 
            | System.Windows.Forms.AnchorStyles.Right)));
            this.label3.AutoSize = true;
            this.label3.Location = new System.Drawing.Point(334, 349);
            this.label3.Name = "label3";
            this.label3.Size = new System.Drawing.Size(0, 20);
            this.label3.TabIndex = 0;
            // 
            // tabPage4
            // 
            this.tabPage4.Controls.Add(this.radioButton6);
            this.tabPage4.Controls.Add(this.radioButton5);
            this.tabPage4.Controls.Add(this.radioButton4);
            this.tabPage4.Controls.Add(this.radioButton3);
            this.tabPage4.Controls.Add(this.radioButton2);
            this.tabPage4.Controls.Add(this.radioButton1);
            this.tabPage4.Controls.Add(this.label9);
            this.tabPage4.Controls.Add(this.textBox4);
            this.tabPage4.Controls.Add(this.dataGridView2);
            this.tabPage4.Location = new System.Drawing.Point(4, 29);
            this.tabPage4.Name = "tabPage4";
            this.tabPage4.Padding = new System.Windows.Forms.Padding(3);
            this.tabPage4.Size = new System.Drawing.Size(1660, 899);
            this.tabPage4.TabIndex = 3;
            this.tabPage4.Text = "Score Viewer";
            this.tabPage4.UseVisualStyleBackColor = true;
            // 
            // radioButton6
            // 
            this.radioButton6.Anchor = ((System.Windows.Forms.AnchorStyles)((System.Windows.Forms.AnchorStyles.Bottom | System.Windows.Forms.AnchorStyles.Left)));
            this.radioButton6.AutoSize = true;
            this.radioButton6.Location = new System.Drawing.Point(443, 856);
            this.radioButton6.Name = "radioButton6";
            this.radioButton6.Size = new System.Drawing.Size(98, 24);
            this.radioButton6.TabIndex = 15;
            this.radioButton6.TabStop = true;
            this.radioButton6.Text = "Pro Bass";
            this.radioButton6.UseVisualStyleBackColor = true;
            this.radioButton6.CheckedChanged += onInstrumentScoreChanged;
            // 
            // radioButton5
            // 
            this.radioButton5.Anchor = ((System.Windows.Forms.AnchorStyles)((System.Windows.Forms.AnchorStyles.Bottom | System.Windows.Forms.AnchorStyles.Left)));
            this.radioButton5.AutoSize = true;
            this.radioButton5.Location = new System.Drawing.Point(338, 856);
            this.radioButton5.Name = "radioButton5";
            this.radioButton5.Size = new System.Drawing.Size(98, 24);
            this.radioButton5.TabIndex = 14;
            this.radioButton5.TabStop = true;
            this.radioButton5.Text = "Pro Lead";
            this.radioButton5.UseVisualStyleBackColor = true;
            this.radioButton5.CheckedChanged += onInstrumentScoreChanged;
            // 
            // radioButton4
            // 
            this.radioButton4.Anchor = ((System.Windows.Forms.AnchorStyles)((System.Windows.Forms.AnchorStyles.Bottom | System.Windows.Forms.AnchorStyles.Left)));
            this.radioButton4.AutoSize = true;
            this.radioButton4.Location = new System.Drawing.Point(261, 855);
            this.radioButton4.Name = "radioButton4";
            this.radioButton4.Size = new System.Drawing.Size(70, 24);
            this.radioButton4.TabIndex = 13;
            this.radioButton4.TabStop = true;
            this.radioButton4.Text = "Bass";
            this.radioButton4.UseVisualStyleBackColor = true;
            this.radioButton4.CheckedChanged += onInstrumentScoreChanged;
            // 
            // radioButton3
            // 
            this.radioButton3.Anchor = ((System.Windows.Forms.AnchorStyles)((System.Windows.Forms.AnchorStyles.Bottom | System.Windows.Forms.AnchorStyles.Left)));
            this.radioButton3.AutoSize = true;
            this.radioButton3.Location = new System.Drawing.Point(172, 855);
            this.radioButton3.Name = "radioButton3";
            this.radioButton3.Size = new System.Drawing.Size(82, 24);
            this.radioButton3.TabIndex = 12;
            this.radioButton3.TabStop = true;
            this.radioButton3.Text = "Vocals";
            this.radioButton3.UseVisualStyleBackColor = true;
            this.radioButton3.CheckedChanged += onInstrumentScoreChanged;
            // 
            // radioButton2
            // 
            this.radioButton2.Anchor = ((System.Windows.Forms.AnchorStyles)((System.Windows.Forms.AnchorStyles.Bottom | System.Windows.Forms.AnchorStyles.Left)));
            this.radioButton2.AutoSize = true;
            this.radioButton2.Location = new System.Drawing.Point(84, 856);
            this.radioButton2.Name = "radioButton2";
            this.radioButton2.Size = new System.Drawing.Size(81, 24);
            this.radioButton2.TabIndex = 11;
            this.radioButton2.TabStop = true;
            this.radioButton2.Text = "Drums";
            this.radioButton2.UseVisualStyleBackColor = true;
            this.radioButton2.CheckedChanged += onInstrumentScoreChanged;
            // 
            // radioButton1
            // 
            this.radioButton1.Anchor = ((System.Windows.Forms.AnchorStyles)((System.Windows.Forms.AnchorStyles.Bottom | System.Windows.Forms.AnchorStyles.Left)));
            this.radioButton1.AutoSize = true;
            this.radioButton1.Checked = true;
            this.radioButton1.Location = new System.Drawing.Point(8, 856);
            this.radioButton1.Name = "radioButton1";
            this.radioButton1.Size = new System.Drawing.Size(70, 24);
            this.radioButton1.TabIndex = 10;
            this.radioButton1.TabStop = true;
            this.radioButton1.Text = "Lead";
            this.radioButton1.UseVisualStyleBackColor = true;
            this.radioButton1.CheckedChanged += onInstrumentScoreChanged;
            // 
            // label9
            // 
            this.label9.AutoSize = true;
            this.label9.Location = new System.Drawing.Point(338, 21);
            this.label9.Name = "label9";
            this.label9.Size = new System.Drawing.Size(154, 20);
            this.label9.TabIndex = 9;
            this.label9.Text = "Search by Title/Artist";
            // 
            // textBox4
            // 
            this.textBox4.Location = new System.Drawing.Point(8, 16);
            this.textBox4.Name = "textBox4";
            this.textBox4.Size = new System.Drawing.Size(304, 26);
            this.textBox4.TabIndex = 8;
            this.textBox4.TextChanged += TextBox4_TextChanged;
            // 
            // dataGridView2
            // 
            this.dataGridView2.AllowUserToAddRows = false;
            this.dataGridView2.Anchor = ((System.Windows.Forms.AnchorStyles)((((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Bottom) 
            | System.Windows.Forms.AnchorStyles.Left) 
            | System.Windows.Forms.AnchorStyles.Right)));
            this.dataGridView2.ColumnHeadersHeightSizeMode = System.Windows.Forms.DataGridViewColumnHeadersHeightSizeMode.AutoSize;
            this.dataGridView2.Columns.AddRange(new System.Windows.Forms.DataGridViewColumn[] {
            this.dataGridViewTextBoxColumn1,
            this.dataGridViewTextBoxColumn2,
            this.FullComboColumn,
            this.StarsColumn,
            this.ScoreColumn,
            this.PercentageHitColumn,
            this.SeasonAchievedColumn,
            this.DifficultyColumn});
            this.dataGridView2.Location = new System.Drawing.Point(8, 64);
            this.dataGridView2.Name = "dataGridView2";
            this.dataGridView2.RowHeadersVisible = false;
            this.dataGridView2.RowHeadersWidth = 62;
            this.dataGridView2.RowTemplate.Height = 28;
            this.dataGridView2.Size = new System.Drawing.Size(1656, 771);
            this.dataGridView2.TabIndex = 7;
            this.dataGridView2.Visible = false;
            this.dataGridView2.ColumnHeaderMouseClick += onScoreViewerColumnHeaderMouseClick;
            // 
            // dataGridViewTextBoxColumn1
            // 
            this.dataGridViewTextBoxColumn1.DataPropertyName = "track.tt";
            this.dataGridViewTextBoxColumn1.HeaderText = "Title";
            this.dataGridViewTextBoxColumn1.MinimumWidth = 8;
            this.dataGridViewTextBoxColumn1.Name = "dataGridViewTextBoxColumn1";
            this.dataGridViewTextBoxColumn1.Width = 150;
            // 
            // dataGridViewTextBoxColumn2
            // 
            this.dataGridViewTextBoxColumn2.HeaderText = "Artist";
            this.dataGridViewTextBoxColumn2.MinimumWidth = 8;
            this.dataGridViewTextBoxColumn2.Name = "dataGridViewTextBoxColumn2";
            this.dataGridViewTextBoxColumn2.Width = 150;
            // 
            // FullComboColumn
            // 
            this.FullComboColumn.HeaderText = "Full Combo";
            this.FullComboColumn.MinimumWidth = 8;
            this.FullComboColumn.Name = "FullComboColumn";
            this.FullComboColumn.Width = 150;
            // 
            // StarsColumn
            // 
            this.StarsColumn.HeaderText = "Stars";
            this.StarsColumn.MinimumWidth = 8;
            this.StarsColumn.Name = "StarsColumn";
            this.StarsColumn.Width = 150;
            // 
            // ScoreColumn
            // 
            this.ScoreColumn.HeaderText = "Score";
            this.ScoreColumn.MinimumWidth = 8;
            this.ScoreColumn.Name = "ScoreColumn";
            this.ScoreColumn.Width = 150;
            // 
            // PercentageHitColumn
            // 
            this.PercentageHitColumn.HeaderText = "Percentage Hit";
            this.PercentageHitColumn.MinimumWidth = 8;
            this.PercentageHitColumn.Name = "PercentageHitColumn";
            this.PercentageHitColumn.Width = 150;
            // 
            // SeasonAchievedColumn
            // 
            this.SeasonAchievedColumn.HeaderText = "Season Achieved";
            this.SeasonAchievedColumn.MinimumWidth = 8;
            this.SeasonAchievedColumn.Name = "SeasonAchievedColumn";
            this.SeasonAchievedColumn.Width = 150;
            // 
            // DifficultyColumn
            // 
            this.DifficultyColumn.HeaderText = "Difficulty";
            this.DifficultyColumn.MinimumWidth = 8;
            this.DifficultyColumn.Name = "DifficultyColumn";
            this.DifficultyColumn.Width = 150;
            // 
            // tabPage3
            // 
            this.tabPage3.Controls.Add(this.invertOutput);
            this.tabPage3.Controls.Add(this.label7);
            this.tabPage3.Controls.Add(this.stars);
            this.tabPage3.Controls.Add(this.difficulty);
            this.tabPage3.Controls.Add(this.percentage);
            this.tabPage3.Controls.Add(this.score);
            this.tabPage3.Controls.Add(this.artist);
            this.tabPage3.Controls.Add(this.title);
            this.tabPage3.Controls.Add(this.fullCombo);
            this.tabPage3.Controls.Add(this.label6);
            this.tabPage3.Controls.Add(this.label5);
            this.tabPage3.Controls.Add(this.proBassCheck);
            this.tabPage3.Controls.Add(this.proLeadCheck);
            this.tabPage3.Controls.Add(this.bassCheck);
            this.tabPage3.Controls.Add(this.drumsCheck);
            this.tabPage3.Controls.Add(this.vocalsCheck);
            this.tabPage3.Controls.Add(this.leadCheck);
            this.tabPage3.Location = new System.Drawing.Point(4, 29);
            this.tabPage3.Name = "tabPage3";
            this.tabPage3.Padding = new System.Windows.Forms.Padding(3);
            this.tabPage3.Size = new System.Drawing.Size(1660, 899);
            this.tabPage3.TabIndex = 2;
            this.tabPage3.Text = "Options";
            this.tabPage3.UseVisualStyleBackColor = true;
            // 
            // invertOutput
            // 
            this.invertOutput.AutoSize = true;
            this.invertOutput.Location = new System.Drawing.Point(378, 27);
            this.invertOutput.Name = "invertOutput";
            this.invertOutput.Size = new System.Drawing.Size(172, 24);
            this.invertOutput.TabIndex = 16;
            this.invertOutput.Text = "Invert Output Order";
            this.invertOutput.UseVisualStyleBackColor = true;
            this.invertOutput.CheckedChanged += new System.EventHandler(this.onInvertOutputSelected);
            // 
            // label7
            // 
            this.label7.AutoSize = true;
            this.label7.Location = new System.Drawing.Point(374, 0);
            this.label7.Name = "label7";
            this.label7.Size = new System.Drawing.Size(138, 20);
            this.label7.TabIndex = 15;
            this.label7.Text = "Additional Options";
            // 
            // stars
            // 
            this.stars.AutoSize = true;
            this.stars.Location = new System.Drawing.Point(184, 209);
            this.stars.Name = "stars";
            this.stars.Size = new System.Drawing.Size(72, 24);
            this.stars.TabIndex = 14;
            this.stars.TabStop = true;
            this.stars.Text = "Stars";
            this.stars.UseVisualStyleBackColor = true;
            this.stars.CheckedChanged += new System.EventHandler(this.onOutputFormatSelection);
            // 
            // difficulty
            // 
            this.difficulty.AutoSize = true;
            this.difficulty.Location = new System.Drawing.Point(184, 179);
            this.difficulty.Name = "difficulty";
            this.difficulty.Size = new System.Drawing.Size(94, 24);
            this.difficulty.TabIndex = 13;
            this.difficulty.TabStop = true;
            this.difficulty.Text = "Difficulty";
            this.difficulty.UseVisualStyleBackColor = true;
            this.difficulty.CheckedChanged += new System.EventHandler(this.onOutputFormatSelection);
            // 
            // percentage
            // 
            this.percentage.AutoSize = true;
            this.percentage.Location = new System.Drawing.Point(184, 149);
            this.percentage.Name = "percentage";
            this.percentage.Size = new System.Drawing.Size(116, 24);
            this.percentage.TabIndex = 12;
            this.percentage.TabStop = true;
            this.percentage.Text = "Percentage";
            this.percentage.UseVisualStyleBackColor = true;
            this.percentage.CheckedChanged += new System.EventHandler(this.onOutputFormatSelection);
            // 
            // score
            // 
            this.score.AutoSize = true;
            this.score.Location = new System.Drawing.Point(184, 119);
            this.score.Name = "score";
            this.score.Size = new System.Drawing.Size(76, 24);
            this.score.TabIndex = 11;
            this.score.TabStop = true;
            this.score.Text = "Score";
            this.score.UseVisualStyleBackColor = true;
            this.score.CheckedChanged += new System.EventHandler(this.onOutputFormatSelection);
            // 
            // artist
            // 
            this.artist.AutoSize = true;
            this.artist.Location = new System.Drawing.Point(184, 89);
            this.artist.Name = "artist";
            this.artist.Size = new System.Drawing.Size(71, 24);
            this.artist.TabIndex = 10;
            this.artist.TabStop = true;
            this.artist.Text = "Artist";
            this.artist.UseVisualStyleBackColor = true;
            this.artist.CheckedChanged += new System.EventHandler(this.onOutputFormatSelection);
            // 
            // title
            // 
            this.title.AutoSize = true;
            this.title.Location = new System.Drawing.Point(184, 57);
            this.title.Name = "title";
            this.title.Size = new System.Drawing.Size(63, 24);
            this.title.TabIndex = 9;
            this.title.TabStop = true;
            this.title.Text = "Title";
            this.title.UseVisualStyleBackColor = true;
            this.title.CheckedChanged += new System.EventHandler(this.onOutputFormatSelection);
            // 
            // fullCombo
            // 
            this.fullCombo.AutoSize = true;
            this.fullCombo.Checked = true;
            this.fullCombo.Location = new System.Drawing.Point(184, 27);
            this.fullCombo.Name = "fullCombo";
            this.fullCombo.Size = new System.Drawing.Size(114, 24);
            this.fullCombo.TabIndex = 8;
            this.fullCombo.TabStop = true;
            this.fullCombo.Text = "Full Combo";
            this.fullCombo.UseVisualStyleBackColor = true;
            this.fullCombo.CheckedChanged += new System.EventHandler(this.onOutputFormatSelection);
            // 
            // label6
            // 
            this.label6.AutoSize = true;
            this.label6.Location = new System.Drawing.Point(180, 0);
            this.label6.Name = "label6";
            this.label6.Size = new System.Drawing.Size(136, 20);
            this.label6.TabIndex = 7;
            this.label6.Text = "Output Sort Order";
            // 
            // label5
            // 
            this.label5.AutoSize = true;
            this.label5.Location = new System.Drawing.Point(4, 0);
            this.label5.Name = "label5";
            this.label5.Size = new System.Drawing.Size(94, 20);
            this.label5.TabIndex = 6;
            this.label5.Text = "Instruments";
            // 
            // proBassCheck
            // 
            this.proBassCheck.AutoSize = true;
            this.proBassCheck.Checked = true;
            this.proBassCheck.CheckState = System.Windows.Forms.CheckState.Checked;
            this.proBassCheck.Location = new System.Drawing.Point(9, 179);
            this.proBassCheck.Name = "proBassCheck";
            this.proBassCheck.Size = new System.Drawing.Size(99, 24);
            this.proBassCheck.TabIndex = 5;
            this.proBassCheck.Text = "Pro Bass";
            this.proBassCheck.UseVisualStyleBackColor = true;
            this.proBassCheck.CheckedChanged += new System.EventHandler(this.onInstrumentOutputSelected);
            // 
            // proLeadCheck
            // 
            this.proLeadCheck.AutoSize = true;
            this.proLeadCheck.Checked = true;
            this.proLeadCheck.CheckState = System.Windows.Forms.CheckState.Checked;
            this.proLeadCheck.Location = new System.Drawing.Point(9, 149);
            this.proLeadCheck.Name = "proLeadCheck";
            this.proLeadCheck.Size = new System.Drawing.Size(99, 24);
            this.proLeadCheck.TabIndex = 4;
            this.proLeadCheck.Text = "Pro Lead";
            this.proLeadCheck.UseVisualStyleBackColor = true;
            this.proLeadCheck.CheckedChanged += new System.EventHandler(this.onInstrumentOutputSelected);
            // 
            // bassCheck
            // 
            this.bassCheck.AutoSize = true;
            this.bassCheck.Checked = true;
            this.bassCheck.CheckState = System.Windows.Forms.CheckState.Checked;
            this.bassCheck.Location = new System.Drawing.Point(9, 119);
            this.bassCheck.Name = "bassCheck";
            this.bassCheck.Size = new System.Drawing.Size(71, 24);
            this.bassCheck.TabIndex = 3;
            this.bassCheck.Text = "Bass";
            this.bassCheck.UseVisualStyleBackColor = true;
            this.bassCheck.CheckedChanged += new System.EventHandler(this.onInstrumentOutputSelected);
            // 
            // drumsCheck
            // 
            this.drumsCheck.AutoSize = true;
            this.drumsCheck.Checked = true;
            this.drumsCheck.CheckState = System.Windows.Forms.CheckState.Checked;
            this.drumsCheck.Location = new System.Drawing.Point(9, 89);
            this.drumsCheck.Name = "drumsCheck";
            this.drumsCheck.Size = new System.Drawing.Size(82, 24);
            this.drumsCheck.TabIndex = 2;
            this.drumsCheck.Text = "Drums";
            this.drumsCheck.UseVisualStyleBackColor = true;
            this.drumsCheck.CheckedChanged += new System.EventHandler(this.onInstrumentOutputSelected);
            // 
            // vocalsCheck
            // 
            this.vocalsCheck.AutoSize = true;
            this.vocalsCheck.Checked = true;
            this.vocalsCheck.CheckState = System.Windows.Forms.CheckState.Checked;
            this.vocalsCheck.Location = new System.Drawing.Point(8, 59);
            this.vocalsCheck.Name = "vocalsCheck";
            this.vocalsCheck.Size = new System.Drawing.Size(83, 24);
            this.vocalsCheck.TabIndex = 1;
            this.vocalsCheck.Text = "Vocals";
            this.vocalsCheck.UseVisualStyleBackColor = true;
            this.vocalsCheck.CheckedChanged += new System.EventHandler(this.onInstrumentOutputSelected);
            // 
            // leadCheck
            // 
            this.leadCheck.AutoSize = true;
            this.leadCheck.Checked = true;
            this.leadCheck.CheckState = System.Windows.Forms.CheckState.Checked;
            this.leadCheck.Location = new System.Drawing.Point(9, 28);
            this.leadCheck.Name = "leadCheck";
            this.leadCheck.Size = new System.Drawing.Size(71, 24);
            this.leadCheck.TabIndex = 0;
            this.leadCheck.Text = "Lead";
            this.leadCheck.UseVisualStyleBackColor = true;
            this.leadCheck.CheckedChanged += new System.EventHandler(this.onInstrumentOutputSelected);
            // 
            // dataGridViewCheckBoxColumn1
            // 
            this.dataGridViewCheckBoxColumn1.DataPropertyName = "isSelected";
            this.dataGridViewCheckBoxColumn1.HeaderText = "Query Scores";
            this.dataGridViewCheckBoxColumn1.MinimumWidth = 8;
            this.dataGridViewCheckBoxColumn1.Name = "dataGridViewCheckBoxColumn1";
            this.dataGridViewCheckBoxColumn1.Width = 150;
            // 
            // dataGridViewTextBoxColumn3
            // 
            this.dataGridViewTextBoxColumn3.HeaderText = "Date Active";
            this.dataGridViewTextBoxColumn3.MinimumWidth = 8;
            this.dataGridViewTextBoxColumn3.Name = "dataGridViewTextBoxColumn3";
            this.dataGridViewTextBoxColumn3.Width = 150;
            // 
            // isSelected
            // 
            this.isSelected.DataPropertyName = "isSelected";
            this.isSelected.HeaderText = "Query Scores";
            this.isSelected.MinimumWidth = 8;
            this.isSelected.Name = "isSelected";
            this.isSelected.Width = 150;
            // 
            // inLocalData
            // 
            this.inLocalData.HeaderText = "Available Locally";
            this.inLocalData.MinimumWidth = 8;
            this.inLocalData.Name = "inLocalData";
            this.inLocalData.Width = 150;
            // 
            // tt
            // 
            this.tt.DataPropertyName = "track.tt";
            this.tt.HeaderText = "Title";
            this.tt.MinimumWidth = 8;
            this.tt.Name = "tt";
            this.tt.Width = 150;
            // 
            // Column2
            // 
            this.Column2.HeaderText = "Artist";
            this.Column2.MinimumWidth = 8;
            this.Column2.Name = "Column2";
            this.Column2.Width = 150;
            // 
            // DateActive
            // 
            this.DateActive.HeaderText = "Date Active";
            this.DateActive.MinimumWidth = 8;
            this.DateActive.Name = "DateActive";
            this.DateActive.Width = 150;
            // 
            // LD
            // 
            this.LD.HeaderText = "Lead Difficulty";
            this.LD.MinimumWidth = 8;
            this.LD.Name = "LD";
            this.LD.Width = 150;
            // 
            // BD
            // 
            this.BD.HeaderText = "Bass Difficulty";
            this.BD.MinimumWidth = 8;
            this.BD.Name = "BD";
            this.BD.Width = 150;
            // 
            // VD
            // 
            this.VD.HeaderText = "Vocals Difficulty";
            this.VD.MinimumWidth = 8;
            this.VD.Name = "VD";
            this.VD.Width = 150;
            // 
            // DD
            // 
            this.DD.HeaderText = "Drums Difficulty";
            this.DD.MinimumWidth = 8;
            this.DD.Name = "DD";
            this.DD.Width = 150;
            // 
            // PGD
            // 
            this.PGD.HeaderText = "Pro Lead Difficulty";
            this.PGD.MinimumWidth = 8;
            this.PGD.Name = "PGD";
            this.PGD.Width = 150;
            // 
            // PBD
            // 
            this.PBD.HeaderText = "Pro Bass Difficulty";
            this.PBD.MinimumWidth = 8;
            this.PBD.Name = "PBD";
            this.PBD.Width = 150;
            // 
            // su
            // 
            this.su.HeaderText = "Song ID";
            this.su.MinimumWidth = 8;
            this.su.Name = "su";
            this.su.Width = 150;
            // 
            // Form1
            // 
            this.AutoScaleDimensions = new System.Drawing.SizeF(9F, 20F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.ClientSize = new System.Drawing.Size(1668, 932);
            this.Controls.Add(this.tabControl1);
            this.FormBorderStyle = System.Windows.Forms.FormBorderStyle.FixedSingle;
            this.Name = "Form1";
            this.Text = "Fortnite Festival Score Tracker";
            this.tabControl1.ResumeLayout(false);
            this.tabPage1.ResumeLayout(false);
            this.tabPage1.PerformLayout();
            this.tabPage2.ResumeLayout(false);
            this.tabPage2.PerformLayout();
            ((System.ComponentModel.ISupportInitialize)(this.dataGridView1)).EndInit();
            this.tabPage4.ResumeLayout(false);
            this.tabPage4.PerformLayout();
            ((System.ComponentModel.ISupportInitialize)(this.dataGridView2)).EndInit();
            this.tabPage3.ResumeLayout(false);
            this.tabPage3.PerformLayout();
            this.ResumeLayout(false);

        }

        private void TextBox1_TextChanged(object sender, System.EventArgs e)
        {
            this.button2.Enabled = (textBox1.Text.Length != 0);
        }

        #endregion

        private System.Windows.Forms.TextBox textBox1;
        private System.Windows.Forms.Label label1;
        private System.Windows.Forms.Button button1;
        private System.Windows.Forms.Button button2;
        private System.Windows.Forms.ContextMenuStrip contextMenuStrip1;
        private System.Windows.Forms.TextBox textBox2;
        private System.Windows.Forms.Label label2;
        private System.Windows.Forms.TabControl tabControl1;
        private System.Windows.Forms.TabPage tabPage1;
        private System.Windows.Forms.TabPage tabPage2;
        private System.Windows.Forms.Label label4;
        private System.Windows.Forms.Label label3;
        private System.Windows.Forms.DataGridView dataGridView1;
        private System.Windows.Forms.DataGridViewTextBoxColumn Column1;
        private System.Windows.Forms.Button button4;
        private System.Windows.Forms.Button button3;
        private System.Windows.Forms.TabPage tabPage3;
        private System.Windows.Forms.CheckBox proBassCheck;
        private System.Windows.Forms.CheckBox proLeadCheck;
        private System.Windows.Forms.CheckBox bassCheck;
        private System.Windows.Forms.CheckBox drumsCheck;
        private System.Windows.Forms.CheckBox vocalsCheck;
        private System.Windows.Forms.CheckBox leadCheck;
        private System.Windows.Forms.RadioButton difficulty;
        private System.Windows.Forms.RadioButton percentage;
        private System.Windows.Forms.RadioButton score;
        private System.Windows.Forms.RadioButton artist;
        private System.Windows.Forms.RadioButton title;
        private System.Windows.Forms.RadioButton fullCombo;
        private System.Windows.Forms.Label label6;
        private System.Windows.Forms.Label label5;
        private System.Windows.Forms.RadioButton stars;
        private System.Windows.Forms.Label label7;
        private System.Windows.Forms.CheckBox invertOutput;
        private System.Windows.Forms.Button button5;
        private System.Windows.Forms.Label label8;
        private System.Windows.Forms.TextBox textBox3;
        private System.Windows.Forms.TabPage tabPage4;
        private System.Windows.Forms.Label label9;
        private System.Windows.Forms.TextBox textBox4;
        private System.Windows.Forms.DataGridView dataGridView2;
        private System.Windows.Forms.DataGridViewCheckBoxColumn dataGridViewCheckBoxColumn1;
        private System.Windows.Forms.DataGridViewTextBoxColumn dataGridViewTextBoxColumn3;
        private System.Windows.Forms.RadioButton radioButton2;
        private System.Windows.Forms.RadioButton radioButton1;
        private System.Windows.Forms.DataGridViewTextBoxColumn dataGridViewTextBoxColumn1;
        private System.Windows.Forms.DataGridViewTextBoxColumn dataGridViewTextBoxColumn2;
        private System.Windows.Forms.DataGridViewTextBoxColumn FullComboColumn;
        private System.Windows.Forms.DataGridViewTextBoxColumn StarsColumn;
        private System.Windows.Forms.DataGridViewTextBoxColumn ScoreColumn;
        private System.Windows.Forms.DataGridViewTextBoxColumn PercentageHitColumn;
        private System.Windows.Forms.DataGridViewTextBoxColumn SeasonAchievedColumn;
        private System.Windows.Forms.DataGridViewTextBoxColumn DifficultyColumn;
        private System.Windows.Forms.RadioButton radioButton6;
        private System.Windows.Forms.RadioButton radioButton5;
        private System.Windows.Forms.RadioButton radioButton4;
        private System.Windows.Forms.RadioButton radioButton3;
        private DataGridViewCheckBoxColumn isSelected;
        private DataGridViewTextBoxColumn inLocalData;
        private DataGridViewTextBoxColumn tt;
        private DataGridViewTextBoxColumn Column2;
        private DataGridViewTextBoxColumn DateActive;
        private DataGridViewTextBoxColumn LD;
        private DataGridViewTextBoxColumn BD;
        private DataGridViewTextBoxColumn VD;
        private DataGridViewTextBoxColumn DD;
        private DataGridViewTextBoxColumn PGD;
        private DataGridViewTextBoxColumn PBD;
        private DataGridViewTextBoxColumn su;
    }
}

