using System;
using System.Threading.Tasks.Dataflow;
using EdFi.Tools.ApiPublisher.Core.Processing.Messages;

namespace EdFi.Tools.ApiPublisher.Core.Processing
{
    public class StreamingPagesItem
    {
        public string[] DependencyPaths { get; set; }
        public ISourceBlock<ErrorItemMessage> CompletionBlock { get; set; }
    }
}