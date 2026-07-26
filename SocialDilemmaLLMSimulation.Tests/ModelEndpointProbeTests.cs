using System.Text;
using System.Net;
using System.Net.Sockets;
using SocialDilemmaLLMSimulation;
using Xunit;

public sealed class ModelEndpointProbeTests
{
    [Fact]
    public async Task CheckAsyncReportsReachableWhenPortAcceptsTcpConnections()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var result = await ModelEndpointProbe.CheckAsync(
            $"http://127.0.0.1:{port}",
            TimeSpan.FromSeconds(1));

        Assert.True(result.IsReachable);
        Assert.Equal(port, result.Port);
        Assert.Null(result.Error);
    }

    [Fact]
    public async Task CheckAsyncRejectsInvalidBaseUrl()
    {
        var result = await ModelEndpointProbe.CheckAsync(
            "not-a-url",
            TimeSpan.FromSeconds(1));

        Assert.False(result.IsReachable);
        Assert.Contains("absolute URL", result.Error);
    }

    [Fact]
    public async Task CheckModelListsAsyncVerifiesAdvertisedModels()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var responseTask = RespondWithModelListAsync(listener, "model-a");
        var selection = Selection($"http://127.0.0.1:{port}", "model-a");

        var results = await ModelEndpointProbe.CheckModelListsAsync(selection);
        await responseTask;

        var result = Assert.Single(results);
        Assert.True(result.IsAvailable);
        Assert.Empty(result.MissingModels);
        Assert.Contains("model-a", result.AdvertisedModels);
    }

    [Fact]
    public async Task CheckModelListsAsyncReportsModelsMissingFromValidList()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var responseTask = RespondWithModelListAsync(listener, "different-model");
        var selection = Selection($"http://127.0.0.1:{port}", "claimed-model");

        var results = await ModelEndpointProbe.CheckModelListsAsync(selection);
        await responseTask;

        var result = Assert.Single(results);
        Assert.True(result.IsAvailable);
        Assert.Contains("claimed-model", result.MissingModels);
    }

    private static StartupModelSelection Selection(string baseUrl, string model)
        => new(
            "Test",
            "local",
            new[]
            {
                new ModelProfile
                {
                    Key = "primary",
                    Model = model,
                    BaseUrl = baseUrl,
                    Temperature = 0.7,
                    TopP = 0.95
                }
            },
            UsesCatalog: true);

    private static async Task RespondWithModelListAsync(TcpListener listener, string model)
    {
        using var client = await listener.AcceptTcpClientAsync();
        await using var stream = client.GetStream();
        using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);

        while (!string.IsNullOrEmpty(await reader.ReadLineAsync()))
        {
        }

        var body = $$"""{"data":[{"id":"{{model}}"}]}""";
        var response = Encoding.UTF8.GetBytes(
            "HTTP/1.1 200 OK\r\n" +
            "Content-Type: application/json\r\n" +
            $"Content-Length: {Encoding.UTF8.GetByteCount(body)}\r\n" +
            "Connection: close\r\n\r\n" +
            body);
        await stream.WriteAsync(response);
    }
}
