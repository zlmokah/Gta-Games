using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Reflection;
using System.Media;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Newtonsoft.Json;

namespace GTA_Games
{
    public partial class Form1 : Form
    {
        private const string ResourceName = "GTA_Games.UI.index.html";

        private SoundPlayer soundPlayer;
        private bool musicEnabled = true;
        private bool isClosing;

        private readonly Dictionary<int, DownloadInfo> activeDownloads =
            new Dictionary<int, DownloadInfo>();

        [DllImport("user32.dll")]
        private static extern IntPtr FindWindow(
            string className,
            string windowName);

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(
            IntPtr hwnd,
            int attr,
            ref int attrValue,
            int attrSize);

        [DllImport("user32.dll")]
        private static extern int SendMessage(
            IntPtr hWnd,
            int Msg,
            int wParam,
            int lParam);

        [DllImport("user32.dll")]
        private static extern bool ReleaseCapture();

        private const int WM_NCLBUTTONDOWN = 0xA1;
        private const int HT_CAPTION = 0x2;

        private class DownloadInfo
        {
            public int Id;
            public string GameName;
            public string Url;
            public string FilePath;
            public string PartFilePath;

            public long TotalBytes;
            public long ReceivedBytes;

            public Stopwatch Stopwatch;

            public bool Finished;
            public bool Failed;
            public bool Paused;
            public bool Cancelled;

            public CancellationTokenSource Cancellation;
        }

        public Form1()
        {
            InitializeComponent();

            FormBorderStyle = FormBorderStyle.None;
            BackColor = Color.FromArgb(10, 0, 20);
            StartPosition = FormStartPosition.CenterScreen;

            Load += Form1_Load;
        }

        private async void Form1_Load(
            object sender,
            EventArgs e)
        {
            try
            {
                await webView.EnsureCoreWebView2Async(null);

                webView.CoreWebView2.WebMessageReceived +=
                    CoreWebView2_WebMessageReceived;

                webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
                webView.CoreWebView2.Settings.AreDevToolsEnabled = false;
                webView.CoreWebView2.Settings.IsStatusBarEnabled = false;
                webView.CoreWebView2.Settings.AreBrowserAcceleratorKeysEnabled = false;

                webView.CoreWebView2.NavigateToString(
                    ReadEmbeddedHtml());

                PlayStartupMusic();
                SetTaskbarColorToBlack();
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    "Failed to start:\n\n" + ex.Message,
                    "GTA Games",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
        }

        private void CoreWebView2_WebMessageReceived(
            object sender,
            CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                if (isClosing)
                    return;

                string json = e.WebMessageAsJson;

                if (string.IsNullOrWhiteSpace(json))
                    return;

                if (json == "\"drag\"")
                {
                    BeginWindowDrag();
                    return;
                }

                Dictionary<string, object> obj =
                    JsonConvert.DeserializeObject<Dictionary<string, object>>(json);

                if (obj == null || !obj.ContainsKey("type"))
                    return;

                string type =
                    obj["type"]?.ToString();

                if (string.Equals(
                    type,
                    "download",
                    StringComparison.OrdinalIgnoreCase))
                {
                    string url =
                        obj.ContainsKey("url")
                            ? obj["url"]?.ToString()
                            : obj.ContainsKey("magnet")
                                ? obj["magnet"]?.ToString()
                                : null;

                    string gameName =
                        obj.ContainsKey("gameName")
                            ? obj["gameName"]?.ToString()
                            : null;

                    int downloadId = 0;

                    if (obj.ContainsKey("downloadId"))
                    {
                        int.TryParse(
                            obj["downloadId"]?.ToString(),
                            out downloadId);
                    }

                    if (!string.IsNullOrWhiteSpace(url) &&
                        !string.IsNullOrWhiteSpace(gameName) &&
                        downloadId > 0)
                    {
                        StartDownload(
                            url,
                            gameName,
                            downloadId);
                    }

                    return;
                }

                if (string.Equals(type, "cancelDownload",
                    StringComparison.OrdinalIgnoreCase))
                {
                    int id = GetId(obj);

                    if (id > 0)
                        CancelDownload(id);

                    return;
                }

                if (string.Equals(type, "pauseDownload",
                    StringComparison.OrdinalIgnoreCase))
                {
                    int id = GetId(obj);

                    if (id > 0)
                        PauseDownload(id);

                    return;
                }

                if (string.Equals(type, "resumeDownload",
                    StringComparison.OrdinalIgnoreCase))
                {
                    int id = GetId(obj);

                    if (id > 0)
                        ResumeDownload(id);

                    return;
                }

                if (string.Equals(type, "toggleMusic",
                    StringComparison.OrdinalIgnoreCase))
                {
                    ToggleMusic();
                    return;
                }

                if (string.Equals(type, "close",
                    StringComparison.OrdinalIgnoreCase))
                {
                    BeginInvoke(new Action(() =>
                    {
                        Close();
                    }));

                    return;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine(
                    "WebMessage error: " + ex);
            }
        }

        private int GetId(
            Dictionary<string, object> obj)
        {
            if (!obj.ContainsKey("downloadId"))
                return 0;

            int id;

            int.TryParse(
                obj["downloadId"]?.ToString(),
                out id);

            return id;
        }

        private void BeginWindowDrag()
        {
            try
            {
                if (isClosing || !IsHandleCreated)
                    return;

                ReleaseCapture();

                SendMessage(
                    Handle,
                    WM_NCLBUTTONDOWN,
                    HT_CAPTION,
                    0);
            }
            catch
            {
            }
        }
        private static string MakeSafeFileName(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName))
                return "download";

