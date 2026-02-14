using System.Net.Http.Json;
using System.Text.Json;
using WowProxy.Core.Abstractions.Models.Clash;

namespace WowProxy.Infrastructure;

public class ClashApiClient
{
    private readonly HttpClient _client;
    private readonly string _baseUrl;
    private readonly string? _secret;

    public ClashApiClient(int port, string? secret)
    {
        _baseUrl = $"http://127.0.0.1:{port}";
        _secret = secret;
        _client = new HttpClient();
        
        if (!string.IsNullOrWhiteSpace(_secret))
        {
            _client.DefaultRequestHeaders.Add("Authorization", $"Bearer {_secret}");
        }
    }

    public async Task<ConnectionsResponse?> GetConnectionsAsync()
    {
        try
        {
            return await _client.GetFromJsonAsync<ConnectionsResponse>($"{_baseUrl}/connections");
        }
        catch
        {
            return null;
        }
    }

    public async Task<bool> CloseConnectionAsync(string connectionId)
    {
        try
        {
            var response = await _client.DeleteAsync($"{_baseUrl}/connections/{connectionId}");
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }
}
