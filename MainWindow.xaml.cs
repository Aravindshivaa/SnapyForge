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
                        VideoDurationText.Text = $"Duration: {TimeSpan.FromSeconds(_videoDurationInSeconds):mm\\:ss}";
                    });
                }
                else return;
            }

            if (_isPlaying)
                return;

            _isPlaying = true;
            _isPaused = false;

            WriteableBitmap writableBitmap = new WriteableBitmap(
                _videoCapture.FrameWidth, _videoCapture.FrameHeight, 96, 96, System.Windows.Media.PixelFormats.Bgr24, null);

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
                    VideoDisplay.Source = writableBitmap;
                    ProgressBar.Value = _videoCapture.Get(VideoCaptureProperties.PosFrames);
                    double currentTimeInSeconds = _videoCapture.Get(VideoCaptureProperties.PosFrames) / _videoCapture.Get(VideoCaptureProperties.Fps);
                    CurrentTimeText.Text = $"Current Time: {TimeSpan.FromSeconds(currentTimeInSeconds):mm\\:ss}";
                });

                if (_isTakingScreenshots && _stopwatch.ElapsedMilliseconds >= _screenshotIntervalMs)
                {
                    SaveScreenshot();
                    _stopwatch.Restart();
                }

                await Task.Delay(33);
            }
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

                double videoDurationSeconds = totalFrames / fps;
                double intervalSeconds = videoDurationSeconds / screenshotCount;

                string screenshotsDir = GetScreenshotFolder("BatchScreenshots"); 

                ScreenshotProgressBar.Visibility = Visibility.Visible;
                ScreenshotProgressBar.Minimum = 0;
                ScreenshotProgressBar.Maximum = screenshotCount;
                ScreenshotProgressBar.Value = 0;

                for (int i = 0; i < screenshotCount; i++)
                {
                    int frameNumber = (int)(i * intervalSeconds * fps);
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

        public class RelayCommand : ICommand
        {
            private readonly Action<object> _execute;
            public RelayCommand(Action<object> execute) => _execute = execute;
            public bool CanExecute(object parameter) => true;
            public event EventHandler CanExecuteChanged;
            public void Execute(object parameter) => _execute(parameter);
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
