using Amazon.Extensions.NETCore.Setup;

namespace EdFi.Tools.ApiPublisher.Configuration.Aws
{
    public interface IAwsOptionsProvider
    {
        AWSOptions GetOptions();
    }
}