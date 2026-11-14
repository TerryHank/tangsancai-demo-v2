using System.Net.WebSockets;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;

var root = Path.GetFullPath(Argument(args, "--root", Directory.GetCurrentDirectory()));
var device = Argument(args, "--device", "auto");
var port = int.Parse(Argument(args, "--port", "8765"));
if (device is not ("auto" or "azure" or "v2" or "mock"))
    throw new ArgumentException("--device must be auto, azure, v2, or mock.");
if (!File.Exists(Path.Combine(root, "index.html")))
    throw new DirectoryNotFoundException($"No index.html in {root}");

var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
var app = builder.Build();
var types = new FileExtensionContentTypeProvider();
types.Mappings[".mjs"] = "text/javascript";
types.Mappings[".wasm"] = "application/wasm";
types.Mappings[".tflite"] = "application/octet-stream";
using var files = new PhysicalFileProvider(root);
app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = files });
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = files,
    ContentTypeProvider = types,
    OnPrepareResponse = context => context.Context.Response.Headers.CacheControl = "no-cache"
});
var socketOptions = new WebSocketOptions();
socketOptions.AllowedOrigins.Add($"http://127.0.0.1:{port}");
socketOptions.AllowedOrigins.Add($"http://localhost:{port}");
app.UseWebSockets(socketOptions);
V2Runtime.Register();
var capture = new CaptureLoop(device, root);
app.MapGet("/health", () => Results.Ok(new { app = "tangsancai", mode = device, sensor = capture.Latest.Header }));
app.MapGet("/kinect", async (HttpContext context) =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = 400;
        return;
    }

    using var socket = await context.WebSockets.AcceptWebSocketAsync();
    using var stop = CancellationTokenSource.CreateLinkedTokenSource(
        context.RequestAborted, app.Lifetime.ApplicationStopping);
    var receive = ReadClient(socket, stop.Token);
    capture.AddClient();
    try
    {
        SensorPacket? sent = null;
        while (!receive.IsCompleted && !stop.IsCancellationRequested)
        {
            var packet = capture.Latest;
            if (packet != sent)
            {
                // Use a single in-flight frame and fetch the latest after each send.
                await socket.SendAsync(packet.Wire, WebSocketMessageType.Binary, true, stop.Token);
                sent = packet;
            }
            await Task.Delay(15, stop.Token);
        }
    }
    catch (Exception error) when (error is WebSocketException or OperationCanceledException) { }
    finally
    {
        capture.RemoveClient();
        await stop.CancelAsync();
        await receive;
    }
});

async Task ReadClient(WebSocket socket, CancellationToken cancellation)
{
    try
    {
        var buffer = new byte[32];
        while (socket.State == WebSocketState.Open)
        {
            var message = await socket.ReceiveAsync(buffer, cancellation);
            if (message.MessageType == WebSocketMessageType.Close) return;
        }
    }
    catch (Exception error) when (error is WebSocketException or OperationCanceledException) { }
}

var captureTask = Task.Run(() => capture.Run(app.Lifetime.ApplicationStopping));
await app.RunAsync();
await captureTask;

static string Argument(string[] args, string name, string fallback)
{
    var index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : fallback;
}
