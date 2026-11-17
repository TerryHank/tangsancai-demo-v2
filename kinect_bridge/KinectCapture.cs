using System.Drawing;
using System.Drawing.Imaging;
using System.Numerics;
using Azure = Microsoft.Azure.Kinect.Sensor;
using Tracking = Microsoft.Azure.Kinect.BodyTracking;
using V2 = Microsoft.Kinect;

sealed class AzureCapture : ICapture
{
    private readonly Azure.Device device;
    private readonly Tracking.Tracker tracker;
    private readonly Azure.Calibration calibration;
    public string Name => "azure";

    public AzureCapture()
    {
        if (Azure.Device.GetInstalledCount() == 0)
            throw new InvalidOperationException("Azure Kinect DK not found.");
        device = Azure.Device.Open(0);
        try
        {
            device.StartCameras(new Azure.DeviceConfiguration
            {
                CameraFPS = Azure.FPS.FPS30,
                ColorFormat = Azure.ImageFormat.ColorMJPG,
                ColorResolution = Azure.ColorResolution.R720p,
                DepthMode = Azure.DepthMode.NFOV_Unbinned,
                SynchronizedImagesOnly = true
            });
            calibration = device.GetCalibration();
            var config = Tracking.TrackerConfiguration.Default;
            config.ModelPath = Path.Combine(AppContext.BaseDirectory, "dnn_model_2_0_op11.onnx");
            config.ProcessingMode = Tracking.TrackerProcessingMode.DirectML;
            try { tracker = Tracking.Tracker.Create(calibration, config); }
            catch (Exception error)
            {
                Console.WriteLine($"Azure DirectML unavailable: {error.Message}. Trying CPU.");
                config.ProcessingMode = Tracking.TrackerProcessingMode.Cpu;
                tracker = Tracking.Tracker.Create(calibration, config);
            }
        }
        catch
        {
            device.Dispose();
            throw;
        }
    }

    public Reading? Read(bool includeColor)
    {
        try
        {
            using var capture = device.GetCapture(TimeSpan.FromMilliseconds(250));
            tracker.EnqueueCapture(capture, TimeSpan.Zero);
        }
        catch (TimeoutException) { }

        using var frame = tracker.PopResult(TimeSpan.FromMilliseconds(100), throwOnTimeout: false);
        if (frame is null) return null;
        using var original = includeColor ? frame.Capture : null;
        using var color = original?.Color;
        var bodies = new List<Skeleton>();
        for (uint i = 0; i < frame.NumberOfBodies; i++)
        {
            var body = frame.GetBody(i);
            var skeleton = body.Skeleton;
            var joints = new Dictionary<string, Joint>();
            var colorJoints = new Dictionary<string, Point>();
            foreach (var (name, id) in JointNames.Azure)
            {
                var joint = skeleton.GetJoint(id);
                var pos = joint.Position;
                joints[name] = new(pos.X / 1000, pos.Y / 1000, pos.Z / 1000,
                    joint.ConfidenceLevel >= Tracking.JointConfidenceLevel.Medium);
                if (color is null || !joints[name].Tracked) continue;
                var point = calibration.TransformTo2D(pos, Azure.CalibrationDeviceType.Depth, Azure.CalibrationDeviceType.Color);
                if (point is Vector2 pixel && float.IsFinite(pixel.X) && float.IsFinite(pixel.Y))
                    colorJoints[name] = new(pixel.X / color.WidthPixels, pixel.Y / color.HeightPixels);
            }
            if (joints["leftHip"].Tracked && joints["rightHip"].Tracked)
                bodies.Add(new(body.Id.ToString(), joints, colorJoints));
        }

        return new(bodies.ToArray(), color?.Memory.ToArray());
    }

    public void Dispose()
    {
        tracker.Dispose();
        device.Dispose();
    }
}

sealed class V2Capture : ICapture
{
    private readonly V2.KinectSensor sensor;
    private readonly V2.MultiSourceFrameReader reader;
    private readonly V2.BodyFrameReader bodyReader;
    private readonly V2.Body[] bodies = new V2.Body[6];
    private readonly V2.Body[] colorBodies = new V2.Body[6];
    private readonly Bitmap preview = new(640, 360, PixelFormat.Format32bppRgb);
    private readonly MemoryStream jpeg = new();
    public string Name => "v2";

    public V2Capture()
    {
        sensor = V2.KinectSensor.GetDefault() ?? throw new InvalidOperationException("Kinect v2 runtime or device not found.");
        sensor.Open();
        try
        {
            reader = sensor.OpenMultiSourceFrameReader(V2.FrameSourceTypes.Body | V2.FrameSourceTypes.Color);
            bodyReader = sensor.BodyFrameSource.OpenReader();
        }
        catch
        {
            sensor.Close();
            throw;
        }
    }

