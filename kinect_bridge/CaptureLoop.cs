using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;

record Joint(float X, float Y, float Z, bool Tracked);
record Point(float X, float Y);
record Skeleton(string Id, Dictionary<string, Joint> Joints,
    Dictionary<string, Point> ColorJoints)
{
    public float Distance => Joints["leftHip"].Z + Joints["rightHip"].Z;
}
record Reading(Skeleton[] Bodies, byte[]? Jpeg);
record FrameHeader(long FrameId, long TimestampMs, string Device, string State,
    string? Detail, string? BodyId, Dictionary<string, Joint>? Joints,
    Dictionary<string, Point>? ColorJoints);
record SensorPacket(FrameHeader Header, ReadOnlyMemory<byte> Wire);

interface ICapture : IDisposable
{
    string Name { get; }
    Reading? Read(bool includeColor);
}

sealed class CaptureLoop(string mode, string root)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private long frameId;
    private volatile SensorPacket latest = Encode(new(0, 0, "none", "waiting", null, null, null, null));
    private int clients;
    public SensorPacket Latest => latest;
    public void AddClient() => Interlocked.Increment(ref clients);
    public void RemoveClient() => Interlocked.Decrement(ref clients);

    public void Run(CancellationToken cancellation)
    {
        while (!cancellation.IsCancellationRequested)
        {
            if (mode == "mock" && Volatile.Read(ref clients) == 0)
            {
                cancellation.WaitHandle.WaitOne(100);
                continue;
            }

            try
            {
                using var source = Open();
                Console.WriteLine($"Sensor opened: {source.Name}");
                ReadFrames(source, cancellation);
                if (mode == "mock") Publish("mock", "waiting");
            }
            catch (Exception error)
            {
                Publish(mode, "waiting", detail: error.Message);
                Console.WriteLine($"Sensor waiting ({mode}): {error.Message}");
                cancellation.WaitHandle.WaitOne(2000);
            }
        }
    }

    private ICapture Open()
    {
        if (mode == "mock") return new MockCapture(root);
        if (mode == "azure") return new AzureCapture();
        if (mode == "v2") return OpenInstalledV2();

        try { return new AzureCapture(); }
        catch (Exception azureError)
        {
            try { return OpenInstalledV2(); }
            catch (Exception v2Error)
            {
                throw new InvalidOperationException($"Azure DK: {azureError.Message} | Kinect v2: {v2Error.Message}");
            }
        }
    }

    private static ICapture OpenInstalledV2()
    {
        if (!V2Runtime.IsInstalled())
            throw new InvalidOperationException("Install Kinect for Windows Runtime 2.0, then reconnect the sensor.");
        return OpenV2();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ICapture OpenV2() => new V2Capture();

    private void ReadFrames(ICapture source, CancellationToken cancellation)
    {
        string? selectedId = null;
        long lastSeen = 0;
        long lastFrame = clock.ElapsedMilliseconds;
        long lastColor = lastFrame - 200;
        while (!cancellation.IsCancellationRequested)
        {
            if (mode == "mock" && Volatile.Read(ref clients) == 0) return;
            var now = clock.ElapsedMilliseconds;
            var reading = source.Read(now - lastColor >= 200 && Volatile.Read(ref clients) > 0);
            if (reading is null)
            {
                if (clock.ElapsedMilliseconds - lastFrame > 3000)
                    throw new TimeoutException("No capture frames for 3 seconds.");
                cancellation.WaitHandle.WaitOne(10);
                continue;
            }

            lastFrame = clock.ElapsedMilliseconds;
            if (reading.Jpeg is not null) lastColor = lastFrame;
            var body = reading.Bodies.FirstOrDefault(person => person.Id == selectedId);
            if (body is null && (selectedId is null || lastFrame - lastSeen >= 5000))
            {
                body = reading.Bodies.MinBy(person => person.Distance);
                selectedId = body?.Id;
            }
            if (body is not null) lastSeen = lastFrame;
            Publish(source.Name, "frame", body, reading.Jpeg);
        }
    }

    private void Publish(string device, string state, Skeleton? body = null, byte[]? jpeg = null, string? detail = null)
    {
        var header = new FrameHeader(++frameId, clock.ElapsedMilliseconds, device, state,
            detail, body?.Id, body?.Joints, jpeg is null ? null : body?.ColorJoints);
        latest = Encode(header, jpeg);
    }

    private static SensorPacket Encode(FrameHeader header, byte[]? jpeg = null)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(header, Json);
        var wire = new byte[4 + json.Length + (jpeg?.Length ?? 0)];
        BinaryPrimitives.WriteInt32LittleEndian(wire, json.Length);
        json.CopyTo(wire, 4);
        jpeg?.CopyTo(wire, 4 + json.Length);
        return new(header, wire);
    }
}
