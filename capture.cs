using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Drawing;
using System.Drawing.Imaging;
using System.Threading;

class Capture {
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd, out RECT lpRect);
    [DllImport("user32.dll")] static extern bool PrintWindow(IntPtr hwnd, IntPtr hdcBlt, uint nFlags);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int nWidth, int nHeight);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr hdc, IntPtr hgdiobj);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr hObject);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr hdc);
    [DllImport("user32.dll")] static extern IntPtr GetWindowDC(IntPtr hwnd);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
    
    [StructLayout(LayoutKind.Sequential)] 
    public struct RECT { 
        public int Left; 
        public int Top; 
        public int Right; 
        public int Bottom; 
    }
    
    public const int SW_RESTORE = 9;
    
    static void Main(string[] args) {
        if (args.Length < 2) {
            Console.WriteLine("Usage: capture <processName> <outputPath>");
            return;
        }
        
        var procs = Process.GetProcessesByName(args[0]);
        if (procs.Length == 0) { 
            Console.WriteLine("Process not found: " + args[0]); 
            return; 
        }
        
        var hwnd = procs[0].MainWindowHandle;
        ShowWindow(hwnd, SW_RESTORE);
        Thread.Sleep(500);
        SetForegroundWindow(hwnd);
        Thread.Sleep(500);
        
        GetWindowRect(hwnd, out RECT rc);
        int w = rc.Right - rc.Left;
        int h = rc.Bottom - rc.Top;
        
        IntPtr hdcScreen = GetWindowDC(hwnd);
        IntPtr hdcMem = CreateCompatibleDC(hdcScreen);
        IntPtr hBitmap = CreateCompatibleBitmap(hdcScreen, w, h);
        SelectObject(hdcMem, hBitmap);
        PrintWindow(hwnd, hdcMem, 0);
        
        using (Bitmap bmp = Bitmap.FromHbitmap(hBitmap)) {
            bmp.Save(args[1], ImageFormat.Png);
        }
        
        DeleteObject(hBitmap);
        DeleteDC(hdcMem);
        ReleaseDC(hwnd, hdcScreen);
        Console.WriteLine("Screenshot saved to: " + args[1]);
    }
}
