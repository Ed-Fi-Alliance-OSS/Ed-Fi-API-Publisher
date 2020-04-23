using System;
using Newtonsoft.Json.Linq;

namespace EdFi.Tools.ApiPublisher.Core.Processing.Messages
{
    public class PostItemMessage
    {
        public string ResourceUrl { get; set; }
        
        public JObject Item { get; set; }
        
        public Action<object> PostAuthorizationFailureRetry { get; set; }
    }
}