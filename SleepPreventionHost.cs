using System;
using System.Drawing;
using System.Windows.Forms;

internal sealed partial class MainWindow
{
    readonly SleepPrevention sleepPrevention = new SleepPrevention();
    readonly Label sleepStatus = new Label();

    void StartMouseLoop()
    {
        if (mouseBookingTimer == null)
        {
            mouseBookingTimer = new Timer { Interval = 500 };
            mouseBookingTimer.Tick += async delegate { await TickMouseBooking(); };
        }
        // All fallible startup work precedes the generation increment, so
        // each caller's existing catch path can release a partial start.
        sleepPrevention.Start();
        ShowSleepStatus(false);
        BeginAutomationInputWatch();
        mouseLastInput = NativeMouseInput.ForeignInputVersion();
        mouseBookingTimer.Start();
        mouseBookingActive = true;
        mouseGeneration++;
    }

    void ReleaseSleepPrevention()
    {
        bool released = sleepPrevention.TryStop();
        ShowSleepStatus(!released);
    }

    void ShowSleepStatus(bool releaseFailed)
    {
        sleepStatus.ForeColor = releaseFailed ? Color.Firebrick : Color.FromArgb(18, 60, 83);
        sleepStatus.Text = releaseFailed
            ? "절전 방지 해제를 확인하지 못했습니다. 작업을 마쳤으면 앱을 종료하세요."
            : sleepPrevention.Active
                ? "절전 방지 켜짐 · 자동 실행 중 유휴 절전·화면 꺼짐 방지"
                : "절전 방지 꺼짐 · 마우스 자동 조회·예약 실행 시 자동으로 켜집니다.";
    }
}
