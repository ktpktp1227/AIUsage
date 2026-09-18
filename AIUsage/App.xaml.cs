using System.Threading;
using System.Windows;

namespace AIUsage;

public partial class App : Application
{
    private const string MutexName = @"Local\AIUsage";
    public const string ShowEventName = @"Local\AIUsage.Show";

    private static Mutex? _mutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        _mutex = new Mutex(true, MutexName, out bool created);
        if (!created)
        {
            // 이미 실행 중이면 숨어 있던 창을 다시 보이게 하고 종료
            try
            {
                using var showEvent = EventWaitHandle.OpenExisting(ShowEventName);
                showEvent.Set();
            }
            catch
            {
                // 기존 인스턴스가 종료되는 중일 수 있음
            }
            Shutdown();
            return;
        }

        base.OnStartup(e);
        new MainWindow().Show();
    }
}
