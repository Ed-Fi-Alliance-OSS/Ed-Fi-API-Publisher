namespace EdFi.Tools.ApiPublisher.Core.Configuration
{
    public interface IChangeProcessorConfigurationProvider
    {
        ChangeProcessorRuntimeConfiguration GetRuntimeConfiguration(string[] commandLineArgs);
    }
}