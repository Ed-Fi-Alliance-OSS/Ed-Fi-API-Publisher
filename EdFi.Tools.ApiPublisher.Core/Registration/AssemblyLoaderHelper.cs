// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using EdFi.Tools.ApiPublisher.Core.Extensions;
using Serilog;

namespace EdFi.Ods.Api.Helpers
{
    public static class AssemblyLoaderHelper
    {
        private static readonly ILogger _logger = Log.ForContext(typeof(AssemblyLoaderHelper));
        private const string AssemblyMetadataSearchString = "assemblyMetadata.json";

        public static void LoadAssembliesFromExecutingFolder(bool includeFramework = false)
        {
            // Storage to ensure not loading the same assembly twice and optimize calls to GetAssemblies()
            IDictionary<string, bool> loadedByAssemblyName = new ConcurrentDictionary<string, bool>();

            LoadAssembliesFromExecutingFolder();

            int alreadyLoaded = loadedByAssemblyName.Keys.Count;

            var sw = new Stopwatch();
            _logger.Debug($"Already loaded assemblies:");

            CacheAlreadyLoadedAssemblies(loadedByAssemblyName, includeFramework);

            // Loop on loaded assemblies to load dependencies (it includes Startup assembly so should load all the dependency tree)
            foreach (Assembly nonFrameworkAssemblies in AppDomain.CurrentDomain.GetAssemblies()
                .Where(a => IsNotNetFramework(a.FullName)))
            {
                LoadReferencedAssembly(nonFrameworkAssemblies, loadedByAssemblyName, includeFramework);
            }

            _logger.Debug(
                $"Assemblies loaded after scan ({loadedByAssemblyName.Keys.Count - alreadyLoaded} assemblies in {sw.ElapsedMilliseconds} ms):");

            void LoadAssembliesFromExecutingFolder()
            {
                // Load referenced assemblies into the domain. This is effectively the same as EnsureLoaded in common
                // however the assemblies are linked in the project.
                var directoryInfo = new DirectoryInfo(
                    Path.GetDirectoryName(
                        Assembly.GetExecutingAssembly()
                            .Location));

                _logger.Debug($"Loaded assemblies from executing folder: '{directoryInfo.FullName}'");

                foreach (FileInfo assemblyFilesToLoad in directoryInfo.GetFiles("*.dll")
                    .Where(fi => ShouldLoad(fi.Name, loadedByAssemblyName, includeFramework)))
                {
                    _logger.Debug($"{assemblyFilesToLoad.Name}");
                    Assembly.LoadFrom(assemblyFilesToLoad.FullName);
                }
            }
        }

        private static void LoadReferencedAssembly(Assembly assembly, IDictionary<string, bool> loaded,
            bool includeFramework = false)
        {
            // Check all referenced assemblies of the specified assembly
            foreach (var referencedAssembliesToLoad in assembly.GetReferencedAssemblies()
                .Where(a => ShouldLoad(a.FullName, loaded, includeFramework)))
            {
                // Load the assembly and load its dependencies
                LoadReferencedAssembly(Assembly.Load(referencedAssembliesToLoad), loaded, includeFramework); // AppDomain.CurrentDomain.Load(name)
                loaded.TryAdd(referencedAssembliesToLoad.FullName, true);
                _logger.Debug($"Referenced assembly => '{referencedAssembliesToLoad.FullName}'");
            }
        }

        private static void CacheAlreadyLoadedAssemblies(IDictionary<string, bool> loaded, bool includeFramework = false)
        {
            foreach (var alreadyLoadedAssemblies in AppDomain.CurrentDomain.GetAssemblies()
                .Where(a => ShouldLoad(a.FullName, loaded, includeFramework)))
            {
                loaded.TryAdd(alreadyLoadedAssemblies.FullName, true);
                _logger.Debug($"Assembly '{alreadyLoadedAssemblies.FullName}' was already loaded.");
            }
        }

        private static bool ShouldLoad(string assemblyName, IDictionary<string, bool> loaded, bool includeFramework = false)
        {
            return (includeFramework || IsNotNetFramework(assemblyName))
                   && !loaded.ContainsKey(assemblyName);
        }

        private static bool IsNotNetFramework(string assemblyName)
        {
            return !assemblyName.StartsWithIgnoreCase("Microsoft.")
                   && !assemblyName.StartsWithIgnoreCase("System.")
                   && !assemblyName.StartsWithIgnoreCase("Newtonsoft.")
                   && assemblyName != "netstandard"
                   && !assemblyName.StartsWithIgnoreCase("Autofac");
        }
    }
}