    public Reading? Read(bool includeColor)
    {
        using var skeletons = bodyReader.AcquireLatestFrame();
        if (skeletons is null) return null;
        var frame = includeColor ? reader.AcquireLatestFrame() : null;
        using var color = frame?.ColorFrameReference.AcquireFrame();
        using var pairedBodies = color is null ? null : frame!.BodyFrameReference.AcquireFrame();
        pairedBodies?.GetAndRefreshBodyData(colorBodies);
        skeletons.GetAndRefreshBodyData(bodies);
        var tracked = new List<Skeleton>();
        foreach (var body in bodies)
        {
            if (!body.IsTracked) continue;
            var joints = new Dictionary<string, Joint>();
            var colorJoints = new Dictionary<string, Point>();
            var paired = pairedBodies is null ? null : Array.Find(colorBodies,
                candidate => candidate is { IsTracked: true } && candidate.TrackingId == body.TrackingId);
            var latestJoints = body.Joints;
            var pairedJoints = paired?.Joints;
            foreach (var (name, id) in JointNames.V2)
            {
                var joint = latestJoints[id];
                var pos = joint.Position;
                // Match the Azure convention: meters, positive Y down and positive Z away.
                joints[name] = new(-pos.X, -pos.Y, pos.Z, joint.TrackingState == V2.TrackingState.Tracked);
                if (color is null || pairedJoints is null) continue;
                var colorJoint = pairedJoints[id];
                if (colorJoint.TrackingState != V2.TrackingState.Tracked) continue;
                var pixel = sensor.CoordinateMapper.MapCameraPointToColorSpace(colorJoint.Position);
                if (float.IsFinite(pixel.X) && float.IsFinite(pixel.Y))
                    colorJoints[name] = new(pixel.X / color.FrameDescription.Width, pixel.Y / color.FrameDescription.Height);
            }
            if (joints["leftHip"].Tracked && joints["rightHip"].Tracked)
                tracked.Add(new(body.TrackingId.ToString(), joints, colorJoints));
        }

        return new(tracked.ToArray(), color is null ? null : EncodeColor(color));
    }

    private unsafe byte[] EncodeColor(V2.ColorFrame color)
    {
        var width = color.FrameDescription.Width;
        var height = color.FrameDescription.Height;
        if (color.RawColorImageFormat == V2.ColorImageFormat.Yuy2)
        {
            using var raw = color.LockRawImageBuffer();
            if (raw.Size < (long)width * height * 2) throw new InvalidDataException("Incomplete Kinect YUY2 frame");
            var data = preview.LockBits(new Rectangle(0, 0, preview.Width, preview.Height),
                ImageLockMode.WriteOnly, preview.PixelFormat);
            try
            {
                // YUY2 stores Y0 U Y1 V per pair. Sample at output resolution before conversion.
                for (int y = 0; y < data.Height; y++)
                {
                    var row = (byte*)raw.UnderlyingBuffer + ((y * height / data.Height) * width * 2);
                    var dst = (byte*)data.Scan0 + y * data.Stride;
                    for (int x = 0; x < data.Width; x++, dst += 4)
                    {
                        int sx = x * width / data.Width;
                        var pair = row + (sx & ~1) * 2;
                        int luma = 298 * (pair[(sx & 1) * 2] - 16) + 128;
                        int u = pair[1] - 128, v = pair[3] - 128;
                        dst[0] = (byte)Math.Clamp((luma + 516 * u) >> 8, 0, 255);
                        dst[1] = (byte)Math.Clamp((luma - 100 * u - 208 * v) >> 8, 0, 255);
                        dst[2] = (byte)Math.Clamp((luma + 409 * v) >> 8, 0, 255);
                    }
                }
            }
            finally { preview.UnlockBits(data); }
        }
        else
        {
            using var full = new Bitmap(width, height, PixelFormat.Format32bppRgb);
            var data = full.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, full.PixelFormat);
            try { color.CopyConvertedFrameDataToIntPtr(data.Scan0, (uint)(data.Stride * height), V2.ColorImageFormat.Bgra); }
            finally { full.UnlockBits(data); }
            using var graphics = Graphics.FromImage(preview);
            graphics.DrawImage(full, 0, 0, preview.Width, preview.Height);
        }
        jpeg.SetLength(0);
        preview.Save(jpeg, ImageFormat.Jpeg);
        return jpeg.ToArray();
    }

    public void Dispose()
    {
        bodyReader.Dispose();
        reader.Dispose();
        sensor.Close();
        preview.Dispose();
        jpeg.Dispose();
    }
}

static class JointNames
{
    public static readonly (string, Tracking.JointId)[] Azure =
    [
        ("leftShoulder", Tracking.JointId.ShoulderLeft), ("rightShoulder", Tracking.JointId.ShoulderRight),
        ("leftElbow", Tracking.JointId.ElbowLeft), ("rightElbow", Tracking.JointId.ElbowRight),
        ("leftWrist", Tracking.JointId.WristLeft), ("rightWrist", Tracking.JointId.WristRight),
        ("leftHip", Tracking.JointId.HipLeft), ("rightHip", Tracking.JointId.HipRight)
    ];
    public static readonly (string, V2.JointType)[] V2 =
    [
        ("leftShoulder", Microsoft.Kinect.JointType.ShoulderLeft), ("rightShoulder", Microsoft.Kinect.JointType.ShoulderRight),
        ("leftElbow", Microsoft.Kinect.JointType.ElbowLeft), ("rightElbow", Microsoft.Kinect.JointType.ElbowRight),
        ("leftWrist", Microsoft.Kinect.JointType.WristLeft), ("rightWrist", Microsoft.Kinect.JointType.WristRight),
        ("leftHip", Microsoft.Kinect.JointType.HipLeft), ("rightHip", Microsoft.Kinect.JointType.HipRight)
    ];
}
