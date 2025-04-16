using Ayuda.AppRouter;
using Ayuda.AppRouter.Helpers;
using Ayuda.AppRouter.Interfaces;
using Ayuda.AppRouter.Mamba;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
builder.Services.AddControllers();
builder.Services.AddHttpClient();
builder.Services.Configure<RouterOptions>( builder.Configuration.GetSection("Router"));
builder.Services.AddTransient<IStartupFilter, PathBaseStartupFilter>();
builder.Services.Configure<MambaOptions>(builder.Configuration.GetSection("Mamba"));
builder.Services.AddTransient<IVersionProvider, MambaVersionProvider>();
builder.Services.AddSingleton<IIisPathFinder, IisPathFinder>();
WebApplication endpoints = builder.Build();
endpoints.MapControllers();
endpoints.Run();
