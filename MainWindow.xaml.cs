using Microsoft.Win32;
using OpenCvSharp;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;


namespace WpfOpenCvApp
{

    public partial class MainWindow : System.Windows.Window
    {
        private VideoCapture _videoCapture;
        private Mat _currentFrame;
        private bool _isTakingScreenshots = false;
        private bool _isPlaying = false;
        private bool _isPaused = false;
        private Stopwatch _stopwatch = new Stopwatch();
        private int _screenshotIntervalMs = 1000;
        private int _screenshotCount = 0;
        private int _totalFrames = 0;
        private string _videoFilePath;
        private readonly CascadeClassifier _faceCascade;



        private List<Mat> _screenshotQueue = new List<Mat>();
        private int _batchSize = 10;
        private Task _screenshotTask = null;

        private double _videoDurationInSeconds;


        private void CalculateVideoDuration()
        {
            double fps = _videoCapture.Get(VideoCaptureProperties.Fps);
            _videoDurationInSeconds = _totalFrames / fps;
        }

        private string GetScreenshotFolder(string baseFolderName = "Screenshots")
        {
            string picturesPath = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
            string videoName = Path.GetFileNameWithoutExtension(_videoFilePath);
            string screenshotDir = Path.Combine(picturesPath, baseFolderName, videoName);
            Directory.CreateDirectory(screenshotDir);
            return screenshotDir;
        }

        private async void PlayVideo_Click(object sender, RoutedEventArgs e)
        {
            if (_videoCapture == null)
            {
                OpenFileDialog openFileDialog = new OpenFileDialog
                {
                    Filter = "Video Files|*.mp4;*.avi;*.mov;*.mkv;*.webm"
                };

                if (openFileDialog.ShowDialog() == true)
                {
                    _videoFilePath = openFileDialog.FileName;

                    _videoCapture?.Release();
                    _videoCapture = new VideoCapture(_videoFilePath);

                    if (!_videoCapture.IsOpened())
                    {
                        MessageBox.Show("Failed to open video.");
                        return;
                    }

                    _currentFrame = new Mat();
                    _totalFrames = (int)_videoCapture.Get(VideoCaptureProperties.FrameCount);
                    CalculateVideoDuration();

                    ProgressBar.IsEnabled = true;
                    ProgressBar.Minimum = 0;
                    ProgressBar.Maximum = _totalFrames;

                    Dispatcher.Invoke(() =>
                    {
                        VideoDurationText.Text = $"{TimeSpan.FromSeconds(_videoDurationInSeconds):mm\\:ss}";
                    });
                }
                else return;
            }

            if (_isPlaying)
                return;

            _isPlaying = true;
            _isPaused = false;

            int width = _videoCapture.FrameWidth;
            int height = _videoCapture.FrameHeight;

            var writableBitmap = new WriteableBitmap(
                width, height, 96, 96, PixelFormats.Bgr24, null);

            _stopwatch.Restart();

            while (_isPlaying && _videoCapture.IsOpened())
            {
                if (_isPaused)
                {
                    await Task.Delay(100);
                    continue;
                }

                if (!_videoCapture.Read(_currentFrame) || _currentFrame.Empty())
                {
                    StopVideo();
                    break;
                }

                byte[] buffer = new byte[_currentFrame.Rows * _currentFrame.Cols * _currentFrame.ElemSize()];
                System.Runtime.InteropServices.Marshal.Copy(_currentFrame.Data, buffer, 0, buffer.Length);
                IntPtr bufferPtr = System.Runtime.InteropServices.Marshal.UnsafeAddrOfPinnedArrayElement(buffer, 0);

                writableBitmap.Lock();
                writableBitmap.WritePixels(
                    new Int32Rect(0, 0, _currentFrame.Width, _currentFrame.Height),
                    bufferPtr,
                    buffer.Length,
                    (int)_currentFrame.Step());
                writableBitmap.Unlock();

                Dispatcher.Invoke(() =>
                {
                    // Convert current frame to a BitmapSource
                    BitmapSource bitmap = BitmapSource.Create(
     _currentFrame.Width, _currentFrame.Height, 96, 96,
     PixelFormats.Bgr24, null,
     _currentFrame.Data,
     (int)(_currentFrame.Step() * _currentFrame.Height),  // total buffer size
     (int)_currentFrame.Step());                           // stride (bytes per row)


                    // Update main video display and ambient background separately
                    VideoDisplay.Source = bitmap;
                    AmbientVideoGlow.Source = bitmap;

                    ProgressBar.Value = _videoCapture.Get(VideoCaptureProperties.PosFrames);
                    double currentTimeInSeconds = _videoCapture.Get(VideoCaptureProperties.PosFrames) / _videoCapture.Get(VideoCaptureProperties.Fps);
                    CurrentTimeText.Text = $"{TimeSpan.FromSeconds(currentTimeInSeconds):mm\\:ss}";
                });


                if (_isTakingScreenshots && _stopwatch.ElapsedMilliseconds >= _screenshotIntervalMs)
                {
                    SaveScreenshot();
                    _stopwatch.Restart();
                }

                await Task.Delay(33); // ~30 FPS
            }
        }


