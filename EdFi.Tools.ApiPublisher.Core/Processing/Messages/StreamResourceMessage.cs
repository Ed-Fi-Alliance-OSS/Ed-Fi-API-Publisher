using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace EdFi.Tools.ApiPublisher.Core.Processing.Messages
{
    public class StreamResourceMessage
    {
        public HttpClient HttpClient { get; set; }
        public string ResourceUrl { get; set; }
        public Task[] Dependencies { get; set; }

        public string[] DependencyPaths { get; set; }
        public int PageSize { get; set; }
        
        public ChangeWindow ChangeWindow { get; set; }
        
        public CancellationTokenSource CancellationSource { get; set; }

        public Action<object> PostAuthorizationFailureRetry { get; set; }
    }
}