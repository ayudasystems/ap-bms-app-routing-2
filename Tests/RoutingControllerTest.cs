using Ayuda.AppRouter.Controllers;
using Ayuda.AppRouter.Helpers;
using Ayuda.AppRouter.Interfaces;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Moq;
using Moq.Protected;
using System.Net;
using System.Text;
using Xunit; // Added for testing attributes and Assert

namespace Ayuda.AppRouter.Tests
{
    public class RoutingControllerTest
    {
        private readonly Mock<IVersionProvider> _mockVersionProvider;
        private readonly Mock<IIisPathFinder> _mockPathFinder;
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
            _mockPathFinder = new Mock<IIisPathFinder>();
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
            _mockPathFinder.Setup(pf => pf.GetPriorityPhysicalPathForVersion(It.IsAny<string>()))
                .Returns((string)null);
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
                _mockPathFinder.Object,
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
            var okResult = Assert.IsType<OkObjectResult>(result);
            Assert.Equal("X-Tenant-Host header missing.", okResult.Value);
            
            _mockVersionProvider.Verify(vp => vp.GetVersion(It.IsAny<string>()), Times.Never,
                "VersionProvider should not be called when X-Tenant-Host is missing.");

            _mockPathFinder.Verify(pf => pf.GetPriorityPhysicalPathForVersion(It.IsAny<string>()), Times.Never,
                "PathFinder should not be called when X-Tenant-Host is missing.");

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
            var notFoundResult = Assert.IsType<NotFoundObjectResult>(result);
            Assert.Equal("Version not found for host.", notFoundResult.Value);
            _mockVersionProvider.Verify(vp => vp.GetVersion(tenantHost), Times.Once,
                "VersionProvider should be called once.");
            _mockPathFinder.Verify(pf => pf.GetPriorityPhysicalPathForVersion(It.IsAny<string>()), Times.Never,
                "PathFinder should not be called when version is null.");

            _mockHttpClientFactory.Verify(hcf => hcf.CreateClient(It.IsAny<string>()), Times.Never,
                "HttpClientFactory should not be called when version is null.");
        }
        
        [Fact]
        public async Task Route_When_Highest_Priority_Path_Differs_From_Tenant_Sends_Correct_Header()
        {
            // Arrange
            // This test is going to try an mimic as close as possible to the prod bug
            string tenantHost = "test@ayudalabs.com";
            string version = "60843"; 
            string physicalPath = "C:\\AyudaApps\\Cloud NA\\BmsInternalWebService\\7.3023.60843.1";
            string requestPath = "/BMSInternalWebService/ReportService.asmx"; 
            string queryString = "";
            string expectedProxiedUrl = $"http://{_defaultRouterOptions.RedirectHost}/{version}{requestPath}{queryString}";

            var httpContext = CreateHttpContext();
            httpContext.Request.Headers["X-Tenant-Host"] = tenantHost;
            httpContext.Request.Method = HttpMethods.Post; // Example method
            httpContext.Request.Path = requestPath;
            httpContext.Request.QueryString = new QueryString(queryString);
            // Add some body content for testing pass-through
            var requestBody = "Content of the request body";
            var requestBodyStream = new MemoryStream(Encoding.UTF8.GetBytes(requestBody));
            httpContext.Request.Body = requestBodyStream;
            httpContext.Request.ContentLength = requestBodyStream.Length;
            httpContext.Request.ContentType = "text/xml";
            
            _mockPathFinder.Setup(pf => pf.GetPriorityPhysicalPathForVersion(version))
                           .Returns(physicalPath);

            // Setup backend response (doesn't matter much, just needs to be success for this test)
            SetupMockBackendResponse(HttpStatusCode.OK, "Backend Response OK");

            // Variable to capture the outgoing request
            HttpRequestMessage capturedRequest = null;

            // Configure the handler to capture the request *before* returning the response
            _mockHttpMessageHandler.Protected()
                .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
                .Callback<HttpRequestMessage, CancellationToken>((req, ct) => capturedRequest = req)
                .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("Backend Response OK") });


            var controller = CreateController(httpContext);

            // Act
            var result = await controller.Route();

            // Assert
            Assert.IsType<EmptyResult>(result);
            _mockHttpMessageHandler.Protected().Verify("SendAsync", Times.Once(), ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>());
            Assert.NotNull(capturedRequest); // Ensure request was captured

            // 2. *** Verify the INCORRECT URL was constructed (based on code BEFORE fix) ***
            Assert.Equal(expectedProxiedUrl, capturedRequest.RequestUri.ToString());

            
            Assert.Equal(HttpMethod.Post, capturedRequest.Method);
            string capturedBody = await capturedRequest.Content.ReadAsStringAsync();
            Assert.Equal(requestBody, capturedBody);
            Assert.Equal(httpContext.Request.ContentType, capturedRequest.Content.Headers.ContentType?.ToString());

            
            Assert.True(capturedRequest.Headers.TryGetValues("X-Ayuda-Resolved-Path", out var headerValues), "X-Ayuda-Resolved-Path header missing");
            Assert.Equal(physicalPath, headerValues.First());
            
            _mockVersionProvider.Verify(vp => vp.GetVersion(tenantHost), Times.Once);
            _mockPathFinder.Verify(pf => pf.GetPriorityPhysicalPathForVersion(version), Times.Once);
            _mockHttpClientFactory.Verify(hcf => hcf.CreateClient(It.IsAny<string>()), Times.Once);
        }

    }
}