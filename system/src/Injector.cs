using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace DSHVoiceSystem
{
    /// <summary>
    /// Types the final text into whatever window currently has focus. Uses
    /// Unicode SendInput so Chinese goes in directly (no IME switch), and keeps
    /// the clipboard untouched unless the fallback is explicitly used.
    /// </summary>
    public static class Injector
    {
        private const uint INPUT_KEYBOARD = 1;
        private const uint KEYEVENTF_KEYUP = 0x0002;
        private const uint KEYEVENTF_UNICODE = 0x0004;

        [StructLayout(LayoutKind.Sequential)]
        private struct INPUT
        {
            public uint type;
            public InputUnion U;
        }

        [StructLayout(LayoutKind.Explicit, Size = 32)]
        private struct InputUnion
        {
            [FieldOffset(0)] public KEYBDINPUT ki;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct KEYBDINPUT
        {
            public ushort wVk;
            public ushort wScan;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint numberOfInputs, INPUT[] inputs, int sizeOfInputStructure);

        public static string LastText { get; private set; }

        public static int StructureSize
        {
            get { return Marshal.SizeOf(typeof(INPUT)); }
        }

        public static void TypeText(string text)
        {
            if (text == null) text = "";
            LastText = text;
            text = text.Replace("\r\n", "\n").Replace('\r', '\n');
            int sent = 0;
            foreach (char ch in text)
            {
                if (ch == '\n')
                {
                    SendKey(0x0D);
                }
                else if (ch == '\t')
                {
                    SendKey(0x09);
                }
                else
                {
                    SendUnicode(ch);
                }
                sent++;
                if (sent % 64 == 0) Thread.Sleep(2);
            }
        }

        public static void PasteText(string text)
        {
            LastText = text;
            string previous = null;
            bool hadText = false;
            try
            {
                if (Clipboard.ContainsText())
                {
                    previous = Clipboard.GetText();
                    hadText = true;
                }
            }
            catch
            {
            }
            try
            {
                Clipboard.SetText(text == null ? "" : text);
                Thread.Sleep(60);
                SendKeyDown(0x11); // Ctrl
                SendKeyDown(0x56); // V
                SendKeyUp(0x56);
                SendKeyUp(0x11);
                Thread.Sleep(180);
            }
            finally
            {
                try
                {
                    if (hadText) Clipboard.SetText(previous);
                }
                catch
                {
                }
            }
        }

        private static void SendUnicode(char ch)
        {
            INPUT down = new INPUT();
            down.type = INPUT_KEYBOARD;
            down.U.ki.wScan = ch;
            down.U.ki.dwFlags = KEYEVENTF_UNICODE;
            INPUT up = new INPUT();
            up.type = INPUT_KEYBOARD;
            up.U.ki.wScan = ch;
            up.U.ki.dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP;
            Send(new INPUT[] { down, up });
        }

        private static void SendKey(ushort virtualKey)
        {
            SendKeyDown(virtualKey);
            SendKeyUp(virtualKey);
        }

        private static void SendKeyDown(ushort virtualKey)
        {
            INPUT input = new INPUT();
            input.type = INPUT_KEYBOARD;
            input.U.ki.wVk = virtualKey;
            Send(new INPUT[] { input });
        }

        private static void SendKeyUp(ushort virtualKey)
        {
            INPUT input = new INPUT();
            input.type = INPUT_KEYBOARD;
            input.U.ki.wVk = virtualKey;
            input.U.ki.dwFlags = KEYEVENTF_KEYUP;
            Send(new INPUT[] { input });
        }

        private static void Send(INPUT[] inputs)
        {
            int size = Marshal.SizeOf(typeof(INPUT));
            uint result = SendInput((uint)inputs.Length, inputs, size);
            if (result != inputs.Length)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "无法把文字输入到当前窗口（可能前台窗口以管理员权限运行）。");
        }
    }
}