            foreach (char c in Path.GetInvalidFileNameChars())
            {
                fileName = fileName.Replace(c, '_');
            }

            return fileName.Trim();
        }
        private async void StartDownload(
            string url,
            string gameName,
            int downloadId)
        {
            DownloadInfo info = null;

            try
            {
                lock (activeDownloads)
                {
                    if (activeDownloads.ContainsKey(downloadId))
                        return;
                }

                using (SaveFileDialog dialog =
                       new SaveFileDialog())
                {
                    dialog.Title = "Save Download As";
                    dialog.FileName =
                        MakeSafeFileName(gameName) + ".zip";
                    dialog.Filter =
                        "ZIP files (*.zip)|*.zip|All files (*.*)|*.*";
                    dialog.RestoreDirectory = true;
                    dialog.OverwritePrompt = true;

                    if (dialog.ShowDialog(this) !=
                        DialogResult.OK)
                    {
                        SendDownloadProgress(
                            downloadId,
                            "error",
                            0,
                            0,
                            0,
                            0,
                            0);

                        return;
                    }

                    string finalPath =
                        dialog.FileName;

                    string partPath =
                        finalPath + ".part";

                    info = new DownloadInfo
                    {
                        Id = downloadId,
                        GameName = gameName,
                        Url = url,
                        FilePath = finalPath,
                        PartFilePath = partPath,
                        TotalBytes = 0,
                        ReceivedBytes = 0,
                        Stopwatch =
                            Stopwatch.StartNew(),
                        Finished = false,
                        Failed = false,
                        Paused = false,
                        Cancelled = false,
                        Cancellation =
                            new CancellationTokenSource()
                    };

                    if (File.Exists(partPath))
                    {
                        try
                        {
                            info.ReceivedBytes =
                                new FileInfo(partPath).Length;
                        }
                        catch
                        {
                            info.ReceivedBytes = 0;
                        }
                    }

                    lock (activeDownloads)
                    {
                        activeDownloads[downloadId] =
                            info;
                    }

                    SendProgressUpdate(info);

                    await DownloadWorkerAsync(info);
                }
            }
            catch (OperationCanceledException)
            {
                if (info == null)
                    return;

                if (info.Paused)
                {
                    SendProgressUpdate(info);
                    return;
                }

                if (info.Cancelled)
                    return;

                info.Failed = true;

                try
                {
                    info.Stopwatch?.Stop();
                }
                catch
                {
                }

                SendDownloadProgress(
                    info.Id,
                    "error",
                    CalculateProgress(info),
                    info.ReceivedBytes,
                    info.TotalBytes,
                    0,
                    CalculateRemaining(info));

                RemoveDownload(info.Id);
            }
            catch (Exception ex)
            {
                Debug.WriteLine(
                    "Download error: " + ex);

                if (info != null)
                {
                    info.Failed = true;

                    try
                    {
                        info.Stopwatch?.Stop();
                    }
                    catch
                    {
                    }

                    SendDownloadProgress(
                        info.Id,
                        "error",
                        CalculateProgress(info),
                        info.ReceivedBytes,
                        info.TotalBytes,
                        0,
                        CalculateRemaining(info));

                    RemoveDownload(info.Id);
                }

                SendMessageToWebView(
                    "Download failed: " +
                    ex.Message,
                    false);
            }
        }

