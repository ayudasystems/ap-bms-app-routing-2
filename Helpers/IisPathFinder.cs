using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Ayuda.AppRouter.Helpers
{
    public interface IIisPathFinder
    {
        string? GetPriorityPathForVersion(string version);
    }

    public class IisPathFinder : IIisPathFinder
    {
        private readonly ILogger<IisPathFinder> _logger;
        private readonly string _baseDirectory;

        // Environment priority: Cloud > Preview > Labs
        private readonly string[] _environmentPriority = new[] { "Cloud NA", "Preview CA", "Labs NA" };
        
        //local path for now to test
        public IisPathFinder(ILogger<IisPathFinder> logger, string baseDirectory = @"C:\Broadsign\AyudaApps")
        {
            _logger = logger;
            _baseDirectory = baseDirectory;
        }

        public string? GetPriorityPathForVersion(string shortVersion)
        {
            _logger.LogInformation($"Looking for priority path for version: {shortVersion}");

            var availablePaths = new Dictionary<string, string>();

            // check for direct version matches (ex:60843)
            foreach (var env in _environmentPriority)
            {
                var directPath = Path.Combine(_baseDirectory, $"{env}", "BmsInternalWebService", shortVersion);
                if (Directory.Exists(directPath))
                {
                    _logger.LogInformation($"Found direct path for {env}: {directPath}");
                    availablePaths[$"{env}"] = directPath;
                }
            }

            // If no direct matches, look for the full version format (7.3023.60843.1 or 7.3023.60843.1_1)
            if (!availablePaths.Any())
            {
                _logger.LogInformation("No direct matches found, looking for full version formats");

                foreach (var env in _environmentPriority)
                {
                    var envRegionPath = Path.Combine(_baseDirectory, $"{env}", "BmsInternalWebService");
                    if (!Directory.Exists(envRegionPath))
                        continue;
                    
                    var matchingDirs = Directory.GetDirectories(envRegionPath)
                        .Where(d =>
                        {
                            var dirName = Path.GetFileName(d);
                            return dirName.EndsWith($".{shortVersion}") ||
                                   dirName.EndsWith($".{shortVersion}.1") ||
                                   dirName == shortVersion;
                        })
                        .ToList();

                    if (matchingDirs.Any())
                    {
                        var fullVersionPath = matchingDirs.First();
                        _logger.LogInformation($"Found full version path for {env}: {fullVersionPath}");
                        availablePaths[$"{env}"] = Path.Combine(envRegionPath, shortVersion);
                    }
                }
            }

            // Return the highest priority path based on environment
            foreach (var env in _environmentPriority)
            {
                if (availablePaths.TryGetValue($"{env}", out var path))
                {
                    _logger.LogInformation($"Selected priority path: {path}");
                    return path;
                }
            }

            _logger.LogWarning($"No path found for version {shortVersion}");
            return null;
        }
    }
}