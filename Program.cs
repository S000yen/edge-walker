namespace EdgeWalker;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        // 二重起動を防ぐ
        using var mutex = new Mutex(true, @"Local\EdgeWalker.Single", out bool created);
        if (!created) return;

        ApplicationConfiguration.Initialize();
        Application.Run(new WalkerApp());
    }
}
