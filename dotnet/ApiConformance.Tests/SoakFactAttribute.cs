public sealed class SoakFactAttribute : FactAttribute
{
    public SoakFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("SOAK") != "1") Skip = "Soak test: set SOAK=1 to run.";
    }
}
