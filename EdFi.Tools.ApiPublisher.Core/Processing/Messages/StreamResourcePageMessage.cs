using System;
using System.Threading;
using EdFi.Tools.ApiPublisher.Core.ApiClientManagement;
using Newtonsoft.Json.Linq;

namespace EdFi.Tools.ApiPublisher.Core.Processing.Messages
{
    public class StreamResourcePageMessage<TItemActionMessage>
    {
        public EdFiApiClient EdFiApiClient { get; set; }
        public string ResourceUrl { get; set; }
        public long Offset { get; set; }
        public int Limit { get; set; }
        public bool IsFinalPage { get; set; }

        public ChangeWindow ChangeWindow { get; set; }

        public Func<StreamResourcePageMessage<TItemActionMessage>, JObject, TItemActionMessage> CreateItemActionMessage { get; set; }
        
        public CancellationTokenSource CancellationSource { get; set; }
        public Action<object> PostAuthorizationFailureRetry { get; set; }
    }
}