        private async Task DownloadWorkerAsync(
            DownloadInfo info)
        {
            CancellationTokenSource source =
                info.Cancellation;

            try
            {
                await DownloadFileAsync(
                    info,
                    source.Token);
            }
            catch (OperationCanceledException)
            {
                if (!ReferenceEquals(
                    source,
                    info.Cancellation))
                    return;

                if (info.Cancelled ||
                    info.Paused)
                    return;

                info.Failed = true;

                SendDownloadProgress(
                    info.Id,
                    "error",
                    CalculateProgress(info),
                    info.ReceivedBytes,
                    info.TotalBytes,
                    0,
                    CalculateRemaining(info));
                RemoveDownload(info.Id);
            }
            catch (WebException ex)
            {
                if (!ReferenceEquals(
                    source,
                    info.Cancellation))
                    return;

                if (info.Cancelled ||
                    info.Paused)
                    return;

                info.Failed = true;

                Debug.WriteLine(
                    "Web error: " + ex);

                SendDownloadProgress(
                    info.Id,
                    "error",
                    CalculateProgress(info),
                    info.ReceivedBytes,
                    info.TotalBytes,
                    0,
                    CalculateRemaining(info));
                RemoveDownload(info.Id);

                SendMessageToWebView(
                    "Download failed: " +
                    ex.Message,
                    false);
            }
            catch (Exception ex)
            {
                if (!ReferenceEquals(
                    source,
                    info.Cancellation))
                    return;

                if (info.Cancelled ||
                    info.Paused)
                    return;

                info.Failed = true;

                Debug.WriteLine(
                    "Download error: " + ex);

                SendDownloadProgress(
                    info.Id,
                    "error",
                    CalculateProgress(info),
                    info.ReceivedBytes,
                    info.TotalBytes,
                    0,
                    CalculateRemaining(info));
                RemoveDownload(info.Id);

                SendMessageToWebView(
                    "Download failed: " +
                    ex.Message,
                    false);
            }
            finally
            {
                if (!info.Finished)
                {
                    try
                    {
                        source.Dispose();
                    }
                    catch
                    {
                    }
                }
            }
        }

