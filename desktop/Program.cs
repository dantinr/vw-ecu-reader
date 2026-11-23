namespace VwEcuReader;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new Dashboard(new SampleStore(SampleStore.DefaultPath)));
    }
}
