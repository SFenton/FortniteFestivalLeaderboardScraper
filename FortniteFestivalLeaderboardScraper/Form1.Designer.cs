using System.Windows.Forms;
using System;

namespace FortniteFestivalLeaderboardScraper
{
    partial class Form1
    {
        private System.ComponentModel.IContainer components = null;
        private TabControl mainTabControl;
        private TabPage processTab;
        private TabPage songsTab;
        private TabPage scoresTab;
        private TabPage optionsTab;
        private UI.Views.ProcessView processView;
        private UI.Views.SongSelectView songSelectView;
        private UI.Views.ScoreViewerView scoreViewerView;
        private Label dopLabel;
        private NumericUpDown dopNumeric;

        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null)) components.Dispose();
            base.Dispose(disposing);
        }

        private void InitializeComponent()
        {
            this.components = new System.ComponentModel.Container();
            this.mainTabControl = new System.Windows.Forms.TabControl();
            this.processTab = new System.Windows.Forms.TabPage();
            this.songsTab = new System.Windows.Forms.TabPage();
            this.scoresTab = new System.Windows.Forms.TabPage();
            this.optionsTab = new System.Windows.Forms.TabPage();
            this.processView = new UI.Views.ProcessView();
            this.songSelectView = new UI.Views.SongSelectView();
            this.scoreViewerView = new UI.Views.ScoreViewerView();
            this.dopLabel = new System.Windows.Forms.Label();
            this.dopNumeric = new System.Windows.Forms.NumericUpDown();
            this.mainTabControl.SuspendLayout();
            this.processTab.SuspendLayout();
            this.songsTab.SuspendLayout();
            this.scoresTab.SuspendLayout();
            this.optionsTab.SuspendLayout();
            ((System.ComponentModel.ISupportInitialize)(this.dopNumeric)).BeginInit();
            this.SuspendLayout();
            this.FormClosed += OnMainWindowClosing;
            this.Load += OnMainWindowLoad;
            // mainTabControl
            this.mainTabControl.Controls.Add(this.processTab);
            this.mainTabControl.Controls.Add(this.songsTab);
            this.mainTabControl.Controls.Add(this.scoresTab);
            this.mainTabControl.Controls.Add(this.optionsTab);
            this.mainTabControl.Dock = DockStyle.Fill;
            this.mainTabControl.Name = "mainTabControl";
            this.mainTabControl.SelectedIndex = 0;
            this.mainTabControl.Size = new System.Drawing.Size(1668, 932);
            // processTab
            this.processTab.Controls.Add(this.processView);
            this.processTab.Location = new System.Drawing.Point(4, 29);
            this.processTab.Name = "processTab";
            this.processTab.Padding = new Padding(3);
            this.processTab.Size = new System.Drawing.Size(1660, 899);
            this.processTab.Text = "Process";
            this.processTab.UseVisualStyleBackColor = true;
            // songsTab
            this.songsTab.Controls.Add(this.songSelectView);
            this.songsTab.Location = new System.Drawing.Point(4, 29);
            this.songsTab.Name = "songsTab";
            this.songsTab.Padding = new Padding(3);
            this.songsTab.Size = new System.Drawing.Size(1660, 899);
            this.songsTab.Text = "Songs";
            this.songsTab.UseVisualStyleBackColor = true;
            // scoresTab
            this.scoresTab.Controls.Add(this.scoreViewerView);
            this.scoresTab.Location = new System.Drawing.Point(4, 29);
            this.scoresTab.Name = "scoresTab";
            this.scoresTab.Padding = new Padding(3);
            this.scoresTab.Size = new System.Drawing.Size(1660, 899);
            this.scoresTab.Text = "Scores";
            this.scoresTab.UseVisualStyleBackColor = true;
            // optionsTab
            this.optionsTab.Controls.Add(this.dopLabel);
            this.optionsTab.Controls.Add(this.dopNumeric);
            this.optionsTab.Location = new System.Drawing.Point(4, 29);
            this.optionsTab.Name = "optionsTab";
            this.optionsTab.Padding = new Padding(3);
            this.optionsTab.Size = new System.Drawing.Size(1660, 899);
            this.optionsTab.Text = "Options";
            this.optionsTab.UseVisualStyleBackColor = true;
            // processView
            this.processView.Dock = DockStyle.Fill;
            this.processView.Name = "processView";
            // songSelectView
            this.songSelectView.Dock = DockStyle.Fill;
            this.songSelectView.Name = "songSelectView";
            // scoreViewerView
            this.scoreViewerView.Dock = DockStyle.Fill;
            this.scoreViewerView.Name = "scoreViewerView";
            // dopLabel
            this.dopLabel.AutoSize = true;
            this.dopLabel.Text = "Concurrent Requests";
            this.dopLabel.Left = 24;
            this.dopLabel.Top = 32;
            // dopNumeric
            this.dopNumeric.Left = 200;
            this.dopNumeric.Top = 28;
            this.dopNumeric.Minimum = 1;
            this.dopNumeric.Maximum = 48;
            this.dopNumeric.Value = 16;
            this.dopNumeric.Width = 80;
            this.dopNumeric.ValueChanged += (s,e)=> this.OnDopChanged();
            // Form1
            this.AutoScaleDimensions = new System.Drawing.SizeF(9F, 20F);
            this.AutoScaleMode = AutoScaleMode.Font;
            this.ClientSize = new System.Drawing.Size(1668, 932);
            this.Controls.Add(this.mainTabControl);
            this.FormBorderStyle = FormBorderStyle.FixedSingle;
            this.Name = "Form1";
            this.Text = "Fortnite Festival Score Tracker";
            this.mainTabControl.ResumeLayout(false);
            this.processTab.ResumeLayout(false);
            this.songsTab.ResumeLayout(false);
            this.scoresTab.ResumeLayout(false);
            this.optionsTab.ResumeLayout(false);
            this.optionsTab.PerformLayout();
            ((System.ComponentModel.ISupportInitialize)(this.dopNumeric)).EndInit();
            this.ResumeLayout(false);
        }
    }
}
