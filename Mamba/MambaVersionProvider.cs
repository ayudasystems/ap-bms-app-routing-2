using System.Net.Http.Headers;
using Ayuda.AppRouter.Interfaces;
using Microsoft.Extensions.Options;

namespace Ayuda.AppRouter.Mamba;

public class MambaVersionProvider : IVersionProvider
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly MambaOptions _mambaOptions;

    public MambaVersionProvider(
        IHttpClientFactory httpClientFactory,
        IOptions<MambaOptions> options)
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _mambaOptions = options.Value ?? throw new ArgumentNullException(nameof(options));
    }

    public async Task<string?> GetVersion(string host)
    {
        HttpResponseMessage httpResponseMessage = await _httpClientFactory.CreateClient().SendAsync(new HttpRequestMessage(HttpMethod.Get, _mambaOptions.ApiUrl + "/get_version/" + host + "/")
        {
            Headers = {
                Authorization = new AuthenticationHeaderValue("Token", _mambaOptions.Token)
            }
        });
        
        if (httpResponseMessage.IsSuccessStatusCode)
        {
            MambaVersionResponse? mambaVersionResponse = await httpResponseMessage.Content.ReadFromJsonAsync<MambaVersionResponse>();
            if (mambaVersionResponse != null)
            {
                string[] strArray = mambaVersionResponse.Version.Split('.');
                if (strArray.Length >= 3)
                    return strArray[2]; // Return the third part of the version (e.g., 60843)
            }
        }
        return null;
    }
    
    private class MambaVersionResponse(string version)
    {
        public string Version { get; set; } = version;
    }
}