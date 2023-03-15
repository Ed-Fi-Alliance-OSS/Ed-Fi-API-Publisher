namespace EdFi.Tools.ApiPublisher.Connections.Sqlite.Processing.Target.Messages;

public abstract class ResourceJsonMessage
{
    /// <summary>
    /// Gets or sets the relative URL for the resource associated with the data.
    /// </summary>
    public string ResourceUrl { get; set; }

    /// <summary>
    /// Get or sets the JSON to be stored in the Sqlite database.
    /// </summary>
    public string Json { get; set; }
}

public class KeyChangesJsonMessage : ResourceJsonMessage {}
public class UpsertsJsonMessage : ResourceJsonMessage {}
public class DeletesJsonMessage : ResourceJsonMessage {}