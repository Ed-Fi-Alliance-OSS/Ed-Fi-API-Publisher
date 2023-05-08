// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.Tools.ApiPublisher.Core.Processing;
using Microsoft.Extensions.Configuration;
using Serilog;
using System.Threading.Tasks;

namespace EdFi.Tools.ApiPublisher.ConfigurationStore.Plaintext
{
    public class PlaintextChangeVersionProcessedWriter : IChangeVersionProcessedWriter
    {
        private readonly ILogger _logger = Log.Logger.ForContext(typeof(PlaintextChangeVersionProcessedWriter));
        
        public Task SetProcessedChangeVersionAsync(
            string sourceConnectionName,
            string targetConnectionName,
            long changeVersion,
            IConfigurationSection configurationStoreSection)
        {
            _logger.Warning("Plaintext connections don't support writing back updated change versions.");
            return Task.FromResult(0);
        }
    }
}
