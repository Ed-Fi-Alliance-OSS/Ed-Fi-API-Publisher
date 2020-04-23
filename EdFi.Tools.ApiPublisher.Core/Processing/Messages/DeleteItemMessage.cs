namespace EdFi.Tools.ApiPublisher.Core.Processing.Messages
{
    public class DeleteItemMessage
    {
        /// <summary>
        /// Gets or sets the relative URL for the resource to be deleted.
        /// </summary>
        public string ResourceUrl { get; set; }

        /// <summary>
        /// Gets or sets the target API's resource identifier for the resource to be deleted.
        /// </summary>
        public string Id { get; set; }

        /// <summary>
        /// Gets or sets the source API's resource identifier for the resource that was deleted (primarily for correlating activity in log messages).
        /// </summary>
        public string SourceId { get; set; }
    }
}