        private void Window_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
                this.DragMove(); // Allows window dragging
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }

        private void Minimize_Click(object sender, RoutedEventArgs e)
        {
            this.WindowState = WindowState.Minimized;
        }

        private void OpenTimeFramePopup_Click(object sender, RoutedEventArgs e)
        {
            TimeFramePopup.IsOpen = true;
        }


        private void PauseVideo_Click(object sender, RoutedEventArgs e)
        {
            if (_isPlaying)
                _isPaused = !_isPaused;
        }

        private void StopVideo_Click(object sender, RoutedEventArgs e)
        {
            StopVideo();
        }

        private void StopVideo()
        {
            _isPlaying = false;
            _isPaused = false;
            _videoCapture?.Release();
            Dispatcher.Invoke(() =>
            {
                VideoDisplay.Source = null;
                ProgressBar.Value = 0;
                ProgressBar.IsEnabled = false;
            });
        }

        private void StartTakingScreenshots_Click(object sender, RoutedEventArgs e)
        {
            if (_videoCapture == null || !_videoCapture.IsOpened())
            {
                MessageBox.Show("Please start the video first.");
                return;
            }

            _isTakingScreenshots = true;
            _stopwatch.Restart();

            if (_screenshotTask == null || _screenshotTask.IsCompleted)
            {
                _screenshotTask = Task.Run(() => SaveScreenshotsInBatch());
            }
        }

        private void SaveScreenshot()
        {
            if (_currentFrame == null || _currentFrame.Empty())
                return;

            _screenshotQueue.Add(_currentFrame.Clone());
        }

        private async void SaveScreenshotsInBatch()
        {
            while (_isTakingScreenshots)
            {
                await Task.Delay(100);

                if (_screenshotQueue.Count >= _batchSize)
                {
                    List<Mat> batchToSave = new List<Mat>(_screenshotQueue.GetRange(0, _batchSize));
                    _screenshotQueue.RemoveRange(0, _batchSize);

                    try
                    {
                        string screenshotsDir = GetScreenshotFolder();

                        foreach (var frame in batchToSave)
                        {
                            string screenshotPath = Path.Combine(screenshotsDir, $"Masstudioz_{_screenshotCount++}.png");
                            frame.SaveImage(screenshotPath);
                        }
                    }
                    catch (Exception ex)
                    {
                        Dispatcher.Invoke(() =>
                        {
                            MessageBox.Show($"Error saving screenshots: {ex.Message}");
                        });
                    }
                }
            }
        }

