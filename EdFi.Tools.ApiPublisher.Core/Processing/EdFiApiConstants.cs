namespace EdFi.Tools.ApiPublisher.Core.Processing
{
    public static class EdFiApiConstants
    {
        /// <summary>
        /// Gets the path suffix to the "deletes" child resource under each of the data management API's resources.
        /// </summary>
        public const string DeletesPathSuffix = "/deletes";
        
        /// <summary>
        /// Gets the path segment to the data management API, including the version.
        /// </summary>
        public const string DataManagementApiSegment = "data/v3";
        
        /// <summary>
        /// Gets the name of the property which holds the natural key values for a deleted item (if supported by the source API).
        /// </summary>
        public const string KeyValuesPropertyName = "keyValues";
    }
}