using System.Threading;
using Newtonsoft.Json.Linq;

namespace EdFi.Tools.ApiPublisher.Core.Processing.Messages
{
    public class GetItemForDeletionMessage
    {
        /// <summary>
        /// Gets or sets the relative URL for the resource to be deleted.
        /// </summary>
        public string ResourceUrl { get; set; }
        
        /// <summary>
        /// Gets or sets the natural key values for the resource to be deleted on the target.
        /// </summary>
        public JToken KeyValues { get; set; }
        
        /// <summary>
        /// Gets or sets the source API's resource identifier for the resource that was deleted.
        /// </summary>
        /// <remarks>This is captured for informational purposes only.</remarks>
        public string Id { get; set; }

        /// <summary>
        /// Gets or sets the cancellation token indicating whether delete processing should proceed.
        /// </summary>
        public CancellationToken CancellationToken { get; set; }
    }
}