        private async Task DownloadFileAsync(
            DownloadInfo info,
            CancellationToken token)
        {
            long existingBytes = 0;

            if (File.Exists(info.PartFilePath))
            {
                existingBytes =
                    new FileInfo(
                        info.PartFilePath).Length;
            }

            info.ReceivedBytes =
                existingBytes;

            bool retriedWithoutRange = false;

            while (true)
            {
                token.ThrowIfCancellationRequested();

                HttpWebRequest request =
                    (HttpWebRequest)WebRequest.Create(
                        info.Url);

                request.Method = "GET";
                request.AllowAutoRedirect = true;
                request.MaximumAutomaticRedirections = 20;

                request.UserAgent =
                    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) " +
                    "AppleWebKit/537.36 " +
                    "(KHTML, like Gecko) " +
                    "Chrome/131.0.0.0 Safari/537.36";

                request.Accept =
                    "application/octet-stream,*/*";

                request.AutomaticDecompression =
                    DecompressionMethods.None;

                request.Timeout = 30000;
                request.ReadWriteTimeout = 30000;

                if (existingBytes > 0)
                    request.AddRange(existingBytes);

                using (token.Register(() =>
                {
                    try
                    {
                        request.Abort();
                    }
                    catch
                    {
                    }
                }))
                using (WebResponse response =
                       await request.GetResponseAsync())
                {
                    token.ThrowIfCancellationRequested();

                    HttpWebResponse httpResponse =
                        response as HttpWebResponse;

                    bool resumed =
                        existingBytes > 0 &&
                        httpResponse != null &&
                        httpResponse.StatusCode ==
                        HttpStatusCode.PartialContent;

                    if (existingBytes > 0 &&
                        !resumed)
                    {
                        if (retriedWithoutRange)
                        {
                            throw new IOException(
                                "The download server does not support resuming this file.");
                        }

                        retriedWithoutRange = true;

                        TryDeleteFile(
                            info.PartFilePath);

                        existingBytes = 0;
                        info.ReceivedBytes = 0;
                        info.TotalBytes = 0;

                        continue;
                    }

                    long contentLength =
                        response.ContentLength;

                    if (resumed)
                    {
                        info.TotalBytes =
                            contentLength > 0
                                ? existingBytes +
                                  contentLength
                                : 0;
                    }
                    else if (contentLength > 0)
                    {
                        info.TotalBytes =
                            contentLength;
                    }

                    using (Stream input =
                           response.GetResponseStream())
                    {
                        if (input == null)
                        {
                            throw new IOException(
                                "The download server returned an empty stream.");
                        }

                        FileMode mode =
                            existingBytes > 0
                                ? FileMode.Append
                                : FileMode.Create;

                        using (FileStream output =
                               new FileStream(
                                   info.PartFilePath,
                                   mode,
                                   FileAccess.Write,
                                   FileShare.Read,
                                   1024 * 128,
                                   true))
                        {
                            byte[] buffer =
                                new byte[1024 * 128];

                            long lastUpdateBytes =
                                info.ReceivedBytes;

                            long lastUpdateTime =
                                info.Stopwatch.ElapsedMilliseconds;

                            int read;

                            while ((read =
                                    await input.ReadAsync(
                                        buffer,
                                        0,
                                        buffer.Length,
                                        token)) > 0)
                            {
                                token.ThrowIfCancellationRequested();

                                await output.WriteAsync(
                                    buffer,
                                    0,
                                    read,
                                    token);

                                info.ReceivedBytes +=
                                    read;

                                long now =
                                    info.Stopwatch.ElapsedMilliseconds;

                                bool enoughBytes =
                                    info.ReceivedBytes -
                                    lastUpdateBytes >=
                                    256 * 1024;

                                bool enoughTime =
                                    now -
                                    lastUpdateTime >=
                                    250;

                                if (enoughBytes ||
                                    enoughTime)
                                {
                                    SendProgressUpdate(
                                        info);

                                    lastUpdateBytes =
                                        info.ReceivedBytes;

                                    lastUpdateTime =
                                        now;
                                }
                            }

                            await output.FlushAsync(
                                token);
                        }
                    }
                }

                break;
            }

            token.ThrowIfCancellationRequested();

            SendProgressUpdate(info);

            if (info.TotalBytes > 0 &&
                info.ReceivedBytes != info.TotalBytes)
            {
                throw new IOException(
                    "The download ended before the expected file size was received.");
            }

            if (!File.Exists(
                info.PartFilePath))
            {
                throw new FileNotFoundException(
                    "Downloaded file was not found.");
            }

            if (File.Exists(info.FilePath))
                TryDeleteFile(info.FilePath);

            File.Move(
                info.PartFilePath,
                info.FilePath);

            info.Finished = true;
            info.Paused = false;
            info.Stopwatch.Stop();

            long completedTotal =
                info.TotalBytes > 0
                    ? info.TotalBytes
                    : info.ReceivedBytes;

            SendDownloadProgress(
                info.Id,
                "completed",
                100,
                info.ReceivedBytes,
                completedTotal,
                0,
                0);

            SendMessageToWebView(
                "Download completed: " +
                info.GameName,
                true);

            lock (activeDownloads)
            {
                activeDownloads.Remove(
                    info.Id);
            }

