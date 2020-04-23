namespace EdFi.Tools.ApiPublisher.Core.Configuration
{
    public class PublisherSettings
    {
        public Options Options { get; set; }

        public AuthorizationFailureHandling[] AuthorizationFailureHandling { get; set; }
    }

    public class AuthorizationFailureHandling
    {
        public string Path { get; set; }
        public string[] UpdatePrerequisitePaths { get; set; }
    }
    
    public class Options
    {
        public int BearerTokenRefreshMinutes { get; set; } = 12;
        
        public int RetryStartingDelayMilliseconds { get; set; } = 250;
        
        public int MaxRetryAttempts { get; set; } = 5;

        public int MaxDegreeOfParallelismForPostResourceItem { get; set; } = 200;

        public int MaxDegreeOfParallelismForStreamResourcePages { get; set; } = 10;

        public int StreamingPagesWaitDurationSeconds { get; set; } = 10;

        public int StreamingPageSize { get; set; } = 75;

        public bool IncludeDescriptors { get; set; } = false;
        
        public bool WhatIf { get; set; } = false;
        
        public int ErrorPublishingBatchSize { get; set; } = 25;

        public bool IgnoreSSLErrors { get; set; } = false;
    }
}