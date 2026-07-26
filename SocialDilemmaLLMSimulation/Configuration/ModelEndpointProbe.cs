using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text.Json;

namespace SocialDilemmaLLMSimulation;

public sealed record ModelEndpointProbeResult(
    string BaseUrl,
    string Host,
    int Port,
    bool IsReachable,
    string? Error);

public sealed record ModelListProbeResult(
    string BaseUrl,
    string ModelsUrl,
    bool IsAvailable,
    IReadOnlyList<string> AdvertisedModels,
    IReadOnlyList<string> MissingModels,
    string? Error);

public static class ModelEndpointProbe
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(3);

    public static async Task<IReadOnlyList<ModelEndpointProbeResult>> CheckSelectionAsync(
        StartupModelSelection selection,
        CancellationToken cancellationToken = default)
    {
        if (selection.Models.Count == 0)
            return Array.Empty<ModelEndpointProbeResult>();

        var defaultBaseUrl = string.IsNullOrWhiteSpace(selection.Models[0].BaseUrl)
            ? "http://localhost:8080"
            : selection.Models[0].BaseUrl;

        var endpoints = selection.Models
            .Select(model => string.IsNullOrWhiteSpace(model.BaseUrl) ? defaultBaseUrl : model.BaseUrl)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var checks = endpoints.Select(baseUrl => CheckAsync(baseUrl, DefaultTimeout, cancellationToken));
        return await Task.WhenAll(checks);
    }

    public static async Task<IReadOnlyList<ModelListProbeResult>> CheckModelListsAsync(
        StartupModelSelection selection,
        CancellationToken cancellationToken = default)
    {
        if (selection.Models.Count == 0)
            return Array.Empty<ModelListProbeResult>();

        var defaultBaseUrl = string.IsNullOrWhiteSpace(selection.Models[0].BaseUrl)
            ? "http://localhost:8080"
            : selection.Models[0].BaseUrl;

        var profilesByEndpoint = selection.Models
            .GroupBy(
                model => string.IsNullOrWhiteSpace(model.BaseUrl) ? defaultBaseUrl : model.BaseUrl,
                StringComparer.OrdinalIgnoreCase);

        var checks = profilesByEndpoint.Select(group =>
            CheckModelListAsync(group.Key, group.ToList(), DefaultTimeout, cancellationToken));
        return await Task.WhenAll(checks);
    }

    public static async Task<ModelEndpointProbeResult> CheckAsync(
        string baseUrl,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri)
            || string.IsNullOrWhiteSpace(uri.Host)
            || uri.Port <= 0)
        {
            return new ModelEndpointProbeResult(
                baseUrl,
                string.Empty,
                0,
                IsReachable: false,
                Error: "Base URL must be an absolute URL with a valid host and port.");
        }

        try
        {
            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(timeout);

            using var client = new TcpClient();
            await client.ConnectAsync(uri.Host, uri.Port, timeoutSource.Token);

            return new ModelEndpointProbeResult(
                baseUrl,
                uri.Host,
                uri.Port,
                IsReachable: true,
                Error: null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new ModelEndpointProbeResult(
                baseUrl,
                uri.Host,
                uri.Port,
                IsReachable: false,
                Error: $"Connection timed out after {timeout.TotalSeconds:0.#} seconds.");
        }
        catch (Exception ex) when (ex is SocketException or IOException)
        {
            return new ModelEndpointProbeResult(
                baseUrl,
                uri.Host,
                uri.Port,
                IsReachable: false,
                Error: ex.Message);
        }
    }

    private static async Task<ModelListProbeResult> CheckModelListAsync(
        string baseUrl,
        IReadOnlyList<ModelProfile> profiles,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var modelsUrl = baseUrl.TrimEnd('/') + "/v1/models";
        var expectedModels = profiles
            .Select(profile => profile.Model)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        try
        {
            using var client = new HttpClient
            {
                Timeout = timeout
            };
            using var request = new HttpRequestMessage(HttpMethod.Get, modelsUrl);

            var apiKey = profiles
                .Select(profile => profile.ApiKey)
                .FirstOrDefault(key => !string.IsNullOrWhiteSpace(key)
                    && !string.Equals(key, "EMPTY", StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(apiKey))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

            foreach (var header in profiles.SelectMany(profile => profile.Headers))
            {
                request.Headers.Remove(header.Key);
                request.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            using var response = await client.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return Unavailable(
                    baseUrl,
                    modelsUrl,
                    $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
            }

            await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(responseStream, cancellationToken: cancellationToken);
            if (!document.RootElement.TryGetProperty("data", out var data)
                || data.ValueKind != JsonValueKind.Array)
            {
                return Unavailable(baseUrl, modelsUrl, "Response did not contain a standard data array.");
            }

            var advertisedModels = data
                .EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.Object
                    && item.TryGetProperty("id", out var id)
                    && id.ValueKind == JsonValueKind.String)
                .Select(item => item.GetProperty("id").GetString())
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => id!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var advertisedSet = advertisedModels.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var missingModels = expectedModels
                .Where(model => !advertisedSet.Contains(model))
                .ToList();

            return new ModelListProbeResult(
                baseUrl,
                modelsUrl,
                IsAvailable: true,
                AdvertisedModels: advertisedModels,
                MissingModels: missingModels,
                Error: null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Unavailable(
                baseUrl,
                modelsUrl,
                $"Request timed out after {timeout.TotalSeconds:0.#} seconds.");
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or IOException)
        {
            return Unavailable(baseUrl, modelsUrl, ex.Message);
        }
    }

    private static ModelListProbeResult Unavailable(
        string baseUrl,
        string modelsUrl,
        string error)
        => new(
            baseUrl,
            modelsUrl,
            IsAvailable: false,
            AdvertisedModels: Array.Empty<string>(),
            MissingModels: Array.Empty<string>(),
            Error: error);
}
