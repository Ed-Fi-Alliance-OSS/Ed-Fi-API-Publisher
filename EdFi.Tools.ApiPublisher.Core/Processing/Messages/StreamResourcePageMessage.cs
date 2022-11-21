using System;
using System.Collections.Generic;
using System.Threading;
using EdFi.Tools.ApiPublisher.Core.ApiClientManagement;
using Newtonsoft.Json.Linq;

namespace EdFi.Tools.ApiPublisher.Core.Processing.Messages
{
    /// <summary>
    /// Represents details needed for obtaining a page of JSON data from the source connection.
    /// </summary>
    public class StreamResourcePageMessage<TProcessDataMessage>
    {
        // ----------------------------
        // Resource-specific context
        // ----------------------------
        public string ResourceUrl { get; set; }

        // Source Ed-Fi ODS API processing context (resource-specific)
        // Question: Is this just a pass-through from the top-level StreamResourceMessage?
        public Action<object> PostAuthorizationFailureRetry { get; set; }

        // -------------------------------
        // Paging-strategy specific context
        // --------------------------------
        public long Offset { get; set; }
        public int Limit { get; set; }
        public bool IsFinalPage { get; set; }
        
        // -------------------------------------------------
        // Source Ed-Fi ODS API processing context (shared)
        // -------------------------------------------------
        // public EdFiApiClient EdFiApiClient { get; set; }

        // ----------------------------
        // Global processing context
        // ----------------------------
        public ChangeWindow? ChangeWindow { get; set; }
        public CancellationTokenSource CancellationSource { get; set; }

        // TODO: GKM - Need to eliminate use of JObject in signature of this factory method -- needs proper abstractions
        public Func<StreamResourcePageMessage<TProcessDataMessage>, string, IEnumerable<TProcessDataMessage>> CreateProcessDataMessages { get; set; }
    }
}