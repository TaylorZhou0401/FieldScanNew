using AForge.Video;
using AForge.Video.DirectShow;
using System;
using System.Collections.Generic;
using System.Drawing; // 需要引用 System.Drawing.Common 或 AForge 自带的 Bitmap
using System.IO;
using System.Windows.Media.Imaging;

// taylorzhou0401/fieldscannew/FieldScanNew-QBC-/FieldScanNew/Services/CameraService.cs

namespace FieldScanNew.Services
{
    public class CameraService
    {
        // --- 添加单例实现 ---
        private static CameraService? _instance;
        public static CameraService Instance => _instance ??= new CameraService();

        // 将构造函数设为私有
        private CameraService() { }
        // ------------------

        private FilterInfoCollection? _videoDevices;
        private VideoCaptureDevice? _videoSource;

        // 记录当前开启的摄像头索引，避免重复启动
        private int _currentCameraIndex = -1;

        public event Action<BitmapSource>? NewFrameReceived;

        public List<string> GetCameraList()
        {
            var cameras = new List<string>();
            _videoDevices = new FilterInfoCollection(FilterCategory.VideoInputDevice);
            foreach (FilterInfo device in _videoDevices)
            {
                cameras.Add(device.Name);
            }
            return cameras;
        }

        public void StartCamera(int cameraIndex)
        {
            if (_videoDevices == null || _videoDevices.Count == 0) return;
            if (cameraIndex < 0 || cameraIndex >= _videoDevices.Count) return;

            // 如果请求的摄像头已经是在运行状态，则直接返回，不重新初始化硬件
            if (_videoSource != null && _videoSource.IsRunning && _currentCameraIndex == cameraIndex)
            {
                return;
            }

            StopCamera();

            _videoSource = new VideoCaptureDevice(_videoDevices[cameraIndex].MonikerString);
            _videoSource.NewFrame += OnNewFrame;
            _videoSource.Start();
            _currentCameraIndex = cameraIndex;
        }

        public void StopCamera()
        {
            if (_videoSource != null && _videoSource.IsRunning)
            {
                _videoSource.SignalToStop();
                _videoSource.NewFrame -= OnNewFrame;
                _videoSource = null;
                _currentCameraIndex = -1;
            }
        }

        private void OnNewFrame(object sender, NewFrameEventArgs eventArgs)
        {
            try
            {
                using (var bitmap = (Bitmap)eventArgs.Frame.Clone())
                {
                    var bi = ToBitmapImage(bitmap);
                    bi.Freeze();
                    NewFrameReceived?.Invoke(bi);
                }
            }
            catch { }
        }

        private BitmapImage ToBitmapImage(Bitmap bitmap)
        {
            using (var memory = new MemoryStream())
            {
                bitmap.Save(memory, System.Drawing.Imaging.ImageFormat.Bmp);
                memory.Position = 0;
                var bitmapImage = new BitmapImage();
                bitmapImage.BeginInit();
                bitmapImage.StreamSource = memory;
                bitmapImage.CacheOption = BitmapCacheOption.OnLoad;
                bitmapImage.EndInit();
                return bitmapImage;
            }
        }
    }
}