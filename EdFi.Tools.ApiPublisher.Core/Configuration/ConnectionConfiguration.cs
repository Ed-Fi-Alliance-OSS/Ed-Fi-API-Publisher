using System.Collections.Generic;

namespace EdFi.Tools.ApiPublisher.Core.Configuration
{
    public class ConnectionConfiguration
    {
        public Connections Connections { get; set; }
    }
    
    public class Connections
    {
        public NamedConnectionDetails Source { get; set; }
        public NamedConnectionDetails Target { get; set; }
    }

    /// <summary>
    /// Defines properties and behaviors required for a source or target connection. 
    /// </summary>
    public interface INamedConnectionDetails
    {
        /// <summary>
        /// The name to be used for uniquely identifying external configuration information for the connection and/or to track
        /// the current state of change processing for specific source and target connections.
        /// </summary>
        string Name { get; set; }

        /// <summary>
        /// Indicates whether the named connection has been fully defined by initial configuration (or requires additional
        /// augmentation from and external configuration source).
        /// </summary>
        /// <returns></returns>
        bool IsFullyDefined();

        /// <summary>
        /// Indicates that the connection is named, and needs additional resolution to the configuration from an external source. 
        /// </summary>
        /// <returns><b>true</b> if the connection configuration needs additional information; otherwise <b>false</b>.</returns>
        public bool NeedsResolution();
    }

    public class NamedConnectionDetails : INamedConnectionDetails
    {
        public string? Name { get; set; }

        public virtual bool IsFullyDefined() => true;

        public virtual bool NeedsResolution() => false;
    }

    public interface ISourceConnectionDetails : INamedConnectionDetails
    {
        public bool? IgnoreIsolation { get; set; }

        IDictionary<string, long> LastChangeVersionProcessedByTargetName { get; }
        
        public string? Include { get; set; }
        
        public string? IncludeOnly { get; set; }
        
        public string? Exclude { get; set; }
        
        public string? ExcludeOnly { get; set; }
        
        /// <summary>
        /// Gets or sets the explicitly provided value to use for the last change version processed.
        /// </summary>
        // TODO: Should this property really be here? Perhaps it should be more of a contextual argument somewhere else.
        long? LastChangeVersionProcessed { get; set; }
    }
    
    public interface ITargetConnectionDetails : INamedConnectionDetails { }
}