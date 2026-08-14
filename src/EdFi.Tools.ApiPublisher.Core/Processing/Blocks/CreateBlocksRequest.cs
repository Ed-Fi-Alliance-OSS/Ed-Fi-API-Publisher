// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.Tools.ApiPublisher.Core.Configuration;
using EdFi.Tools.ApiPublisher.Core.Processing.Messages;
using System;
using System.Threading.Tasks.Dataflow;

namespace EdFi.Tools.ApiPublisher.Core.Processing.Blocks
{
    public class CreateBlocksRequest
    {
        public CreateBlocksRequest(
            Options options,
            AuthorizationFailureHandling[] authorizationFailureHandling,
            ITargetBlock<ErrorItemMessage> errorHandlingBlock,
            Func<string> javaScriptModuleFactory)
        {
            Options = options;
            AuthorizationFailureHandling = authorizationFailureHandling;
            ErrorHandlingBlock = errorHandlingBlock;
            JavaScriptModuleFactory = javaScriptModuleFactory;
        }

        public Options Options { get; set; }
        public AuthorizationFailureHandling[] AuthorizationFailureHandling { get; set; }
        public ITargetBlock<ErrorItemMessage> ErrorHandlingBlock { get; set; }
        public Func<string> JavaScriptModuleFactory { get; }

        /// <summary>
        /// Indicates the blocks are for an authorization-retry pseudo-resource ("#Retry"). Retry pipelines
        /// receive their items through a synchronous <c>Post</c> from the main processing pipeline that would
        /// silently drop messages if the target block declined them, so they must remain unbounded (see APIPUB-112).
        /// </summary>
        public bool IsRetryPipeline { get; set; }
    }
}
