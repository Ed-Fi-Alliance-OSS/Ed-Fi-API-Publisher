using System;
using System.Net;
using Newtonsoft.Json.Linq;

namespace EdFi.Tools.ApiPublisher.Core.Processing.Messages
{
    public class ErrorItemMessage
    {
        public ErrorItemMessage()
        {
            DateTime = DateTime.UtcNow;
        }

        public DateTime DateTime { get; }

        public string Method { get; set; }
        
        public string ResourceUrl { get; set; }
        
        public string Id { get; set; }

        //[JsonIgnore]
        public JObject Body { get; set; }

        public HttpStatusCode? ResponseStatus { get; set; }
        
        public string ResponseContent { get; set; }
    }
}