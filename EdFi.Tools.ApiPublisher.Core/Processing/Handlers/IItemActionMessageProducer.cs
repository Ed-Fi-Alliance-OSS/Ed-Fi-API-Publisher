using System.Collections.Generic;
using EdFi.Tools.ApiPublisher.Core.Processing.Messages;

namespace EdFi.Tools.ApiPublisher.Core.Processing.Handlers;

public interface IItemActionMessageProducer
{
    IEnumerable<TItemActionMessage> ProduceMessages<TItemActionMessage>(
        string responseContent,
        StreamResourcePageMessage<TItemActionMessage> pageMessage);
}