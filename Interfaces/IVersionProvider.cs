namespace Ayuda.AppRouter.Interfaces;

public interface IVersionProvider
{
    Task<string?> GetVersion(string host);
}
