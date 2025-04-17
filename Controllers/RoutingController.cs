using Ayuda.AppRouter.Helpers;
using Ayuda.AppRouter.Interfaces;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

namespace Ayuda.AppRouter.Controllers;

public class RoutingController(
    IVersionProvider versionProvider,
    IHttpClientFactory httpClientFactory,
    IOptions<RouterOptions> options,
    IIisPathFinder iisPathFinder,
    ILogger<RoutingController> logger)
    : Controller
{
    private readonly IHttpClientFactory _httpClientFactory =
        httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));

    private readonly RouterOptions _routerOptions = options?.Value ?? throw new ArgumentNullException(nameof(options));

    private readonly IVersionProvider _versionProvider =
        versionProvider ?? throw new ArgumentNullException(nameof(versionProvider));

    private readonly IIisPathFinder _iisPathFinder =
        iisPathFinder ?? throw new ArgumentNullException(nameof(iisPathFinder));

    private readonly ILogger<RoutingController> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    [Route("/{**path}")]
    public async Task<IActionResult> Route(CancellationToken cancellationToken = default)
    {
        RoutingController routingController = this;
        string host = routingController.Request.Headers["X-Tenant-Host"].FirstOrDefault<string>();
        if (host == null)
        {
            _logger.LogWarning("Request received without X-Tenant-Host header.");
            return Ok("X-Tenant-Host header missing.");
        }

        string version = await routingController._versionProvider.GetVersion(host); //returns only last part of mamba versioning ex: 7.3030.60843 -> 60843
        if (version == null)
        {
            _logger.LogWarning("Version could not be determined for host {Host}", host); // Added logging
            return routingController.NotFound("Version not found for host.");
        }

        string? priorityPath = _iisPathFinder.GetPriorityPhysicalPathForVersion(version);

        if (priorityPath == null)
        {
            _logger.LogWarning(
                "No priority physical path found by IisPathFinder for version {Version}. Request for host {Host} might be proxied to a potentially incorrect or unavailable backend.",
                version, host);
        }
        else
        {
            _logger.LogInformation("IisPathFinder determined priority path for version {Version} to be: {Path}",
                version, priorityPath);
        }

        HttpMethod method = new HttpMethod(Request.Method);
        string path = Request.Path.HasValue ? Request.Path.ToString() : "/";
        // The target URL  uses the short version in the path, as required by the backend virtual dir structure.
        string url = $"http://{_routerOptions.RedirectHost}/{version}{path}{Request.QueryString}";

        _logger.LogInformation("Routing request for Host: {Host}, Version: {Version} to URL: {Url}", host, version,
            url);

        HttpRequestMessage request = new HttpRequestMessage(method, url);
        request.Content = new StreamContent(routingController.Request.Body);

        foreach (KeyValuePair<string, StringValues> header in
                 routingController.Request.Headers)
        {
            if (header.Key[0] != ':' && header.Key != "Host")
            {
                if (header.Key.StartsWith("Content-"))
                    request.Content.Headers.Add(header.Key, header.Value.AsEnumerable<string>());
                else
                    request.Headers.Add(header.Key, header.Value.AsEnumerable<string>());
            }
        }

        if (!string.IsNullOrEmpty(priorityPath))
        {
            // Use a specific header name, e.g., "X-Ayuda-Resolved-Path"
            const string resolvedPathHeader = "X-Ayuda-Resolved-Path";
            request.Headers.Remove(resolvedPathHeader); // Remove if already exists for any reason
            request.Headers.Add(resolvedPathHeader, priorityPath);
            _logger.LogInformation("Added header {HeaderName}: {HeaderValue}", resolvedPathHeader, priorityPath);
        }
        else
        {
            _logger.LogWarning(
                "No priority physical path found by IisPathFinder for version {Version}. Request for host {Host} will be proxied without X-Ayuda-Resolved-Path header.",
                version, host);
        }

        try
        {
            HttpResponseMessage httpResponseMessage = await _httpClientFactory
                .CreateClient()
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            if (cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning("Request cancelled for Host: {Host}, Version: {Version}", host, version);
                return BadRequest("Request cancelled.");
            }

            Response.StatusCode = (int)httpResponseMessage.StatusCode;

            // copy response headers
            foreach (var header in httpResponseMessage.Headers)
            {
                Response.Headers.Append(header.Key, new StringValues(header.Value.ToArray()));
            }

            foreach (var header in httpResponseMessage.Content.Headers)
            {
                Response.Headers.Append(header.Key, new StringValues(header.Value.ToArray()));
            }

            // Stream the response body
            await httpResponseMessage.Content.CopyToAsync(Response.Body, cancellationToken);

            _logger.LogInformation(
                "Successfully proxied request for Host: {Host}, Version: {Version}. Backend Status: {StatusCode}", host,
                version, httpResponseMessage.StatusCode);
            return new EmptyResult(); // the response has been handled
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Error proxying request for Host {Host}, Version {Version} to {Url}", host, version,
                url);
            return StatusCode(StatusCodes.Status502BadGateway, $"Error connecting to backend service: {ex.Message}");
        }
        catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
        {
            _logger.LogError(ex, "Timeout proxying request for Host {Host}, Version {Version} to {Url}", host, version,
                url);
            return StatusCode(StatusCodes.Status504GatewayTimeout, "Backend service timed out.");
        }
        catch (Exception ex) // Catch-all for any other stuff
        {
            _logger.LogError(ex, "Unexpected error proxying request for Host {Host}, Version {Version} to {Url}", host,
                version, url);
            return StatusCode(StatusCodes.Status500InternalServerError,
                "An unexpected error occurred while routing the request.");
        }
    }
}