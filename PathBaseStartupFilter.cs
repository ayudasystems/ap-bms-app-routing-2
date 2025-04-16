using Microsoft.Extensions.Options;

namespace Ayuda.AppRouter;

public class PathBaseStartupFilter : IStartupFilter
{    
    private readonly string _pathBase;

    public PathBaseStartupFilter(IOptions<RouterOptions> options)
    {
        _pathBase = options.Value.PathBase;
    }
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        return (app =>
        {
            app.UsePathBase((PathString) _pathBase);
            next(app);
        });
    }
}