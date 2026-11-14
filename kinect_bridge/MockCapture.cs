using System.Diagnostics;

sealed class MockCapture(string root) : ICapture
{
    private readonly Stopwatch time = Stopwatch.StartNew();
    private readonly byte[] rgb = File.ReadAllBytes(Path.Combine(root, "assets", "simulation", "red-shirt.jpg"));
    private readonly Dictionary<string, Point> projection = new()
    {
        ["rightShoulder"] = new(.64f, .26f), ["leftShoulder"] = new(.92f, .25f),
        ["rightHip"] = new(.55f, .61f), ["leftHip"] = new(.95f, .63f)
    };
    public string Name => "mock";

    public Reading Read(bool includeColor)
    {
        Thread.Sleep(33);
        var seconds = time.Elapsed.TotalSeconds;
        if (seconds < 2 || seconds > 85) return new([], null);
        var sway = (float)Math.Sin(seconds * 5) * .065f;
        var lift = seconds >= 17 && (seconds < 21 || seconds % 3 < 1.8);
        Dictionary<string, Joint> joints = new()
        {
            ["leftShoulder"] = new(.20f + sway, -.50f, 2, true),
            ["rightShoulder"] = new(-.20f + sway, -.50f, 2, true),
            ["leftElbow"] = new(.29f + sway, -.30f, 2, true),
            ["rightElbow"] = new(-.29f + sway, -.30f, 2, true),
            ["leftWrist"] = new(.34f + sway, lift ? -.64f : -.05f, 2, true),
            ["rightWrist"] = new(-.34f + sway, -.05f, 2, true),
            ["leftHip"] = new(.15f + sway, 0, 2, true),
            ["rightHip"] = new(-.15f + sway, 0, 2, true)
        };
        return new([new("mock-1", joints, projection)], includeColor ? rgb : null);
    }

    public void Dispose() { }
}
