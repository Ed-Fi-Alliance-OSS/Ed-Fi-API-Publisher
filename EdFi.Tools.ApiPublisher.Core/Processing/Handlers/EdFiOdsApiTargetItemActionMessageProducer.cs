using System.Collections.Generic;
using System.Linq;
using EdFi.Tools.ApiPublisher.Core.Processing.Messages;
using log4net;
using Newtonsoft.Json.Linq;

namespace EdFi.Tools.ApiPublisher.Core.Processing.Handlers;

/// <summary>
/// Implements an item action producer that parses the JSON content and creates "ItemActionMessage" instances with a JsonObject
/// for each resource item.
/// </summary>
public class EdFiOdsApiTargetItemActionMessageProducer : IItemActionMessageProducer
{
    private readonly ILog _logger = LogManager.GetLogger(typeof(EdFiOdsApiTargetItemActionMessageProducer));
    
    public IEnumerable<TItemActionMessage> ProduceMessages<TItemActionMessage>(
        string responseContent,
        StreamResourcePageMessage<TItemActionMessage> pageMessage)
    {
        JArray items = JArray.Parse(responseContent);

        // Iterate through the returned items
        foreach (var item in items.OfType<JObject>())
        {
            var actionMessage = pageMessage.CreateItemActionMessage(pageMessage, item);

            // Stop processing individual items if cancellation has been requested
            if (pageMessage.CancellationSource.IsCancellationRequested)
            {
                _logger.Debug(
                    $"{pageMessage.ResourceUrl}: Cancellation requested during item '{typeof(TItemActionMessage).Name}' creation.");

                yield break;
            }

            // Add the item to the buffer for processing into the target API
            if (_logger.IsDebugEnabled)
            {
                _logger.Debug(
                    $"{pageMessage.ResourceUrl}: Adding individual action message of type '{typeof(TItemActionMessage).Name}' for item {item["id"].Value<string>()}...");
            }

            yield return actionMessage;
        }
    }
}