// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.Tools.ApiPublisher.Core.Configuration;
using EdFi.Tools.ApiPublisher.Core.Processing.Messages;
using System;
using System.Collections.Generic;
using System.Threading.Tasks.Dataflow;

namespace EdFi.Tools.ApiPublisher.Core.Processing.Blocks
{
    public class CreateBlocksRequest
    {
        private static readonly IReadOnlySet<string> NoRetryPipelines = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public CreateBlocksRequest(
            Options options,
            AuthorizationFailureHandling[] authorizationFailureHandling,
            ITargetBlock<ErrorItemMessage> errorHandlingBlock,
            Func<string> javaScriptModuleFactory,
            IReadOnlySet<string> authorizationRetryPipelineResourcePaths = null)
        {
            Options = options;
            AuthorizationFailureHandling = authorizationFailureHandling;
            ErrorHandlingBlock = errorHandlingBlock;
            JavaScriptModuleFactory = javaScriptModuleFactory;
            AuthorizationRetryPipelineResourcePaths = authorizationRetryPipelineResourcePaths ?? NoRetryPipelines;
        }

        public Options Options { get; set; }
        public AuthorizationFailureHandling[] AuthorizationFailureHandling { get; set; }
        public ITargetBlock<ErrorItemMessage> ErrorHandlingBlock { get; set; }
        public Func<string> JavaScriptModuleFactory { get; }

        /// <summary>
        /// Resource paths (e.g. "/ed-fi/students") that have an authorization-retry ("#Retry") pipeline in
        /// the current run, which re-publishes the entire resource after its update prerequisites complete.
        /// Lets a processing block decide whether a Forbidden response for an item of a given resource can be
        /// deferred to that retry pass -- including for a resource other than the one it is processing, such
        /// as a missing dependency fetched from the source and posted on behalf of the current item.
        /// </summary>
        public IReadOnlySet<string> AuthorizationRetryPipelineResourcePaths { get; }
    }
}