            try
            {
                info.Cancellation.Dispose();
            }
            catch
            {
            }
        }

        private void PauseDownload(
            int id)
        {
            DownloadInfo info = null;

            lock (activeDownloads)
            {
                if (!activeDownloads.TryGetValue(
                    id,
                    out info))
                    return;

                if (info.Finished ||
                    info.Cancelled ||
                    info.Paused)
                    return;

                info.Paused = true;
                info.Stopwatch.Stop();

                try
                {
                    info.Cancellation?.Cancel();
                }
                catch
                {
                }

                SendDownloadProgress(
                    info.Id,
                    "paused",
                    CalculateProgress(info),
                    info.ReceivedBytes,
                    info.TotalBytes,
                    CalculateSpeed(info),
                    CalculateRemaining(info));
            }
        }

        private void ResumeDownload(
            int id)
        {
            DownloadInfo info = null;

            lock (activeDownloads)
            {
                if (!activeDownloads.TryGetValue(
                    id,
                    out info))
                    return;

                if (info.Finished ||
                    info.Cancelled ||
                    !info.Paused)
                    return;

                info.Paused = false;
                info.Stopwatch.Start();

                info.Cancellation =
                    new CancellationTokenSource();

                SendDownloadProgress(
                    info.Id,
                    "downloading",
                    CalculateProgress(info),
                    info.ReceivedBytes,
                    info.TotalBytes,
                    CalculateSpeed(info),
                    CalculateRemaining(info));

                _ = DownloadWorkerAsync(info);
            }
        }

        private void CancelDownload(
            int id)
        {
            DownloadInfo info = null;

            lock (activeDownloads)
            {
                if (!activeDownloads.TryGetValue(
                    id,
                    out info))
                    return;

                info.Cancelled = true;
                info.Paused = false;

                try
                {
                    info.Cancellation.Cancel();
                }
                catch
                {
                }

                try
                {
                    info.Stopwatch?.Stop();
                }
                catch
                {
                }

                activeDownloads.Remove(id);
            }

            TryDeleteFile(
                info.PartFilePath);

            SendDownloadProgress(
                id,
                "cancelled",
                0,
                0,
                info.TotalBytes,
                0,
                0);
        }

        private void RemoveDownload(
            int id)
        {
            lock (activeDownloads)
            {
                activeDownloads.Remove(id);
            }
        }

        private int CalculateProgress(
            DownloadInfo info)
        {
            if (info == null ||
                info.TotalBytes <= 0)
                return 0;

            return Math.Max(
                0,
                Math.Min(
                    100,
                    (int)Math.Round(
                        info.ReceivedBytes *
                        100.0 /
                        info.TotalBytes)));
        }

        private double CalculateSpeed(
            DownloadInfo info)
        {
            if (info == null ||
                info.Stopwatch == null)
                return 0;

            double seconds =
                Math.Max(
                    info.Stopwatch.Elapsed.TotalSeconds,
                    0.001);

            return info.ReceivedBytes / seconds;
        }

        private long CalculateRemaining(
            DownloadInfo info)
        {
            if (info == null ||
                info.TotalBytes <= 0)
                return 0;

            return Math.Max(
                0,
                info.TotalBytes -
                info.ReceivedBytes);
        }

        private void SendProgressUpdate(
            DownloadInfo info)
        {
            if (info == null ||
                isClosing ||
                info.Cancelled)
                return;

            int progress =
                CalculateProgress(info);

            double speed =
                CalculateSpeed(info);

            long remaining =
                CalculateRemaining(info);

            string status =
                info.Paused
                    ? "paused"
                    : "downloading";

            SendDownloadProgress(
                info.Id,
                status,
                progress,
                info.ReceivedBytes,
                info.TotalBytes,
                speed,
                remaining);
        }

        private void SendDownloadProgress(
            int downloadId,
            string status,
            int progress,
            long bytesReceived,
            long totalBytes,
            double speed,
            long remaining)
        {
            try
            {
                if (isClosing ||
                    webView == null ||
                    webView.CoreWebView2 == null)
                    return;

                string totalText =
                    totalBytes > 0
                        ? FormatSize(totalBytes)
                        : "Unknown";

                string remainingText =
                    totalBytes > 0
                        ? FormatSize(
                            Math.Max(
                                0,
                                remaining))
                        : "Unknown";

                string speedText =
                    speed > 0
                        ? FormatSize(
                            (long)speed) + "/s"
                        : "0 KB/s";

                var data = new
                {
                    type = "downloadProgress",
                    downloadId = downloadId,
                    status = status,
                    progress = progress,
                    bytesReceived = bytesReceived,
                    totalBytes = totalBytes,
                    remaining = remaining,
                    speed = speed,
                    size = FormatSize(bytesReceived),
                    totalSize = totalText,
                    remainingSize = remainingText,
                    speedText = speedText
                };

                webView.CoreWebView2.PostWebMessageAsJson(
                    JsonConvert.SerializeObject(data));
            }
            catch
            {
            }
        }

        private string FormatSize(
            long bytes)
        {
            if (bytes <= 0)
                return "0 B";

            string[] sizes =
            {
                "B",
                "KB",
                "MB",
                "GB",
                "TB"
            };

            double value = bytes;
            int order = 0;

            while (
                value >= 1024 &&
                order < sizes.Length - 1)
            {
                value /= 1024;
                order++;
            }

            return $"{value:0.##} {sizes[order]}";
        }

        private void SendMessageToWebView(
            string message,
            bool success)
        {
            try
            {
                if (isClosing ||
                    webView == null ||
                    webView.CoreWebView2 == null)
                    return;

                var data = new
                {
                    type = "downloadStatus",
                    message = message,
                    success = success
                };

                webView.CoreWebView2.PostWebMessageAsJson(
                    JsonConvert.SerializeObject(data));
            }
            catch
            {
            }
        }

        private void ToggleMusic()
        {
            try
            {
                musicEnabled = !musicEnabled;

                if (musicEnabled)
                {
                    if (soundPlayer == null)
                    {
                        PlayStartupMusic();
                    }
                    else
                    {
                        soundPlayer.PlayLooping();
                    }
                }
                else
                {
                    soundPlayer?.Stop();
                }

                if (!isClosing &&
                    webView?.CoreWebView2 != null)
                {
                    webView.CoreWebView2.PostWebMessageAsJson(
                        JsonConvert.SerializeObject(
                            new
                            {
                                type = "musicState",
                                enabled = musicEnabled
                            }));
                }
            }
            catch
            {
            }
        }

        private void SetTaskbarColorToBlack()
        {
            try
            {
                IntPtr taskbarHandle =
                    FindWindow(
                        "Shell_TrayWnd",
                        null);

                if (taskbarHandle != IntPtr.Zero)
                {
                    int darkMode = 1;

                    DwmSetWindowAttribute(
                        taskbarHandle,
                        20,
                        ref darkMode,
                        sizeof(int));
                }
            }
            catch
            {
            }
        }

        private void PlayStartupMusic()
        {
            try
            {
                string musicFolder =
                    Path.Combine(
                        AppDomain.CurrentDomain.BaseDirectory,
                        "music");

                if (!Directory.Exists(musicFolder))
                    return;

                string[] files =
                    Directory.GetFiles(
                        musicFolder,
                        "*.wav");

                if (files.Length == 0)
                    return;

                soundPlayer =
                    new SoundPlayer(files[0]);

                soundPlayer.Load();

                if (musicEnabled)
                    soundPlayer.PlayLooping();
            }
            catch (Exception ex)
            {
                Debug.WriteLine(
                    "Music error: " + ex);
            }
        }

        private void TryDeleteFile(
            string path)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(path) &&
                    File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
            }
        }

        protected override void OnFormClosing(
            FormClosingEventArgs e)
        {
            isClosing = true;

            lock (activeDownloads)
            {
                foreach (DownloadInfo info
                    in activeDownloads.Values)
                {
                    try
                    {
                        info.Cancelled = true;
                        info.Cancellation?.Cancel();
                    }
                    catch
                    {
                    }
                }
            }

            try
            {
                IntPtr taskbarHandle =
                    FindWindow(
                        "Shell_TrayWnd",
                        null);

                if (taskbarHandle != IntPtr.Zero)
                {
                    int darkMode = 0;

                    DwmSetWindowAttribute(
                        taskbarHandle,
                        20,
                        ref darkMode,
                        sizeof(int));
                }
            }
            catch
            {
            }

            try
            {
                soundPlayer?.Stop();
                soundPlayer?.Dispose();
            }
            catch
            {
            }

            base.OnFormClosing(e);
        }

        private static string ReadEmbeddedHtml()
        {
            Assembly assembly =
                Assembly.GetExecutingAssembly();

            using (Stream stream =
                   assembly.GetManifestResourceStream(
                       ResourceName))
            {
                if (stream == null)
                {
                    throw new FileNotFoundException(
                        "Embedded resource not found: " +
                        ResourceName);
                }

                using (StreamReader reader =
                       new StreamReader(stream))
                {
                    return reader.ReadToEnd();
                }
            }
        }
    }
}