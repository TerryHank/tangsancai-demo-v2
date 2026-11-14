using System.Drawing;
using System.Drawing.Imaging;
using System.Numerics;
using System.Runtime.InteropServices;
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
    private readonly V2.Body[] bodies = new V2.Body[6];
    private byte[]? bgra;
    public string Name => "v2";

    public V2Capture()
    {
        sensor = V2.KinectSensor.GetDefault() ?? throw new InvalidOperationException("Kinect v2 runtime or device not found.");
        sensor.Open();
        try
        {
            reader = sensor.OpenMultiSourceFrameReader(V2.FrameSourceTypes.Body | V2.FrameSourceTypes.Color);
        }
        catch
        {
            sensor.Close();
            throw;
        }
    }

    public Reading? Read(bool includeColor)
    {
        var frame = reader.AcquireLatestFrame();
        using var skeletons = frame?.BodyFrameReference.AcquireFrame();
        if (skeletons is null) return null;
        using var color = includeColor ? frame!.ColorFrameReference.AcquireFrame() : null;
        skeletons.GetAndRefreshBodyData(bodies);
        var tracked = new List<Skeleton>();
        foreach (var body in bodies)
        {
            if (!body.IsTracked) continue;
            var joints = new Dictionary<string, Joint>();
            var colorJoints = new Dictionary<string, Point>();
            foreach (var (name, id) in JointNames.V2)
            {
                var joint = body.Joints[id];
                var pos = joint.Position;
                // Match the Azure convention: meters, positive Y down and positive Z away.
                joints[name] = new(-pos.X, -pos.Y, pos.Z, joint.TrackingState == V2.TrackingState.Tracked);
                if (color is null || !joints[name].Tracked) continue;
                var pixel = sensor.CoordinateMapper.MapCameraPointToColorSpace(pos);
                if (float.IsFinite(pixel.X) && float.IsFinite(pixel.Y))
                    colorJoints[name] = new(pixel.X / color.FrameDescription.Width, pixel.Y / color.FrameDescription.Height);
            }
            if (joints["leftHip"].Tracked && joints["rightHip"].Tracked)
                tracked.Add(new(body.TrackingId.ToString(), joints, colorJoints));
        }

        return new(tracked.ToArray(), color is null ? null : EncodeColor(color));
    }

    private byte[] EncodeColor(V2.ColorFrame color)
    {
        var width = color.FrameDescription.Width;
        var height = color.FrameDescription.Height;
        bgra ??= new byte[width * height * 4];
        color.CopyConvertedFrameDataToArray(bgra, V2.ColorImageFormat.Bgra);
        using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppRgb);
        var data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, bitmap.PixelFormat);
        try { Marshal.Copy(bgra, 0, data.Scan0, bgra.Length); }
        finally { bitmap.UnlockBits(data); }
        using var scaled = new Bitmap(bitmap, 640, 360);
        using var jpeg = new MemoryStream();
        scaled.Save(jpeg, ImageFormat.Jpeg);
        return jpeg.ToArray();
    }

    public void Dispose()
    {
        reader.Dispose();
        sensor.Close();
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
