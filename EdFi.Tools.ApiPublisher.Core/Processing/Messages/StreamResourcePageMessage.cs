using System;
using System.Threading;
using EdFi.Tools.ApiPublisher.Core.ApiClientManagement;
using Newtonsoft.Json.Linq;

namespace EdFi.Tools.ApiPublisher.Core.Processing.Messages
{
    public class StreamResourcePageMessage<TItemActionMessage>
    {
        // ----------------------------
        // Resource-specific context
        // ----------------------------
        public string ResourceUrl { get; set; }

        // Source Ed-Fi ODS API processing context (resource-specific)
        // Question: Is this just a pass-through from the top-level StreamResourceMessage?
        public Action<object> PostAuthorizationFailureRetry { get; set; }

        // -------------------------------
        // Page-strategy specific context
        // --------------------------------
        public long Offset { get; set; }
        public int Limit { get; set; }
        public bool IsFinalPage { get; set; }
        
        // -------------------------------------------------
        // Source Ed-Fi ODS API processing context (shared)
        // -------------------------------------------------
        public EdFiApiClient EdFiApiClient { get; set; }

        // ----------------------------
        // Global processing context
        // ----------------------------
        public ChangeWindow ChangeWindow { get; set; }
        public CancellationTokenSource CancellationSource { get; set; }

        public Func<StreamResourcePageMessage<TItemActionMessage>, JObject, TItemActionMessage> CreateItemActionMessage { get; set; }
    }
}