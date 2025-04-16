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
    IIisPathFinder iisPathFinder) 
    : Controller
{
    private readonly IHttpClientFactory _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
    private readonly RouterOptions _routerOptions = options?.Value ?? throw new ArgumentNullException(nameof(options));
    private readonly IVersionProvider _versionProvider = versionProvider ?? throw new ArgumentNullException(nameof(versionProvider));
    private readonly IIisPathFinder _iisPathFinder = iisPathFinder ?? throw new ArgumentNullException(nameof(iisPathFinder));  // Add this field
    
    [Route("/{**path}")]
    public async Task<IActionResult> Route(CancellationToken cancellationToken = default)
    {
        RoutingController routingController = this;
        string host = routingController.Request.Headers["X-Tenant-Host"].FirstOrDefault<string>();
        if (host == null)
            return routingController.Ok();
        string version = await routingController._versionProvider.GetVersion(host); //returns only last part of mamba versioning ex: 7.3030.60843 -> 60843
        
        if (version == null)
            return routingController.NotFound();
        string? priorityPath = _iisPathFinder.GetPriorityPathForVersion(version);
    
        // If no priority path is found, we can either use the default behavior or return an error
        if (priorityPath == null)
        {
           Console.WriteLine($"No priority path found for version {version}");
            // Decide what to do - either continue with default behavior or return error
            // return NotFound($"No physical path found for version {version}");
        }

        HttpMethod method = new HttpMethod(Request.Method);
        string path = Request.Path.HasValue ? Request.Path.ToString() : "/";
        string url = $"http://{_routerOptions.RedirectHost}/{version}{path}{Request.QueryString}"; // "https://bms-internal-web-service-01.ayudasky.com/60843/

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

        HttpResponseMessage httpResponseMessage =
            await routingController._httpClientFactory.CreateClient().SendAsync(request, cancellationToken);
        
        if (cancellationToken.IsCancellationRequested)
            return routingController.BadRequest();
        
        foreach (KeyValuePair<string, IEnumerable<string>> header in httpResponseMessage.Headers)
            routingController.Response.Headers.Append(header.Key, (StringValues)header.Value.ToArray());
        
        routingController.Response.StatusCode = (int)httpResponseMessage.StatusCode;
        routingController.Response.ContentLength = httpResponseMessage.Content.Headers.ContentLength;
        routingController.Response.ContentType =
            httpResponseMessage.Content.Headers.ContentType?.ToString() ?? string.Empty;
        
        byte[] responseBytes = await httpResponseMessage.Content.ReadAsByteArrayAsync(cancellationToken);
        await Response.BodyWriter.WriteAsync(responseBytes, cancellationToken);
        return new EmptyResult();
    }
}