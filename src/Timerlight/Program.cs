namespace Timerlight;

internal static class Program
{
    // Per-session name: every logged-in user may run their own copy.
    private const string InstanceMutexName = @"Local\Timerlight.SingleInstance.6f3a4f1c";

    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();

        using var instanceLock = new Mutex(initiallyOwned: true, InstanceMutexName, out bool isOnlyInstance);
        if (!isOnlyInstance)
        {
            MessageBox.Show(
                "Timerlight уже запущен — значок ищите в области уведомлений.",
                "Timerlight",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        Application.Run(new TrayApplicationContext());

        // Keeps the mutex owned for the whole lifetime of the message loop.
        GC.KeepAlive(instanceLock);
    }
}
