using System;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace NientumStudio.Dialogs
{
    public class NewSongDialog : Window
    {
        public string SongId { get; private set; }
        public string SongTitle { get; private set; }
        public string Artist { get; private set; }
        public float Bpm { get; private set; } = 140f;
        public string AudioSourcePath { get; private set; }

        private readonly TextBox _idBox = new TextBox { Text = "my_song" };
        private readonly TextBox _titleBox = new TextBox { Text = "My Custom Song" };
        private readonly TextBox _artistBox = new TextBox { Text = "Artist" };
        private readonly TextBox _bpmBox = new TextBox { Text = "140" };
        private readonly TextBox _audioBox = new TextBox { IsReadOnly = true, Text = "" };

        public NewSongDialog()
        {
            Title = "新建自定义曲目";
            Width = 450;
            MinWidth = 400;
            SizeToContent = SizeToContent.Height;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.CanResizeWithGrip;
            SetResourceReference(BackgroundProperty, "BgPanel");
            SetResourceReference(ForegroundProperty, "TextPrimary");

            var dock = new DockPanel { LastChildFill = true };

            // 固定在底部的操作栏，任何 DPI / 字号下都不会被裁切
            var footer = new Border
            {
                BorderThickness = new Thickness(0, 1, 0, 0),
                Padding = new Thickness(22, 12, 22, 16)
            };
            footer.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
            var btnRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var cancel = new Button { Content = "取消", Width = 92, Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(0, 0, 10, 0), IsCancel = true };
            var ok = new Button { Content = "确认创建", Width = 104, Padding = new Thickness(10, 6, 10, 6), IsDefault = true };
            if (TryFindResource("PrimaryButton") is Style primaryStyle) ok.Style = primaryStyle;
            cancel.Click += (_, _) => DialogResult = false;
            ok.Click += (_, _) =>
            {
                string id = _idBox.Text.Trim();
                if (string.IsNullOrWhiteSpace(id)) id = "my_song";
                string title = _titleBox.Text.Trim();
                if (string.IsNullOrWhiteSpace(title)) title = id;
                string artist = _artistBox.Text.Trim();
                if (string.IsNullOrWhiteSpace(artist)) artist = "Unknown";
                if (!float.TryParse(_bpmBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out float b) || b < 20 || b > 400) b = 140f;

                SongId = id;
                SongTitle = title;
                Artist = artist;
                Bpm = b;
                AudioSourcePath = File.Exists(_audioBox.Text.Trim()) ? _audioBox.Text.Trim() : null;
                DialogResult = true;
            };
            btnRow.Children.Add(cancel);
            btnRow.Children.Add(ok);
            footer.Child = btnRow;
            DockPanel.SetDock(footer, Dock.Bottom);
            dock.Children.Add(footer);

            var sp = new StackPanel { Margin = new Thickness(22, 18, 22, 14) };
            sp.Children.Add(MakeRow("曲目 ID (英文/数字/下划线):", _idBox));
            sp.Children.Add(MakeRow("曲名 (Title):", _titleBox));
            sp.Children.Add(MakeRow("曲师 (Artist):", _artistBox));
            sp.Children.Add(MakeRow("初始 BPM:", _bpmBox));

            // 可选音频导入行
            var audioRow = new StackPanel { Margin = new Thickness(0, 0, 0, 4) };
            var audioLbl = new TextBlock { Text = "曲目音频（可选，支持 OGG / MP3 / WAV / FLAC）:", FontSize = 11.5, Margin = new Thickness(0, 0, 0, 5) };
            audioLbl.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
            audioRow.Children.Add(audioLbl);
            var audioGrid = new Grid();
            audioGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            audioGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            _audioBox.Padding = new Thickness(8, 5, 8, 5);
            _audioBox.Margin = new Thickness(0, 0, 8, 0);
            _audioBox.ToolTip = "留空也可先创建曲目，稍后再导入或拖拽音频";
            var browseBtn = new Button { Content = "选择音频...", Padding = new Thickness(10, 5, 10, 5) };
            browseBtn.Click += (_, _) =>
            {
                var dlg = new OpenFileDialog
                {
                    Title = "选择曲目音频（支持 OGG / MP3 / WAV / FLAC）",
                    Filter = "支持的音频文件 (*.ogg;*.mp3;*.wav;*.flac)|*.ogg;*.mp3;*.wav;*.flac|OGG 音频|*.ogg|MP3 音频|*.mp3|WAV 无损|*.wav|FLAC 无损|*.flac"
                };
                if (dlg.ShowDialog(this) == true)
                    _audioBox.Text = dlg.FileName;
            };
            Grid.SetColumn(_audioBox, 0);
            Grid.SetColumn(browseBtn, 1);
            audioGrid.Children.Add(_audioBox);
            audioGrid.Children.Add(browseBtn);
            audioRow.Children.Add(audioGrid);
            sp.Children.Add(audioRow);

            dock.Children.Add(sp);
            Content = dock;
        }

        private static StackPanel MakeRow(string label, TextBox box)
        {
            var p = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
            var tb = new TextBlock { Text = label, FontSize = 11.5, Margin = new Thickness(0, 0, 0, 5) };
            tb.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
            p.Children.Add(tb);
            box.Padding = new Thickness(8, 5, 8, 5);
            p.Children.Add(box);
            return p;
        }
    }
}
