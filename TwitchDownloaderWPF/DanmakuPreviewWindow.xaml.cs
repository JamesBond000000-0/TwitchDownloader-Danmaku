using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media.Imaging;
using TwitchDownloaderCore;
using TwitchDownloaderCore.Interfaces;
using TwitchDownloaderCore.Options;

namespace TwitchDownloaderWPF
{
    /// <summary>
    /// Shows a single danmaku frame rendered with the Chat Render page's current
    /// settings, so danmaku options (scroll speed, lanes, visibility, highlight
    /// badges and colors) can be tuned while looking at the result. The options
    /// factory is the page's GetOptions, so every Update re-reads the live
    /// control values from the main window.
    /// </summary>
    public partial class DanmakuPreviewWindow : Window
    {
        private readonly Func<ChatRenderOptions> _optionsFactory;
        private bool _busy;

        public DanmakuPreviewWindow(Func<ChatRenderOptions> optionsFactory, double time)
        {
            InitializeComponent();
            _optionsFactory = optionsFactory;
            textTime.Text = time.ToString(CultureInfo.CurrentCulture);
        }

        public void SetTime(double time)
        {
            textTime.Text = time.ToString(CultureInfo.CurrentCulture);
        }

        private async void BtnUpdate_Click(object sender, RoutedEventArgs e)
        {
            await UpdatePreviewAsync();
        }

        public async Task UpdatePreviewAsync()
        {
            if (_busy)
                return;

            if (!double.TryParse(textTime.Text, CultureInfo.CurrentCulture, out var time) || time < 0)
            {
                textStatus.Text = "Invalid time.";
                return;
            }

            _busy = true;
            btnUpdate.IsEnabled = false;
            textStatus.Text = "Rendering...";
            try
            {
                var options = _optionsFactory();
                var outputDir = Path.GetDirectoryName(options.OutputFile);
                if (!string.IsNullOrEmpty(outputDir))
                    Directory.CreateDirectory(outputDir);

                using var renderer = new ChatRenderer(options, new PreviewProgress(this));
                await renderer.ParseJsonAsync(CancellationToken.None);
                await renderer.RenderDanmakuFrameAsync(time, options.DanmakuScrollDuration, options.ChatWidth, options.ChatHeight, 0, options.DanmakuSpeedMode, options.DanmakuMaxLanes, CancellationToken.None);

                var bitmap = new BitmapImage();
                using (var stream = new FileStream(options.OutputFile, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    bitmap.BeginInit();
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.StreamSource = stream;
                    bitmap.EndInit();
                }
                bitmap.Freeze();
                imgPreview.Source = bitmap;
                textStatus.Text = $"Frame at {time.ToString(CultureInfo.CurrentCulture)}s. Change settings in the main window and press Update to re-render.";
            }
            catch (Exception ex)
            {
                textStatus.Text = "Preview failed: " + ex.Message;
            }
            finally
            {
                _busy = false;
                btnUpdate.IsEnabled = true;
            }
        }

        private sealed class PreviewProgress : ITaskProgress
        {
            private readonly DanmakuPreviewWindow _window;

            public PreviewProgress(DanmakuPreviewWindow window)
            {
                _window = window;
            }

            public void SetStatus(string status)
            {
                _window.Dispatcher.InvokeAsync(() => _window.textStatus.Text = status);
            }

            public void SetTemplateStatus(string statusTemplate, int initialPercent) { }
            public void SetTemplateStatus(string statusTemplate, int initialPercent, TimeSpan initialTime1, TimeSpan initialTime2) { }
            public void ReportProgress(int percent) { }
            public void ReportProgress(int percent, TimeSpan time1, TimeSpan time2) { }
            public void LogVerbose(string logMessage) { }
            public void LogVerbose(DefaultInterpolatedStringHandler logMessage) { }
            public void LogInfo(string logMessage) { }
            public void LogInfo(DefaultInterpolatedStringHandler logMessage) { }
            public void LogWarning(string logMessage) { }
            public void LogWarning(DefaultInterpolatedStringHandler logMessage) { }
            public void LogError(string logMessage) { }
            public void LogError(DefaultInterpolatedStringHandler logMessage) { }
            public void LogFfmpeg(string logMessage) { }
        }
    }
}