        private async void DynamicBatchScreenshot_Click(object sender, RoutedEventArgs e)
        {
            if (_videoCapture == null || !_videoCapture.IsOpened())
            {
                MessageBox.Show("No video is loaded. Please load a video first.");
                return;
            }

            if (!int.TryParse(ScreenshotCountTextBox.Text, out int screenshotCount) || screenshotCount <= 0)
            {
                MessageBox.Show("Please enter a valid positive number for screenshot count.");
                return;
            }

            try
            {
                int totalFrames = (int)_videoCapture.Get(VideoCaptureProperties.FrameCount);
                double fps = _videoCapture.Get(VideoCaptureProperties.Fps);

                if (fps <= 0)
                {
                    MessageBox.Show("Invalid video FPS.");
                    return;
                }

                double startSeconds = 0;
                double endSeconds = totalFrames / fps;

                // If custom time frame popup is open, try to parse it
                if (TimeFramePopup.IsOpen)
                {
                    bool validStart = TimeSpan.TryParseExact(StartTimeTextBox.Text, @"mm\:ss", null, out TimeSpan start);
                    bool validEnd = TimeSpan.TryParseExact(EndTimeTextBox.Text, @"mm\:ss", null, out TimeSpan end);

                    if (!validStart || !validEnd)
                    {
                        MessageBox.Show("Invalid time format. Use mm:ss.");
                        return;
                    }

                    startSeconds = start.TotalSeconds;
                    endSeconds = end.TotalSeconds;

                    if (startSeconds >= endSeconds || endSeconds > totalFrames / fps)
                    {
                        MessageBox.Show("Invalid time range.");
                        return;
                    }
                }

                double captureDuration = endSeconds - startSeconds;
                double intervalSeconds = captureDuration / screenshotCount;

                string screenshotsDir = GetScreenshotFolder("BatchScreenshots");

                ScreenshotProgressBar.Visibility = Visibility.Visible;
                ScreenshotProgressBar.Minimum = 0;
                ScreenshotProgressBar.Maximum = screenshotCount;
                ScreenshotProgressBar.Value = 0;

                for (int i = 0; i < screenshotCount; i++)
                {
                    double captureTime = startSeconds + (i * intervalSeconds);
                    int frameNumber = (int)(captureTime * fps);

                    if (frameNumber >= totalFrames)
                        continue;

                    _videoCapture.Set(VideoCaptureProperties.PosFrames, frameNumber);
                    Mat frame = new Mat();
                    _videoCapture.Read(frame);

                    if (frame.Empty())
                        continue;

                    string screenshotPath = Path.Combine(screenshotsDir, $"Screenshot_{i + 1}.png");
                    frame.SaveImage(screenshotPath);

                    await Dispatcher.InvokeAsync(() =>
                    {
                        ScreenshotProgressBar.Value = Math.Min(i + 1, screenshotCount);
                    });

                    await Task.Delay(20);
                }

                MessageBox.Show($"{screenshotCount} screenshots saved in: {screenshotsDir}");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error capturing screenshots: {ex.Message}");
            }
            finally
            {
                ScreenshotProgressBar.Visibility = Visibility.Collapsed;
                TimeFramePopup.IsOpen = false; // Close popup after use
            }
        }


        private static readonly Regex _onlyNumbers = new Regex("^[0-9]+$");

        private void ScreenshotCountTextBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
        {
            e.Handled = !_onlyNumbers.IsMatch(e.Text);
        }

        private void ScreenshotCountTextBox_Pasting(object sender, DataObjectPastingEventArgs e)
        {
            if (int.TryParse(ScreenshotCountTextBox.Text, out int count))
            {
                if (count < 1 || count > 1000)
                {
                    MessageBox.Show("Please enter a valid screenshot count greater than 0.");
                    return;
                }

                if (e.DataObject.GetDataPresent(typeof(string)))
                {
                    string text = (string)e.DataObject.GetData(typeof(string));
                    if (!_onlyNumbers.IsMatch(text))
                    {
                        e.CancelCommand();
                    }
                }
                else
                {
                    e.CancelCommand();
                }
            }
        }

        private void StopTakingScreenshots_Click(object sender, RoutedEventArgs e)
        {
            _isTakingScreenshots = false;
            MessageBox.Show("Bulk screenshot capture stopped!");
        }

        private void Window_DragEnter(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
                e.Effects = DragDropEffects.Copy;
            else
                e.Effects = DragDropEffects.None;
        }

        private void Window_Drop(object sender, DragEventArgs e)
        {
            if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
            {
                string path = files[0];
                string extension = System.IO.Path.GetExtension(path).ToLower();

                if (extension == ".mp4" || extension == ".avi" || extension == ".mov" || extension == ".mkv")
                {
                    LoadVideo(path);
                }
                else
                {
                    MessageBox.Show("Unsupported file format.");
                }
            }
        }

        private void LoadVideo(string videoFilePath)
        {
            _videoFilePath = videoFilePath;
            _videoCapture?.Release();
            _videoCapture = new VideoCapture(videoFilePath);

            if (!_videoCapture.IsOpened())
            {
                MessageBox.Show("Failed to open video.");
                return;
            }

            _currentFrame = new Mat();
            _totalFrames = (int)_videoCapture.Get(VideoCaptureProperties.FrameCount);
            CalculateVideoDuration();

            ProgressBar.IsEnabled = true;
            ProgressBar.Minimum = 0;
            ProgressBar.Maximum = _totalFrames;

            Dispatcher.Invoke(() =>
            {
                VideoDurationText.Text = $"Duration: {TimeSpan.FromSeconds(_videoDurationInSeconds):mm\\:ss}";
            });
            _isPlaying = false;
            _isPaused = false;
        }

        private void ProgressBar_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_videoCapture == null || !_isPlaying)
                return;

            if (Math.Abs(_videoCapture.Get(VideoCaptureProperties.PosFrames) - ProgressBar.Value) > 1)
            {
                _videoCapture.Set(VideoCaptureProperties.PosFrames, ProgressBar.Value);
            }
        }


    }
}
