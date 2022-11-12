using System;
using System.Threading.Tasks.Dataflow;
using EdFi.Tools.ApiPublisher.Core.ApiClientManagement;
using EdFi.Tools.ApiPublisher.Core.Configuration;
using EdFi.Tools.ApiPublisher.Core.Processing.Messages;

namespace EdFi.Tools.ApiPublisher.Core.Processing.Blocks
{
    public class CreateBlocksRequest
    {
        public CreateBlocksRequest(
            // EdFiApiClient sourceApiClient,
            // EdFiApiClient targetApiClient,
            Options options,
            AuthorizationFailureHandling[] authorizationFailureHandling,
            ITargetBlock<ErrorItemMessage> errorHandlingBlock,
            Func<string>? javaScriptModuleFactory)
        {
            // SourceApiClient = sourceApiClient;
            // TargetApiClient = targetApiClient;
            Options = options;
            AuthorizationFailureHandling = authorizationFailureHandling;
            ErrorHandlingBlock = errorHandlingBlock;
            JavaScriptModuleFactory = javaScriptModuleFactory;
        }

        // public EdFiApiClient SourceApiClient { get; set; }
        // public EdFiApiClient TargetApiClient { get; set; }
        public Options Options { get; set; }
        public AuthorizationFailureHandling[] AuthorizationFailureHandling { get; set; }
        public ITargetBlock<ErrorItemMessage> ErrorHandlingBlock { get; set; }
        public Func<string>? JavaScriptModuleFactory { get; }
    }
}