using Ayuda.AppRouter.Controllers;
using Ayuda.AppRouter.Helpers;
using Ayuda.AppRouter.Interfaces;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Moq;
using Moq.Protected;
using System.Net;
using System.Text;
using Microsoft.AspNetCore.Http.HttpResults;
using Xunit; // Added for testing attributes and Assert

namespace Ayuda.AppRouter.Tests
{
    public class RoutingControllerTest
    {
        private readonly Mock<IVersionProvider> _mockVersionProvider;
        private readonly Mock<ILogger<RoutingController>> _mockLogger;
        private readonly Mock<IOptions<RouterOptions>> _mockOptions;
        private readonly Mock<IHttpClientFactory> _mockHttpClientFactory;
        private readonly Mock<HttpMessageHandler> _mockHttpMessageHandler;

        private readonly RouterOptions _defaultRouterOptions;
        private readonly HttpClient _httpClient;

        public RoutingControllerTest()
        {
            var configuration = new ConfigurationBuilder()
                .SetBasePath(Directory.GetCurrentDirectory())
                .AddJsonFile("appsettings.Development.json", optional: true) // optional in case not well configured
                .Build();

            _mockVersionProvider = new Mock<IVersionProvider>();
            _mockLogger = new Mock<ILogger<RoutingController>>();
            _mockOptions = new Mock<IOptions<RouterOptions>>();
            _mockHttpClientFactory = new Mock<IHttpClientFactory>();
            _mockHttpMessageHandler = new Mock<HttpMessageHandler>();

            
            string redirectHost = configuration.GetValue<string>("Router:RedirectHost") ??
                                  "bms-internal-web-service-01.ayudasky.com.local";
            string pathBase = configuration.GetValue<string>("Router:PathBase") ??
                              "/BMSInternalWebService"; // kept prod path since the local IIS configuration is setup to use the same path as prod.

            _defaultRouterOptions = new RouterOptions
            {
                RedirectHost = redirectHost,
                PathBase = pathBase
            };

            _mockOptions.Setup(o => o.Value).Returns(_defaultRouterOptions);
            _httpClient = new HttpClient(_mockHttpMessageHandler.Object);
            _mockHttpClientFactory.Setup(_ => _.CreateClient(It.IsAny<string>())).Returns(_httpClient);
            _mockVersionProvider.Setup(vp => vp.GetVersion(It.IsAny<string>()))
                .ReturnsAsync("60843"); // TODO: make it more dynamic ? 
            // default resp is 200
            SetupMockBackendResponse(HttpStatusCode.OK);
        }

        // Create HttpContext
        private DefaultHttpContext CreateHttpContext()
        {
            var httpContext = new DefaultHttpContext();
            httpContext.Request.Method = HttpMethods.Get;
            httpContext.Request.Scheme = "http";
            httpContext.Request.Host = new HostString("localhost");
            httpContext.Request.Path = "/";
            httpContext.Response.Body = new MemoryStream();
            return httpContext;
        }

        // Create Controller
        private RoutingController CreateController(HttpContext httpContext)
        {
            var controller = new RoutingController(
                _mockVersionProvider.Object,
                _mockHttpClientFactory.Object,
                _mockOptions.Object,
                _mockLogger.Object
            )
            {
                ControllerContext = new ControllerContext()
                {
                    HttpContext = httpContext
                }
            };
            return controller;
        }

        // SetupMockBackendResponse
        private void SetupMockBackendResponse(HttpStatusCode statusCode, string responseContent = null,
                    Dictionary<string, string> responseHeaders = null)
        {
            var mockResponse = new HttpResponseMessage(statusCode);
            if (responseContent != null)
            {
                mockResponse.Content = new StringContent(responseContent, Encoding.UTF8, "text/plain");
            }
            if (responseHeaders != null)
            {
                foreach (var header in responseHeaders)
                {
                    mockResponse.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }
            }
            _mockHttpMessageHandler.Protected()
                .Setup<Task<HttpResponseMessage>>(
                    "SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
                .ReturnsAsync(mockResponse)
                .Verifiable();
        }
        

        [Fact]
        public async Task Route_Missing_Tenant_HostHeader_Returns_Ok_With_Message()
        {
            // Arrange
            var httpContext = CreateHttpContext();
            var controller = CreateController(httpContext);

            // Act
            var result = await controller.Route();

            // Assert
            var badRequest = Assert.IsType<BadRequestObjectResult>(result);
            Assert.Equal("X-Tenant-Host header missing.", badRequest.Value);
            
            _mockVersionProvider.Verify(vp => vp.GetVersion(It.IsAny<string>()), Times.Never,
                "VersionProvider should not be called when X-Tenant-Host is missing.");

            _mockHttpClientFactory.Verify(hcf => hcf.CreateClient(It.IsAny<string>()), Times.Never,
                "HttpClientFactory should not be called when X-Tenant-Host is missing.");
        }
        
        
        [Fact]
        public async Task Route_Null_Version_From_Provider_Returns_Not_Found()
        {
            // Arrange
            string tenantHost = "test@ayudalabs.com";
            var httpContext = CreateHttpContext();
            httpContext.Request.Headers["X-Tenant-Host"] = tenantHost;
            
            _mockVersionProvider.Setup(vp => vp.GetVersion(tenantHost))
                .ReturnsAsync((string)null); // Simulate version not found

            var controller = CreateController(httpContext);

            // Act
            var result = await controller.Route();

            // Assert
             Assert.IsType<NotFoundObjectResult>(result);
            _mockVersionProvider.Verify(vp => vp.GetVersion(tenantHost), Times.Once,
                "VersionProvider should be called once.");

            _mockHttpClientFactory.Verify(hcf => hcf.CreateClient(It.IsAny<string>()), Times.Never,
                "HttpClientFactory should not be called when version is null.");
        }
    }
}