
using System.Net.Http.Headers;
using Ayuda.AppRouter.Helpers;
using Ayuda.AppRouter.Interfaces;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;


namespace Ayuda.AppRouter.Controllers // Use your actual namespace
{
    public class RoutingController : Controller
    {
        private readonly IVersionProvider _versionProvider;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly RouterOptions _routerOptions;
        private readonly ILogger<RoutingController> _logger;

        public RoutingController(
            IVersionProvider versionProvider,
            IHttpClientFactory httpClientFactory,
            IOptions<RouterOptions> options,
            ILogger<RoutingController> logger)
        {
            _versionProvider = versionProvider ?? throw new ArgumentNullException(nameof(versionProvider));
            _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
            _routerOptions = options?.Value ?? throw new ArgumentNullException(nameof(options));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        [Route("/{**path}")]
        public async Task<IActionResult> Route(CancellationToken cancellationToken = default)
        {
            string host = Request.Headers["X-Tenant-Host"].FirstOrDefault();
            if (string.IsNullOrEmpty(host))
            {
                _logger.LogWarning("Request received without X-Tenant-Host header.");
                return BadRequest("X-Tenant-Host header missing.");
            }

            var detectedEnv = TenantEnvHelper.GetEnvironmentFromHost(host);
            _logger.LogInformation("Request received for Host: {Host} (Detected Env: {Environment})", host, detectedEnv);

            string version = await _versionProvider.GetVersion(host);
            _logger.LogInformation("Version determined for Host {Host}: {Version}", host, version);
            if (string.IsNullOrEmpty(version))
            {
                _logger.LogWarning("Version could not be determined for host {Host}", host);
                return NotFound($"Version not found for host {host}.");
            }

            HttpMethod method = new HttpMethod(Request.Method);
            
            string path = Request.Path.HasValue ? Request.Path.ToString() : "/"; // Get Path, default to "/"
            string url = $"http://{_routerOptions.RedirectHost.TrimEnd('/')}/{version}{path}{Request.QueryString}";

            _logger.LogInformation("Proxying request to initial URL: {Url}", url);

            HttpRequestMessage proxiedRequest = new HttpRequestMessage(method, url);
            bool hasRequestBody = Request.ContentLength.HasValue && Request.ContentLength > 0;
            if (hasRequestBody)
            {
                 proxiedRequest.Content = new StreamContent(Request.Body);
                 if (Request.Headers.ContentType.Any())
                 {
                    MediaTypeHeaderValue.TryParse(Request.Headers.ContentType, out var contentTypeValue);
                    proxiedRequest.Content.Headers.ContentType = contentTypeValue;
                 }
            }
            
            foreach (var header in Request.Headers)
            {
                 string key = header.Key;
                 if (key.StartsWith(":", StringComparison.Ordinal) ||
                     key.Equals("Host", StringComparison.OrdinalIgnoreCase) ||
                     (hasRequestBody && key.StartsWith("Content-", StringComparison.OrdinalIgnoreCase)) ||
                     key.Equals("Connection", StringComparison.OrdinalIgnoreCase) ||
                     key.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase) ||
                     key.Equals("Keep-Alive", StringComparison.OrdinalIgnoreCase) ||
                     key.Equals("Upgrade", StringComparison.OrdinalIgnoreCase))
                 {
                    continue;
                 }
                 proxiedRequest.Headers.TryAddWithoutValidation(key, header.Value.AsEnumerable());
            }
            
            try
            {
                 HttpResponseMessage backendResponse = await _httpClientFactory
                     .CreateClient()
                     .SendAsync(proxiedRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

                 if (cancellationToken.IsCancellationRequested)
                 {
                     _logger.LogWarning("Request cancelled by client during backend request for Host: {Host}, Version: {Version}", host, version);
                     return StatusCode(StatusCodes.Status499ClientClosedRequest);
                 }

                 Response.StatusCode = (int)backendResponse.StatusCode;

                 foreach (var header in backendResponse.Headers)
                 {
                     Response.Headers.Append(header.Key, new StringValues(header.Value.ToArray()));
                 }

                 foreach (var header in backendResponse.Content.Headers)
                 {
                     Response.Headers.Append(header.Key, new StringValues(header.Value.ToArray()));
                 }


                 await backendResponse.Content.CopyToAsync(Response.Body, cancellationToken);

                 _logger.LogInformation(
                     "Successfully proxied request for Host: {Host}, Version: {Version}. Backend Status: {StatusCode}", host,
                     version, backendResponse.StatusCode);

                 return new EmptyResult();
            }
            catch (HttpRequestException ex)
            {
                 _logger.LogError(ex, "Proxy connection error for Host {Host}, Version {Version} to {Url}", host, version, url);
                 return StatusCode(StatusCodes.Status502BadGateway, $"Error connecting to backend service ({_routerOptions.RedirectHost}).");
            }
            catch (TaskCanceledException ex)
            {
                 if (ex.InnerException is TimeoutException || !cancellationToken.IsCancellationRequested)
                 {
                     _logger.LogError(ex, "Proxy timeout for Host {Host}, Version {Version} to {Url}", host, version, url);
                     return StatusCode(StatusCodes.Status504GatewayTimeout, "Backend service timed out.");
                 }
                 else
                 {
                     _logger.LogWarning("Proxy TaskCanceledException (likely client cancellation) for Host: {Host}, Version: {Version}", host, version);
                     return StatusCode(StatusCodes.Status499ClientClosedRequest);
                 }
            }
            catch (Exception ex)
            {
                 _logger.LogError(ex, "Unexpected error during proxy for Host {Host}, Version {Version} to {Url}", host, version, url);
                 return StatusCode(StatusCodes.Status500InternalServerError, "An unexpected error occurred while routing the request.");
            }
        }
    }
}