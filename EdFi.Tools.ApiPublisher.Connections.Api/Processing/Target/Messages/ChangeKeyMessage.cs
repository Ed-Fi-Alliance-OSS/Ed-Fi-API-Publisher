namespace EdFi.Tools.ApiPublisher.Connections.Api.Processing.Target.Messages
{
    public class ChangeKeyMessage
    {
        /// <summary>
        /// Gets or sets the relative URL for the resource whose key is to be changed.
        /// </summary>
        public string ResourceUrl { get; set; }

        /// <summary>
        /// Gets or sets the target API's resource identifier for the resource whose key is to be changed.
        /// </summary>
        public string Id { get; set; }

        /// <summary>
        /// Gets or sets the target API's existing resource body with the new key values applied -- to be PUT against the target API.
        /// </summary>
        public string Body { get; set; }
        
        /// <summary>
        /// Gets or sets the source API's resource identifier for the resource whose key was changed (primarily for correlating activity in log messages).
        /// </summary>
        public string SourceId { get; set; }
    }
}