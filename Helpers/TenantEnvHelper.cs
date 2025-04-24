using Ayuda.AppRouter.Controllers;
namespace Ayuda.AppRouter.Helpers
{
    public static class TenantEnvHelper
    {
        public enum TenantEnvironment
        {
            Cloud,
            Labs,
            Preview
        }

        public static TenantEnvironment GetEnvironmentFromHost(string tenantHost)
        {
            if (tenantHost.Contains("ayudacloud.com", StringComparison.OrdinalIgnoreCase))
                return TenantEnvironment.Cloud;
            if (tenantHost.Contains("ayudalabs.com", StringComparison.OrdinalIgnoreCase))
                return TenantEnvironment.Labs;
            if (tenantHost.Contains("ayudapreview.com", StringComparison.OrdinalIgnoreCase))
                return TenantEnvironment.Preview;

            throw new ArgumentException("Invalid tenant host.", nameof(tenantHost));
        }
        
    }
}