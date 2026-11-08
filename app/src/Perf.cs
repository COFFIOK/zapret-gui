// Zapret (C#) — экономия ресурсов: в фоне приложение почти ничего не делает и почти не занимает памяти
using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;

namespace ZapretCS
{
    partial class MainUi
    {
        DispatcherTimer idleGlowTimer, trimTimer;

        [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll")] static extern bool SetProcessWorkingSetSize(IntPtr proc, IntPtr min, IntPtr max);

        void WirePerf()
        {
            // окно не в фокусе дольше 20 с — останавливаем бесконечные анимации (свечение, блик на букве)
            idleGlowTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
            idleGlowTimer.Tick += (s, e) => { idleGlowTimer.Stop(); if (!Win.IsActive) PauseGlow(); };
            Win.Deactivated += (s, e) => idleGlowTimer.Start();
            Win.Activated += (s, e) => { idleGlowTimer.Stop(); if (Win.IsVisible && Win.WindowState != WindowState.Minimized) ResumeGlow(); };

            // свернули в трей или в панель задач — гасим анимации и отдаём лишнюю память системе
            trimTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
            trimTimer.Tick += (s, e) => { trimTimer.Stop(); if (!Win.IsVisible || Win.WindowState == WindowState.Minimized) TrimMemory(); };
            Win.IsVisibleChanged += (s, e) =>
            {
                if (!Win.IsVisible)
                {
                    // таймер состояния не трогаем: из трея можно включить zapret, и окно должно это заметить
                    PauseGlow();
                    Mon.Idle = true;
                    trimTimer.Stop(); trimTimer.Start();
                }
                else Mon.Wake();
            };
            Win.StateChanged += (s, e) => { if (Win.WindowState == WindowState.Minimized) { trimTimer.Stop(); trimTimer.Start(); } };
        }

        static void TrimMemory()
        {
            try
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                SetProcessWorkingSetSize(GetCurrentProcess(), (IntPtr)(-1), (IntPtr)(-1));
            }
            catch { }
        }
    }
}
