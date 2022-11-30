namespace EdFi.Tools.ApiPublisher.Core.Extensions;

public static class NullableBooleanExtensions
{
    public static bool IsTrue(this bool? value)
    {
        return (value == true);
    }

    public static bool IsNotTrue(this bool? value)
    {
        return value != true;
    }
    
    public static bool IsFalse(this bool? value)
    {
        return (value == false);
    }

    public static bool IsNotFalse(this bool? value)
    {
        return value != false;
    }
}
