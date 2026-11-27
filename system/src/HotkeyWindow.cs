using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace DSHVoiceSystem
{
    /// <summary>Hidden native window that owns the system-wide hotkey.</summary>
    public sealed class HotkeyWindow : NativeWindow, IDisposable
    {
        private const int WM_HOTKEY = 0x0312;
        private const int MOD_ALT = 0x0001;
        private const int MOD_CONTROL = 0x0002;
        private const int MOD_NOREPEAT = 0x4000;
        private const int VK_SPACE = 0x20;
        private const int HotkeyId = 0xD5A1;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, int fsModifiers, int vk);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        private readonly Action _pressed;
        private bool _registered;

        public HotkeyWindow(Action pressed)
        {
            _pressed = pressed;
            CreateHandle(new CreateParams());
            if (!RegisterHotKey(Handle, HotkeyId, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, VK_SPACE))
            {
                int error = Marshal.GetLastWin32Error();
                DestroyHandle();
                throw new InvalidOperationException("无法注册全局热键 Ctrl+Alt+Space（可能被其他程序占用，错误 " + error + "）。");
            }
            _registered = true;
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_HOTKEY && m.WParam.ToInt32() == HotkeyId)
            {
                try { _pressed(); } catch (Exception ex) { Log.Write("hotkey: " + ex.Message); }
                return;
            }
            base.WndProc(ref m);
        }

        public void Dispose()
        {
            if (_registered)
            {
                try { UnregisterHotKey(Handle, HotkeyId); } catch { }
                _registered = false;
            }
            if (Handle != IntPtr.Zero) DestroyHandle();
        }
    }
}
